using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using CampusPortalAutoLogin.Core.Models;
using CampusPortalAutoLogin.Core.Network;

namespace CampusPortalAutoLogin.Core.Protocol;

public sealed class EportalLoginFlow : ILoginFlow
{
    private readonly ILogSink log;
    private readonly Func<HttpMessageHandler> handlerFactory;
    private readonly IReadOnlyList<Uri> probeUris;

    public EportalLoginFlow(ILogSink log) : this(log, PortalProbeService.CreateDefaultHandler, null) { }

    public EportalLoginFlow(ILogSink log, Func<HttpMessageHandler> handlerFactory, IReadOnlyList<Uri>? probeUris = null)
    {
        this.log = log;
        this.handlerFactory = handlerFactory;
        this.probeUris = probeUris ??
        [
            new("http://www.msftconnecttest.com/connecttest.txt"), new("http://www.msftncsi.com/ncsi.txt"),
            new("http://connect.rom.miui.com/generate_204"), new("http://detectportal.firefox.com/canonical.html")
        ];
    }

    public async Task<LoginResult> ExecuteAsync(LoginRequest request, int timeoutSeconds, IProgress<LoginProgress>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password)) return new(LoginState.Failed, "用户名和密码不能为空。", false);
        if (timeoutSeconds <= 0) return new(LoginState.Failed, "请求超时参数无效。", false);
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        using var client = new HttpClient(handlerFactory(), true) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            progress?.Report(new(LoginState.Probing, "正在探测网络。"));
            var probe = await PortalProbeService.ProbeAsync(client, probeUris, timeout, cancellationToken,
                (uri, status, length) => log.Write(LogLevel.Info, "Probe", $"{OriginAndPath(uri)} status={(int)status} len={length}")).ConfigureAwait(false);
            log.Write(LogLevel.Info, "Probe", $"探测结果 state={probe.State}。");
            if (probe.State == ProbeState.Online) return new(LoginState.Online, "网络已经连通。");
            if (probe.State != ProbeState.CaptivePortal || probe.PortalUri is null)
                return new(probe.State == ProbeState.Offline ? LoginState.Offline : LoginState.Warning, probe.Message, probe.Retryable, probe.PortalUri);

            var portal = probe.PortalUri;
            log.Write(LogLevel.Info, "Probe", $"认证门户 {OriginAndPath(portal)}");
            progress?.Report(new(LoginState.LoggingIn, "正在读取认证门户。"));
            using var pageExchange = await GetFollowingRedirectsAsync(client, portal, timeout, cancellationToken).ConfigureAwait(false);
            var pageResponse = pageExchange.Response;
            {
                var finalUri = pageResponse.RequestMessage?.RequestUri ?? portal;
                var bytes = await PortalProbeService.ReadLimitedAsync(pageResponse.Content, pageExchange.Token).ConfigureAwait(false);
                LogHttp("bootstrap", pageResponse, bytes.Length);
                var page = PortalPageParser.Parse(bytes, pageResponse.Content.Headers.ContentType?.ToString(), finalUri);
                if (!page.IsVerifiedEportal) return new(LoginState.Warning, "门户未通过 ePortal 特征验证，已停止自动提交。", false, finalUri);

                var query = finalUri.Query.TrimStart('?');
                var interfaceUri = new Uri(finalUri, "InterFace.do");
                var pageInfoJson = await PostTextAsync(client, WithMethod(interfaceUri, "pageInfo"), "queryString=" + EportalEncoding.EncodeURIComponent(query), "pageInfo", timeout, cancellationToken).ConfigureAwait(false);
                page = PortalPageParser.ApplyPageInfo(page, pageInfoJson);
                if (!string.IsNullOrEmpty(page.ValidCodeUrl)) return new(LoginState.CaptchaRequired, "认证需要验证码，请在浏览器中人工完成。", false, finalUri);
                if (page.RequiresOperatorCredentials) return new(LoginState.Failed, "门户要求运营商附加账号或密码，需要人工认证。", false, finalUri);

                var servicesUri = new Uri(WithMethod(interfaceUri, "getServices").AbsoluteUri + "&queryString=" + EportalEncoding.EncodeURIComponent(query));
                var servicesJson = await PostTextAsync(client, servicesUri, "", "getServices", timeout, cancellationToken).ConfigureAwait(false);
                var service = ServiceSelector.Select(request.ServiceProvider, PortalPageParser.ParseServices(servicesJson));
                var password = page.PasswordEncrypt ? LegacyRsa.EncryptPassword(request.Password, page.PublicKeyExponent, page.PublicKeyModulus) : request.Password;
                var fields = new Dictionary<string, string>
                {
                    ["userId"] = EportalEncoding.DoubleEncode(request.Username),
                    ["password"] = EportalEncoding.DoubleEncode(password),
                    ["service"] = EportalEncoding.DoubleEncode(service), ["queryString"] = EportalEncoding.DoubleEncode(query),
                    ["operatorPwd"] = "", ["operatorUserId"] = "", ["validcode"] = "", ["passwordEncrypt"] = page.PasswordEncrypt ? "true" : "false"
                };
                // 当前门户脚本从 pageInfo 读取令牌，登录时只编码一次；旧门户保持原有字段。
                if (!string.IsNullOrEmpty(page.LoginToken)) fields["loginToken"] = EportalEncoding.EncodeURIComponent(page.LoginToken);
                var body = string.Join("&", fields.Select(p => p.Key + "=" + p.Value));
                var loginJson = await PostTextAsync(client, WithMethod(interfaceUri, "login"), body, "login", timeout, cancellationToken).ConfigureAwait(false);
                var result = LoginResponseClassifier.Classify(loginJson, finalUri);
                log.Write(result.IsSuccess ? LogLevel.Success : LogLevel.Warning, "Login", result.IsSuccess ? "ePortal 登录成功。" : "ePortal 登录未成功，已返回分类结果。");
                return result;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new(LoginState.Cancelled, "操作已取消。", false); }
        catch (OperationCanceledException) { return new(LoginState.Failed, "网络请求超时。", true); }
        catch (HttpRequestException) { return new(LoginState.Offline, "网络请求失败。", true); }
        catch (EmptyPortalResponseException e) { return new(LoginState.Failed, e.Message, true); }
        catch (InvalidDataException e) { return new(LoginState.Failed, e.Message, false); }
        catch (ArgumentException e) { return new(LoginState.Failed, e.Message, false); }
        catch (InvalidOperationException e) { return new(LoginState.Failed, e.Message, false); }
        catch (System.Text.Json.JsonException) { return new(LoginState.Failed, "认证接口返回了无法识别的数据。", false); }
    }

    private static Uri WithMethod(Uri interfaceUri, string method) => new(interfaceUri.AbsoluteUri + "?method=" + method);

    private static async Task<PortalProbeService.TimedResponse> GetFollowingRedirectsAsync(HttpClient client, Uri initial, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var current = initial;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var depth = 0; depth <= 5; depth++)
        {
            if (!visited.Add(current.AbsoluteUri)) throw new InvalidDataException("认证门户发生重定向循环。 ");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var exchange = await PortalProbeService.SendAsync(client, request, timeout, cancellationToken).ConfigureAwait(false);
            var response = exchange.Response;
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                exchange.Dispose();
                continue;
            }
            if (!response.IsSuccessStatusCode) { exchange.Dispose(); throw new HttpRequestException("认证门户不可用。 "); }
            return exchange;
        }
        throw new InvalidDataException("认证门户重定向过多。 ");
    }

    private async Task<string> PostTextAsync(HttpClient client, Uri uri, string body, string operation, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded") };
        using var exchange = await PortalProbeService.SendAsync(client, request, timeout, cancellationToken).ConfigureAwait(false);
        var response = exchange.Response;
        if ((int)response.StatusCode is >= 300 and < 400) throw new InvalidDataException("认证 POST 返回跳转，已拒绝继续。 ");
        if ((int)response.StatusCode >= 500) throw new HttpRequestException("认证服务器临时不可用。 ");
        if (!response.IsSuccessStatusCode) throw new InvalidDataException("认证接口拒绝请求。 ");
        var bytes = await PortalProbeService.ReadLimitedAsync(response.Content, exchange.Token).ConfigureAwait(false);
        LogHttp(operation, response, bytes.Length);
        if (bytes.Length == 0) throw new EmptyPortalResponseException($"{operation} 接口返回空正文，将有限重试；请确认校园网连接。");
        return Encoding.UTF8.GetString(bytes);
    }

    private void LogHttp(string operation, HttpResponseMessage response, int length) =>
        log.Write(LogLevel.Info, "Network", $"{operation} status={(int)response.StatusCode} len={length}");

    private static string OriginAndPath(Uri uri) => uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath;
    private sealed class EmptyPortalResponseException(string message) : IOException(message);
}
