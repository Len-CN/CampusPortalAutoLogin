using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using CampusPortalAutoLogin.Core.Protocol;

namespace CampusPortalAutoLogin.Core.Network;

public enum ProbeState { Online, CaptivePortal, Offline, UnknownPortal }
public sealed record ProbeResult(ProbeState State, Uri? PortalUri = null, string Message = "", bool Retryable = false);

public sealed class PortalProbeService
{
    private static readonly Uri[] DefaultUris =
    [
        new("http://www.msftconnecttest.com/connecttest.txt"),
        new("http://www.msftncsi.com/ncsi.txt"),
        new("http://connect.rom.miui.com/generate_204"),
        new("http://detectportal.firefox.com/canonical.html")
    ];
    private readonly Func<HttpMessageHandler> handlerFactory;
    private readonly IReadOnlyList<Uri> probeUris;
    private readonly TimeSpan requestTimeout;

    public PortalProbeService(Func<HttpMessageHandler>? handlerFactory = null, IReadOnlyList<Uri>? probeUris = null, TimeSpan? requestTimeout = null)
    {
        this.handlerFactory = handlerFactory ?? CreateDefaultHandler;
        this.probeUris = probeUris ?? DefaultUris;
        this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(12);
    }

    public async Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient(handlerFactory(), true) { Timeout = Timeout.InfiniteTimeSpan };
        return await ProbeAsync(client, probeUris, requestTimeout, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<ProbeResult> ProbeAsync(HttpClient client, IReadOnlyList<Uri> uris, TimeSpan timeout, CancellationToken cancellationToken, Action<Uri, HttpStatusCode, int>? telemetry = null)
    {
        var sawUnknown = false;
        var sawTransient = false;
        foreach (var uri in uris)
        {
            try
            {
                var result = await FollowProbeAsync(client, uri, timeout, cancellationToken, telemetry).ConfigureAwait(false);
                if (result.State is ProbeState.Online or ProbeState.CaptivePortal) return result;
                sawUnknown |= result.State == ProbeState.UnknownPortal;
                sawTransient |= result.Retryable;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { sawTransient = true; }
            catch (HttpRequestException) { sawTransient = true; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return sawUnknown ? new(ProbeState.UnknownPortal, Message: "探测响应不符合联网标记，且未验证为 ePortal。")
            : new(ProbeState.Offline, Message: "网络探测失败。", Retryable: sawTransient);
    }

    private static async Task<ProbeResult> FollowProbeAsync(HttpClient client, Uri initial, TimeSpan timeout, CancellationToken cancellationToken, Action<Uri, HttpStatusCode, int>? telemetry)
    {
        var current = initial;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var depth = 0; depth <= 5; depth++)
        {
            if (!visited.Add(current.AbsoluteUri)) return new(ProbeState.UnknownPortal, Message: "探测发生重定向循环。", Retryable: true);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var exchange = await SendAsync(client, request, timeout, cancellationToken).ConfigureAwait(false);
            var response = exchange.Response;
            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                telemetry?.Invoke(current, response.StatusCode, 0);
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (LooksLikeEportal(next)) return new(ProbeState.CaptivePortal, next, "发现 ePortal 认证门户。");
                current = next;
                continue;
            }
            if ((int)response.StatusCode >= 500) { telemetry?.Invoke(current, response.StatusCode, 0); return new(ProbeState.Offline, Message: "探测服务临时不可用。", Retryable: true); }
            var bytes = await ReadLimitedAsync(response.Content, exchange.Token).ConfigureAwait(false);
            telemetry?.Invoke(current, response.StatusCode, bytes.Length);
            var body = Encoding.UTF8.GetString(bytes);
            if (SameEndpoint(initial, current) && MatchesExpected(initial, response.StatusCode, body)) return new(ProbeState.Online, Message: "联网探测标记匹配。");
            if (response.IsSuccessStatusCode && ExtractPortalRedirect(current, body) is { } htmlPortal) return new(ProbeState.CaptivePortal, htmlPortal, "发现认证页跳转地址。");
            if (response.IsSuccessStatusCode && LooksLikeEportal(current, body)) return new(ProbeState.CaptivePortal, current, "发现 ePortal 认证页面。");
            if (response.IsSuccessStatusCode) return new(ProbeState.UnknownPortal, current, "HTTP 响应不符合联网标记。");
            return new(ProbeState.Offline, Message: "探测请求失败。", Retryable: true);
        }
        return new(ProbeState.UnknownPortal, Message: "探测重定向过多。", Retryable: true);
    }

    internal static async Task<TimedResponse> SendAsync(HttpClient client, HttpRequestMessage request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // 此 ePortal 根据浏览器标识生成页面/接口响应；缺失时可能返回 200 空正文。
        if (!request.Headers.UserAgent.Any())
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            return new TimedResponse(response, timeoutCts);
        }
        catch { timeoutCts.Dispose(); throw; }
    }

    internal static async Task<byte[]> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int limit = 1024 * 1024;
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("HTTP 响应超过允许大小。 ");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > limit) throw new InvalidDataException("HTTP 响应超过允许大小。 ");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    internal static HttpMessageHandler CreateDefaultHandler() => new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer(), UseCookies = true };
    private static bool SameEndpoint(Uri left, Uri right) => left.Scheme == right.Scheme && left.Host == right.Host && left.Port == right.Port && left.AbsolutePath == right.AbsolutePath;
    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
    private static bool LooksLikeEportal(Uri uri) => uri.AbsolutePath.Contains("eportal", StringComparison.OrdinalIgnoreCase);
    private static Uri? ExtractPortalRedirect(Uri current, string body)
    {
        // 只读取字面量，不执行门户返回的脚本。
        var script = Regex.Match(body, @"\blocation(?:\.href)?\s*=\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        var value = script.Success ? script.Groups[1].Value : Regex.Match(body, @"<meta\b[^>]*content\s*=\s*['""][^'""]*?url\s*=\s*([^'"">]+)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)).Groups[1].Value;
        if (!Uri.TryCreate(current, WebUtility.HtmlDecode(value.Trim()), out var target) || string.IsNullOrEmpty(value)) return null;
        return target.Scheme is "http" or "https" && target.UserInfo.Length == 0 && LooksLikeEportal(target) ? target : null;
    }
    private static bool LooksLikeEportal(Uri uri, string body) => LooksLikeEportal(uri) && body.Contains("AuthInterFace", StringComparison.OrdinalIgnoreCase) && body.Contains("InterFace.do", StringComparison.OrdinalIgnoreCase);
    private static bool MatchesExpected(Uri uri, HttpStatusCode status, string body)
    {
        if (uri.Host.Equals("www.msftconnecttest.com", StringComparison.OrdinalIgnoreCase)) return status == HttpStatusCode.OK && body == "Microsoft Connect Test";
        if (uri.Host.Equals("www.msftncsi.com", StringComparison.OrdinalIgnoreCase)) return status == HttpStatusCode.OK && body == "Microsoft NCSI";
        if (uri.Host.Equals("connect.rom.miui.com", StringComparison.OrdinalIgnoreCase)) return status == HttpStatusCode.NoContent;
        if (uri.Host.Equals("detectportal.firefox.com", StringComparison.OrdinalIgnoreCase)) return status == HttpStatusCode.OK && body == "success\n";
        return false;
    }

    internal sealed class TimedResponse(HttpResponseMessage response, CancellationTokenSource timeoutCts) : IDisposable
    {
        public HttpResponseMessage Response { get; } = response;
        public CancellationToken Token => timeoutCts.Token;
        public void Dispose() { Response.Dispose(); timeoutCts.Dispose(); }
    }
}
