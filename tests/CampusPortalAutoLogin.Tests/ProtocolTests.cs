using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using CampusPortalAutoLogin.Core.Models;
using CampusPortalAutoLogin.Core.Network;
using CampusPortalAutoLogin.Core.Protocol;

namespace CampusPortalAutoLogin.Tests;

public static class ProtocolTests
{
    [Case] public static void 页面元数据读取登录令牌且兼容旧门户()
    {
        var page = PortalPageParser.Parse(Encoding.UTF8.GetBytes("<script>AuthInterFace.init('./')</script>"), null, new Uri("http://portal.test/eportal/index.jsp"));
        var updated = PortalPageParser.ApplyPageInfo(page, "{\"loginToken\":\"session A+/%&=\"}");
        Check.Equal("session A+/%&=", updated.LoginToken);
        Check.Equal("", PortalPageParser.ApplyPageInfo(updated, "{}").LoginToken);
        Check.Equal("", PortalPageParser.ApplyPageInfo(updated, "{\"loginToken\":null}").LoginToken);
    }

    [Case] public static async Task 登录令牌单次编码且每轮认证读取新值()
    {
        var portal = new Uri("http://portal.test/eportal/index.jsp?wlanuserip=dynamic");
        var loginBodies = new List<string>();
        var log = new CollectLog();
        var metadataReads = 0;
        var flow = new EportalLoginFlow(log, () => new ScriptedHandler(async (request, token) =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "probe.test") return Response(HttpStatusCode.Redirect, "", uri, portal);
            if (uri.Query.Contains("method=pageInfo"))
                return Response(HttpStatusCode.OK, $"{{\"loginToken\":\"session{++metadataReads} A+/%&=\"}}", uri);
            if (uri.Query.Contains("method=getServices"))
                return Response(HttpStatusCode.OK, "{\"serviceJson\":[{\"serviceName\":\"campus\",\"serviceShowName\":\"校园网\"}]}", uri);
            if (uri.Query.Contains("method=login"))
            {
                var body = await request.Content!.ReadAsStringAsync(token);
                loginBodies.Add(body);
                // 模拟门户拒绝缺失、旧值或双重编码的令牌。
                var expected = $"loginToken=session{metadataReads}%20A%2B%2F%25%26%3D";
                return Response(HttpStatusCode.OK, body.Split('&').Contains(expected)
                    ? "{\"result\":\"success\"}" : "{\"result\":\"fail\",\"message\":\"page expired, please login again\"}", uri);
            }
            return Response(HttpStatusCode.OK, "<script>AuthInterFace.init('./')</script>", uri);
        }), [new Uri("http://probe.test/check")]);
        for (var attempt = 0; attempt < 2; attempt++)
            Check.Equal(LoginState.Success, (await flow.ExecuteAsync(new("u", "p", "campus"), 3, null, CancellationToken.None)).State);
        Check.Equal(2, metadataReads);
        Check.Equal(2, loginBodies.Count);
        Check.True(!loginBodies[1].Contains("session1"));
        Check.True(log.Messages.All(m => !m.Contains("session1") && !m.Contains("session2") && !m.Contains("loginToken")));
    }

    [Case] public static async Task 空元数据响应明确提示且不提交密码()
    {
        var portal = new Uri("http://portal.test/eportal/index.jsp?wlanuserip=dynamic");
        var requests = 0;
        var handler = new ScriptedHandler((request, _) => Task.FromResult(++requests switch
        {
            1 => Response(HttpStatusCode.Redirect, "", request.RequestUri!, portal),
            2 => Response(HttpStatusCode.OK, "<title>上网认证</title><script>AuthInterFace.init('./')</script>", request.RequestUri!),
            _ => Response(HttpStatusCode.OK, "", request.RequestUri!)
        }));
        var result = await new EportalLoginFlow(new NullLog(), () => handler, [new Uri("http://probe.test/check")])
            .ExecuteAsync(new("u", "p", "telecom"), 3, null, CancellationToken.None);
        Check.Equal(LoginState.Failed, result.State);
        Check.True(result.Retryable); Check.True(result.Message.Contains("pageInfo") && result.Message.Contains("空正文"));
        Check.Equal(3, requests);
    }
    [Case] public static async Task 门户要求UserAgent时仍可读取元数据并登录()
    {
        var portal = new Uri("http://portal.test/eportal/index.jsp?wlanuserip=dynamic");
        var handler = new ScriptedHandler((request, _) =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "probe.test") return Task.FromResult(Response(HttpStatusCode.Redirect, "", uri, portal));
            if (!request.Headers.UserAgent.Any()) return Task.FromResult(Response(HttpStatusCode.OK, "", uri));
            var body = uri.Query.Contains("method=pageInfo") ? "{\"passwordEncrypt\":\"false\"}"
                : uri.Query.Contains("method=getServices") ? "{\"serviceJson\":[{\"serviceName\":\"telecom-real\",\"serviceShowName\":\"中国电信\"}]}"
                : uri.Query.Contains("method=login") ? "{\"result\":\"success\"}"
                : "<title>上网认证</title><script>AuthInterFace.init('./')</script><input name=passwordEncrypt value=false>";
            return Task.FromResult(Response(HttpStatusCode.OK, body, uri));
        });
        var result = await new EportalLoginFlow(new NullLog(), () => handler, [new Uri("http://probe.test/check")])
            .ExecuteAsync(new("u", "p", "telecom"), 3, null, CancellationToken.None);
        Check.Equal(LoginState.Success, result.State);
    }
    [Case] public static void RSA高位为一时不增加符号补零()
    {
        const string expected = "853c761dcea37a151b4c780ca34c38eecd7b7ae0df5191f30203a88718297e4567539c8980235e0f80067691a862cccd03fd0b6868c05c7faca109ce05084b57851e90c9cb2ac9befb8d328e915b0e46ea8262064bc75fb824502c70edf0c9856a1af3df56186c24802be051ae8400c2d1a83868c3b5dc4cee24a658a606aedd";
        Check.Equal(expected, LegacyRsa.EncryptPassword("vector5", "10001", Modulus));
    }
    private const string Modulus = "94dd2a8675fb779e6b9f7103698634cd400f27a154afa67af6166a43fc26417222a79506d34cacc7641946abda1785b7acf9910ad6a0978c91ec84d40b71d2891379af19ffb333e7517e390bd26ac312fe940c340466b4a5d4af1d65c3b5944078f96a1a51a5a53e4bc302818b7c9f63c4a1b07bd7d874cef1c3d4b2f5eb7871";

    [Case] public static void GBK原页解析并提取隐藏字段()
    {
        var base64 = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "eportal-gbk.html.b64"));
        var page = PortalPageParser.Parse(Convert.FromBase64String(base64), null, new Uri("http://portal.test/eportal/index.jsp?a=1"));
        Check.Equal("上网认证", page.Title);
        Check.Equal("true", page.HiddenFields["passwordEncrypt"]);
        Check.Equal("电信", page.HiddenFields["net_access_type"]);
        Check.True(page.IsVerifiedEportal);
    }

    [Case] public static void Http字符集优先于错误Meta声明()
    {
        var bytes = Encoding.UTF8.GetBytes("<meta charset=GBK><title>上网认证</title><script>AuthInterFace.init('./')</script>");
        var page = PortalPageParser.Parse(bytes, "text/html; charset=utf-8", new Uri("http://portal.test/eportal/"));
        Check.Equal("上网认证", page.Title);
    }

    [Case] public static void PageInfo覆盖HTML且服务列表支持字符串和数组()
    {
        var page = PortalPageParser.Parse(Encoding.UTF8.GetBytes("<title>上网认证</title><script>AuthInterFace.init('./')</script><input id=passwordEncrypt value=false>"), "text/html; charset=utf-8", new Uri("http://portal.test/eportal/index.jsp?x=1"));
        var updated = PortalPageParser.ApplyPageInfo(page, "{\"passwordEncrypt\":\"true\",\"publicKeyExponent\":\"10001\",\"publicKeyModulus\":\"abcd\",\"validCodeUrl\":\"\"}");
        var stringServices = PortalPageParser.ParseServices("{\"serviceJson\":\"[{\\\"serviceName\\\":\\\"isp-dx-prod\\\",\\\"serviceShowName\\\":\\\"中国电信\\\"}]\"}");
        var arrayServices = PortalPageParser.ParseServices("{\"serviceJson\":[{\"serviceName\":\"campus-prod\",\"serviceShowName\":\"校园网\"}]}");
        Check.True(updated.PasswordEncrypt);
        Check.Equal("isp-dx-prod", ServiceSelector.Select("telecom", stringServices));
        Check.Equal("campus-prod", ServiceSelector.Select("campus", arrayServices));
        Check.Throws<InvalidOperationException>(() => ServiceSelector.Select("mobile", arrayServices));
        Check.Throws<InvalidDataException>(() => PortalPageParser.ApplyPageInfo(page, "[]"));
        Check.Throws<InvalidDataException>(() => PortalPageParser.ParseServices("not-json"));
    }

    [Case] public static void LegacyRSA匹配原脚本向量且参数错误不降级()
    {
        const string expected = "3ae619c65b3dd4ea2880868d91ac741caa71ed43da40727bc01117c67822c8ea5f68b7f3dcf9db3aa11dbf01651d0fde4f854ea3074794252fb68b84dc7eca49dcab28e9140afa62969fe559809fa7601d03bdb357a9df9cf576aca961b25ce9ba04037667a3cc2d2da46f68e8d3b46af6fac0834592b3a1cc560f79b09c2d8f";
        Check.Equal("Abc%2520123!", EportalEncoding.DoubleEncode("Abc 123!"));
        Check.Equal(expected, LegacyRsa.EncryptPassword("Abc 123!", "10001", Modulus));
        Check.Throws<ArgumentException>(() => LegacyRsa.EncryptPassword("secret", "zz", Modulus));
    }

    [Case] public static async Task 探测不把任意200当联网并识别门户()
    {
        var handler = new ScriptedHandler((request, _) => Task.FromResult(Response(HttpStatusCode.OK, "<html>普通网页</html>", request.RequestUri!)));
        var result = await new PortalProbeService(() => handler).ProbeAsync(CancellationToken.None);
        Check.Equal(ProbeState.UnknownPortal, result.State);

        var portal = new Uri("http://portal.test/eportal/index.jsp?token=dynamic");
        handler = new ScriptedHandler((request, _) => Task.FromResult(Response(HttpStatusCode.Redirect, "", request.RequestUri!, portal)));
        result = await new PortalProbeService(() => handler).ProbeAsync(CancellationToken.None);
        Check.Equal(ProbeState.CaptivePortal, result.State);
        Check.Equal(portal, result.PortalUri);
    }
    [Case] public static async Task HTTP200脚本跳转仅提取地址不执行脚本()
    {
        var handler = new ScriptedHandler((request, _) => Task.FromResult(Response(HttpStatusCode.OK, "<script>top.self.location.href='http://portal.test/eportal/index.jsp?wlanuserip=dynamic&amp;mac=temporary';</script>", request.RequestUri!)));
        var result = await new PortalProbeService(() => handler).ProbeAsync(CancellationToken.None);
        Check.Equal(ProbeState.CaptivePortal, result.State);
        Check.Equal("?wlanuserip=dynamic&mac=temporary", result.PortalUri!.Query);
    }

    [Case] public static async Task 探测精确校验正文并限制重定向循环()
    {
        var handler = new ScriptedHandler((request, _) => Task.FromResult(Response(HttpStatusCode.OK, request.RequestUri!.Host.Contains("msftconnecttest") ? "Microsoft Connect Test extra" : "wrong", request.RequestUri)));
        Check.Equal(ProbeState.UnknownPortal, (await new PortalProbeService(() => handler).ProbeAsync(CancellationToken.None)).State);

        handler = new ScriptedHandler((request, _) => Task.FromResult(Response(HttpStatusCode.Redirect, "", request.RequestUri!, new Uri("http://loop.test/a"))));
        var loop = await new PortalProbeService(() => handler, [new Uri("http://loop.test/a")]).ProbeAsync(CancellationToken.None);
        Check.Equal(ProbeState.UnknownPortal, loop.State);

        var redirectCount = 0;
        handler = new ScriptedHandler((request, _) => Task.FromResult(++redirectCount == 1
            ? Response(HttpStatusCode.Redirect, "", request.RequestUri!, new Uri("http://other.test/page"))
            : Response(HttpStatusCode.OK, "Microsoft Connect Test", request.RequestUri!)));
        var redirectedMarker = await new PortalProbeService(() => handler, [new Uri("http://www.msftconnecttest.com/connecttest.txt")]).ProbeAsync(CancellationToken.None);
        Check.Equal(ProbeState.UnknownPortal, redirectedMarker.State);

        handler = new ScriptedHandler((request, _) => Task.FromResult(Response(HttpStatusCode.OK, "canonical fake success", request.RequestUri!)));
        var fakeFirefox = await new PortalProbeService(() => handler, [new Uri("http://detectportal.firefox.com/canonical.html")]).ProbeAsync(CancellationToken.None);
        Check.Equal(ProbeState.UnknownPortal, fakeFirefox.State);

        handler = new ScriptedHandler((request, _) => Task.FromResult(Response(HttpStatusCode.OK, "success\n", request.RequestUri!)));
        var realFirefox = await new PortalProbeService(() => handler, [new Uri("http://detectportal.firefox.com/canonical.html")]).ProbeAsync(CancellationToken.None);
        Check.Equal(ProbeState.Online, realFirefox.State);
    }

    [Case] public static async Task 请求超时覆盖响应正文读取且释放Handler()
    {
        var handler = new DisposableHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, request.RequestUri),
            Content = new BlockingContent()
        }));
        var result = await new PortalProbeService(() => handler, [new Uri("http://probe.test/check")], TimeSpan.FromMilliseconds(50)).ProbeAsync(CancellationToken.None);
        Check.Equal(ProbeState.Offline, result.State);
        Check.True(result.Retryable);
        Check.True(handler.Disposed, "HttpClient 应拥有并释放 handler/Cookie 会话");
    }

    [Case] public static async Task 网络取消覆盖正在进行的请求()
    {
        var handler = new ScriptedHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); throw new Exception(); });
        using var cts = new CancellationTokenSource(30);
        try { await new PortalProbeService(() => handler).ProbeAsync(cts.Token); }
        catch (OperationCanceledException) { return; }
        throw new Exception("请求未响应取消");
    }

    [Case] public static async Task 真实Flow按协议顺序成功且使用动态服务值()
    {
        var portal = new Uri("http://portal.test/eportal/index.jsp?wlanuserip=dynamic%2Bvalue&x=1");
        var calls = new List<(Uri Uri, string Body)>();
        var handler = new ScriptedHandler(async (request, token) =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            calls.Add((request.RequestUri!, body));
            return calls.Count switch
            {
                1 => Response(HttpStatusCode.Redirect, "", request.RequestUri!, portal),
                2 => Response(HttpStatusCode.OK, "<meta charset=utf-8><title>上网认证</title><script>AuthInterFace.init('./')</script><input id=passwordEncrypt value=false>", request.RequestUri!),
                3 => Response(HttpStatusCode.OK, "{\"passwordEncrypt\":\"false\",\"validCodeUrl\":\"\"}", request.RequestUri!),
                4 => Response(HttpStatusCode.OK, "{\"serviceJson\":[{\"serviceName\":\"isp-dx-prod\",\"serviceShowName\":\"中国电信\"}]}", request.RequestUri!),
                _ => Response(HttpStatusCode.OK, "{\"result\":\"success\",\"userIndex\":\"ok\"}", request.RequestUri!)
            };
        });
        var log = new CollectLog();
        var flow = new EportalLoginFlow(log, () => handler, [new Uri("http://probe.test/check")]);
        var result = await flow.ExecuteAsync(new LoginRequest("user name", "p@ss word", "telecom"), 3, null, CancellationToken.None);
        Check.Equal(LoginState.Success, result.State);
        Check.True(calls[2].Uri.Query.Contains("method=pageInfo"));
        Check.True(calls[2].Body.Contains("queryString=wlanuserip%3Ddynamic%252Bvalue%26x%3D1"));
        Check.True(calls[3].Uri.Query.Contains("method=getServices&queryString=wlanuserip%3Ddynamic%252Bvalue%26x%3D1"));
        Check.Equal("", calls[3].Body);
        Check.True(calls[4].Body.Contains("userId=user%2520name"));
        Check.True(calls[4].Body.Contains("password=p%2540ss%2520word"));
        Check.True(calls[4].Body.Contains("service=isp-dx-prod"));
        Check.True(!calls[4].Body.Contains("loginToken="), "旧门户未返回令牌时保持兼容");
        Check.True(log.Messages.Any(m => m.Contains("status=200") && m.Contains("len=")));
        Check.True(log.Messages.Any(m => m.Contains("http://portal.test/eportal/index.jsp")));
        Check.True(log.Messages.All(m => !m.Contains("wlanuserip") && !m.Contains("dynamic") && !m.Contains("user name") && !m.Contains("p@ss")));
    }

    [Case] public static async Task 验证码时不提交密码且返回门户地址()
    {
        var portal = new Uri("http://portal.test/eportal/index.jsp?a=1");
        var count = 0;
        var handler = new ScriptedHandler((request, _) => Task.FromResult(++count switch
        {
            1 => Response(HttpStatusCode.Redirect, "", request.RequestUri!, portal),
            2 => Response(HttpStatusCode.OK, "<title>上网认证</title><script>AuthInterFace.init('./')</script>", request.RequestUri!),
            _ => Response(HttpStatusCode.OK, "{\"passwordEncrypt\":\"false\",\"validCodeUrl\":\"captcha.jpg\"}", request.RequestUri!)
        }));
        var flow = new EportalLoginFlow(new NullLog(), () => handler, [new Uri("http://probe.test/check")]);
        var result = await flow.ExecuteAsync(new LoginRequest("u", "secret", "campus"), 3, null, CancellationToken.None);
        Check.Equal(LoginState.CaptchaRequired, result.State);
        Check.Equal(portal, result.PortalUri);
        Check.Equal(3, count);
    }

    [Case] public static async Task PageInfo坏JSON返回受控失败()
    {
        var portal = new Uri("http://portal.test/eportal/index.jsp?a=1");
        var count = 0;
        var handler = new ScriptedHandler((request, _) => Task.FromResult(++count switch
        {
            1 => Response(HttpStatusCode.Redirect, "", request.RequestUri!, portal),
            2 => Response(HttpStatusCode.OK, "<title>上网认证</title><script>AuthInterFace.init('./')</script>", request.RequestUri!),
            _ => Response(HttpStatusCode.OK, "[]", request.RequestUri!)
        }));
        var result = await new EportalLoginFlow(new NullLog(), () => handler, [new Uri("http://probe.test/check")])
            .ExecuteAsync(new LoginRequest("u", "secret", "campus"), 3, null, CancellationToken.None);
        Check.Equal(LoginState.Failed, result.State);
        Check.True(!result.Retryable);
        Check.Equal(3, count);
    }

    [Case] public static void 登录失败分类区分可重试与不可重试()
    {
        Check.True(!LoginResponseClassifier.Classify("{\"result\":\"fail\",\"message\":\"用户名或密码错误\"}", null).Retryable);
        Check.True(!LoginResponseClassifier.Classify("{\"result\":\"fail\",\"message\":\"不在认证时段\"}", null).Retryable);
        Check.True(LoginResponseClassifier.Classify("{\"result\":\"fail\",\"message\":\"系统繁忙\"}", null).Retryable);
        var online = LoginResponseClassifier.Classify("{\"result\":\"fail\",\"message\":\"已经在线\"}", null);
        Check.Equal(LoginState.Online, online.State);
        Check.True(!online.Retryable);
    }

    [Case] public static async Task 加密多分块密文按原页面再次双重编码()
    {
        var portal = new Uri("http://portal.test/eportal/index.jsp?a=1");
        var calls = new List<string>();
        var handler = new ScriptedHandler(async (request, token) =>
        {
            calls.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(token));
            return calls.Count switch
            {
                1 => Response(HttpStatusCode.Redirect, "", request.RequestUri!, portal),
                2 => Response(HttpStatusCode.OK, "<title>上网认证</title><script>AuthInterFace.init('./')</script>", request.RequestUri!),
                3 => Response(HttpStatusCode.OK, $"{{\"passwordEncrypt\":\"true\",\"publicKeyExponent\":\"10001\",\"publicKeyModulus\":\"{Modulus}\",\"validCodeUrl\":\"\"}}", request.RequestUri!),
                4 => Response(HttpStatusCode.OK, "{\"serviceJson\":[{\"serviceName\":\"campus\",\"serviceShowName\":\"校园网\"}]}", request.RequestUri!),
                _ => Response(HttpStatusCode.OK, "{\"result\":\"success\"}", request.RequestUri!)
            };
        });
        var result = await new EportalLoginFlow(new NullLog(), () => handler, [new Uri("http://probe.test/check")])
            .ExecuteAsync(new LoginRequest("u", new string('a', 140), "campus"), 3, null, CancellationToken.None);
        Check.Equal(LoginState.Success, result.State);
        Check.True(calls[4].Contains("%2520"), "RSA 多块间空格应再经过双重 encodeURIComponent");
    }

    [Case] public static void 使用外链认证脚本的页面通过特征验证()
    {
        // 使用合成页面，仓库测试不依赖含真实网络参数的本地网页。
        var html = "<meta charset=utf-8><title>上网认证</title><script src='./interface/AuthInterFace.js'></script><script>AuthInterFace.init('./');</script>";
        var page = PortalPageParser.Parse(Encoding.UTF8.GetBytes(html), null, new Uri("http://portal.test/eportal/index.jsp?a=1"));
        Check.Equal("上网认证", page.Title);
        Check.True(page.IsVerifiedEportal, "原页外链 AuthInterFace.js 时也应通过已知适配器校验");
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body, Uri request, Uri? location = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body), RequestMessage = new HttpRequestMessage(HttpMethod.Get, request) };
        response.Headers.Location = location;
        return response;
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class DisposableHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class BlockingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task<Stream> CreateContentReadStreamAsync() { await Task.Yield(); return new BlockingStream(); }
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => 0, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => 0, cancellationToken));
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CollectLog : ILogSink
    {
        public List<string> Messages { get; } = [];
        public void Write(LogLevel level, string source, string message) => Messages.Add(message);
        public void Clear() => Messages.Clear();
    }

    private sealed class NullLog : ILogSink { public void Write(LogLevel level, string source, string message) { } public void Clear() { } }
}
