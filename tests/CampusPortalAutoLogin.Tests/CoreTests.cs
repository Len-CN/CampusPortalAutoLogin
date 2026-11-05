using System.IO;
using System.Text;
using CampusPortalAutoLogin.Core.Models;
using CampusPortalAutoLogin.Core.Security;
using CampusPortalAutoLogin.Core.Services;
using CampusPortalAutoLogin.Core.Logging;
using CampusPortalAutoLogin.Core.Scheduling;

namespace CampusPortalAutoLogin.Tests;

public static class CoreTests
{
    [Case] public static void 配置校验拒绝越界和非法时间()
    {
        var s = new AppSettings { Username = "u", DetectIntervalSeconds = 4, ScheduledTime = "7:00", MaxRetries = 6 };
        Check.True(s.Validate().Count >= 3);
        Check.Equal(0, (s with { DetectIntervalSeconds = 45, ScheduledTime = "07:00", MaxRetries = 3 }).Validate().Count);
        Check.True(new AppSettings().Validate().Any(e => e.Contains("用户名")));
    }
    [Case] public static void DPAPI往返且密文不包含明文()
    {
        var bytes = Encoding.UTF8.GetBytes("测试密钥_unique_password");
        var encrypted = Dpapi.Protect(bytes);
        Check.True(!Encoding.UTF8.GetString(encrypted).Contains("unique_password"));
        Check.True(bytes.SequenceEqual(Dpapi.Unprotect(encrypted)));
        encrypted[^1] ^= 123;
        Check.Throws<System.Security.Cryptography.CryptographicException>(() => Dpapi.Unprotect(encrypted));
    }
    [Case] public static void 配置存储加密密码并恢复损坏配置()
    {
        var dir = Temp();
        try
        {
            var store = new SettingsStore(dir, false);
            Check.Equal("07:00", store.Load().Settings.ScheduledTime);
            store.Save(new AppSettings { Username = "test-user" }, "sample_password_908");
            Check.Equal("sample_password_908", store.Load().Password);
            Check.True(!File.ReadAllText(Path.Combine(dir, "config.json")).Contains("sample_password"));
            Check.True(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dir, "secrets.dat"))).Contains("sample_password"));
            store.Save(new AppSettings { Username = "new-user" }, "new_password");
            Check.Equal("new-user", store.Load().Settings.Username);
            Check.Equal("new_password", store.Load().Password);
            File.WriteAllText(Path.Combine(dir, "config.json"), "{broken");
            var recovered = store.Load();
            Check.Equal("", recovered.Password);
            Check.True(!string.IsNullOrEmpty(recovered.Warning));
        }
        finally { Directory.Delete(dir, true); }
    }
    [Case] public static void 未来配置版本不会被覆盖()
    {
        var dir = Temp();
        try
        {
            File.WriteAllText(Path.Combine(dir, "config.json"), "{\"version\":999,\"username\":\"future\"}");
            var store = new SettingsStore(dir, false);
            Check.Throws<InvalidDataException>(() => store.Load());
            Check.Throws<InvalidDataException>(() => store.Save(new AppSettings { Username = "u" }, "p"));
            Check.True(File.ReadAllText(Path.Combine(dir, "config.json")).Contains("999"));
        }
        finally { Directory.Delete(dir, true); }
    }
    [Case] public static void 日志脱敏同时遮盖已知凭据和动态字段()
    {
        var redactor = new LogRedactor(); redactor.SetCredentials("student001", "p@ssXYZ");
        var output = redactor.Redact("student001 p@ssXYZ password=abc Cookie: sid=SECRETTOKEN\nhttp://p/eportal?a=secret&mac=123456789012 token=xyz 00:11:22:33:44:55");
        foreach (var secret in new[] { "student001", "p@ssXYZ", "abc", "SECRETTOKEN", "a=secret", "123456789012", "xyz", "00:11:22:33:44:55" }) Check.True(!output.Contains(secret), "日志泄漏 " + secret);
    }
    [Case] public static void 登录令牌在日志中脱敏()
    {
        var redactor = new LogRedactor();
        foreach (var input in new[] { "loginToken=temporary-secret", "{\"loginToken\":\"temporary secret\"}", "loginToken=temporary%20secret" })
        {
            var output = redactor.Redact(input);
            Check.True(!output.Contains("temporary"));
            Check.True(output.Contains("loginToken=[已隐藏]"));
        }
    }
    [Case] public static void 日志落盘读取保留筛选字段()
    {
        var dir = Temp();
        try
        {
            var log = new LogService(dir); log.Redactor.SetCredentials("u-student", "pw-secret");
            log.Write(LogLevel.Error, "测试", "pw-secret");
            var entries = log.ReadRecent();
            Check.Equal(1, entries.Count); Check.Equal(LogLevel.Error, entries[0].Level);
            Check.True(!entries[0].Message.Contains("pw-secret"));
            log.Clear(); Check.Equal(0, log.ReadRecent().Count);
        }
        finally { Directory.Delete(dir, true); }
    }
    [Case] public static void 保存新配置后旧登录返回值仍能脱敏()
    {
        var redactor = new LogRedactor(); redactor.SetCredentials("old-student", "old-secret");
        redactor.SetCredentials("new-student", "new-secret");
        var output = redactor.Redact("old-student old-secret new-student new-secret");
        Check.True(!output.Contains("old-secret") && !output.Contains("old-student"));
        Check.True(!output.Contains("new-secret") && !output.Contains("new-student"));
    }
    [Case] public static void URL用户信息不会写入日志()
    {
        var output = new LogRedactor().Redact("http://unknown-user:unknown-password@portal.test/eportal/?token=abc");
        Check.True(!output.Contains("unknown-user") && !output.Contains("unknown-password") && !output.Contains("abc"));
    }
    [Case] public static void 日志固定文件追加且重新打开仍可读取()
    {
        var dir = Temp();
        try
        {
            var log = new LogService(dir);
            log.Write(LogLevel.Info, "测试", "开始");
            log.Write(LogLevel.Success, "测试", "结束");
            Check.Equal("latest.jsonl", Path.GetFileName(Directory.GetFiles(dir, "*.jsonl").Single()));
            var reopened = new LogService(dir).ReadRecent();
            Check.Equal(2, reopened.Count);
            Check.Equal("结束", reopened[1].Message);
        }
        finally { Directory.Delete(dir, true); }
    }
    [Case] public static async Task 提前取消的触发返回取消状态()
    {
        await using var coordinator = new LoginCoordinator(new FakeFlow(_ => throw new Exception("不应执行")), new NullLog());
        var result = await coordinator.TriggerAsync("测试", new AppSettings(), new("u", "p", "telecom"), new CancellationToken(true));
        Check.Equal(LoginState.Cancelled, result.State);
    }
    [Case] public static void 自启XML保留带空格和特殊字符的路径并使用低权限()
    {
        var xml = System.Xml.Linq.XDocument.Parse(AutostartService.CreateXml("C:\\Program Files\\Campus & Portal\\app.exe", "S-1-5-21-123"));
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Check.Equal("C:\\Program Files\\Campus & Portal\\app.exe", xml.Descendants(ns + "Command").Single().Value);
        Check.Equal("InteractiveToken", xml.Descendants(ns + "LogonType").Single().Value);
        Check.Equal("LeastPrivilege", xml.Descendants(ns + "RunLevel").Single().Value);
        Check.Equal("PT10S", xml.Descendants(ns + "Delay").Single().Value);
    }
    [Case] public static void 跨年调度与唤醒补执行不重复()
    {
        var now = new DateTime(2026, 12, 31, 23, 59, 0);
        Check.Equal(new DateTime(2027, 1, 1, 7, 0, 0), ScheduleClock.Next(now, "07:00"));
        var clock = new ScheduleClock(); var settings = new AppSettings { ScheduledTime = "07:00" };
        clock.Reset(new DateTime(2026, 9, 23, 6, 59, 0), settings);
        Check.True(clock.TakeDue(new DateTime(2026, 9, 23, 7, 5, 0), settings));
        Check.True(!clock.TakeDue(new DateTime(2026, 9, 23, 7, 5, 10), settings));
        clock.Reset(new DateTime(2026, 9, 23, 7, 5, 10), settings);
        Check.True(!clock.TakeDue(new DateTime(2026, 9, 23, 7, 6, 0), settings));
        Check.True(clock.TakeDue(new DateTime(2026, 9, 24, 7, 0, 0), settings));
    }
    [Case] public static async Task 并发触发合并且取消后可以重新执行()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flow = new FakeFlow(async ct => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new(LoginState.Success, "ok"); });
        await using var coordinator = new LoginCoordinator(flow, new NullLog());
        using var cancel = new CancellationTokenSource();
        var first = coordinator.TriggerAsync("测试", new AppSettings(), new("u", "p", "telecom"), cancel.Token);
        await entered.Task;
        Check.Equal(LoginState.Merged, (await coordinator.TriggerAsync("网络变化", new AppSettings(), new("u", "p", "telecom"))).State);
        cancel.Cancel(); Check.Equal(LoginState.Cancelled, (await first).State);
        Check.True(!coordinator.IsBusy);
        flow.Work = _ => Task.FromResult(new LoginResult(LoginState.Online, "online"));
        Check.Equal(LoginState.Online, (await coordinator.TriggerAsync("立即登录", new AppSettings(), new("u", "p", "telecom"))).State);
    }
    [Case] public static async Task 重试只针对临时错误且最多初次加三次()
    {
        var flow = new FakeFlow(_ => Task.FromResult(new LoginResult(LoginState.Offline, "暂时不可用", true)));
        await using var coordinator = new LoginCoordinator(flow, new NullLog(), (_, _) => Task.CompletedTask);
        var result = await coordinator.TriggerAsync("测试", new AppSettings(), new("u", "p", "telecom"));
        Check.Equal(4, flow.Calls); Check.Equal(LoginState.Offline, result.State);
        flow.Calls = 0; flow.Work = _ => Task.FromResult(new LoginResult(LoginState.Failed, "密码错误"));
        await coordinator.TriggerAsync("测试", new AppSettings(), new("u", "p", "telecom"));
        Check.Equal(1, flow.Calls);
    }
    [Case] public static async Task 新一轮覆盖旧日志且成功结果保留在唯一文件()
    {
        var dir = Temp();
        try
        {
            var log = new LogService(dir);
            var other = Path.Combine(dir, "keep.txt"); File.WriteAllText(other, "保留");
            var flow = new FakeFlow(_ => Task.FromResult(new LoginResult(LoginState.Success, "登录成功")));
            await using var coordinator = new LoginCoordinator(flow, log);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                log.Write(LogLevel.Error, "历史", "上次失败");
                File.Copy(Directory.GetFiles(dir, "*.jsonl")[0], Path.Combine(dir, "rotated.jsonl"), true);
                var result = await coordinator.TriggerAsync("测试登录", new AppSettings(), new("u", "p", "telecom"));
                Check.Equal(LoginState.Success, result.State);
                var entries = log.ReadRecent();
                Check.Equal(2, entries.Count);
                Check.True(entries.All(e => e.Source != "历史"));
                Check.Equal(LogLevel.Success, entries[^1].Level);
                Check.Equal("登录成功", entries[^1].Message);
                Check.Equal("latest.jsonl", Path.GetFileName(Directory.GetFiles(dir, "*.jsonl").Single()));
                Check.Equal("保留", File.ReadAllText(other));
            }
        }
        finally { Directory.Delete(dir, true); }
    }
    [Case] public static async Task 失败取消及已联网探测均覆盖历史并保留本轮结果()
    {
        var dir = Temp();
        try
        {
            var log = new LogService(dir);
            foreach (var state in new[] { LoginState.Failed, LoginState.Cancelled, LoginState.Online, LoginState.Offline, LoginState.CaptchaRequired })
            {
                log.Write(LogLevel.Error, "历史", "待排查记录");
                await using var coordinator = new LoginCoordinator(new FakeFlow(_ => Task.FromResult(new LoginResult(state, "结果"))), log);
                await coordinator.TriggerAsync("测试", new AppSettings(), new("u", "p", "telecom"));
                Check.True(log.ReadRecent().All(e => e.Message != "待排查记录"));
                Check.Equal("结果", log.ReadRecent()[^1].Message);
                Check.Equal(1, Directory.GetFiles(dir, "*.jsonl").Length);
            }
        }
        finally { Directory.Delete(dir, true); }
    }
    [Case] public static async Task 日志被占用时清理失败不改变登录成功结果()
    {
        var dir = Temp();
        try
        {
            var log = new LogService(dir); log.Write(LogLevel.Error, "历史", "待清理记录");
            using var held = new FileStream(Directory.GetFiles(dir, "*.jsonl")[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await using var coordinator = new LoginCoordinator(new FakeFlow(_ => Task.FromResult(new LoginResult(LoginState.Success, "登录成功"))), log);
            var result = await coordinator.TriggerAsync("测试", new AppSettings(), new("u", "p", "telecom"));
            Check.Equal(LoginState.Success, result.State);
            Check.True(log.ReadRecent().Any(e => e.Level == LogLevel.Warning && e.Message.Contains("覆盖")));
            Check.Equal("登录成功", log.ReadRecent()[^1].Message);
        }
        finally { Directory.Delete(dir, true); }
    }
    [Case] public static async Task 同轮重试和合并触发不会覆盖正在进行的日志()
    {
        var dir = Temp();
        try
        {
            var log = new LogService(dir);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = 0;
            var flow = new FakeFlow(async ct =>
            {
                log.Write(LogLevel.Info, "认证", $"尝试 {++attempts}");
                if (attempts == 1)
                {
                    entered.SetResult();
                    await resume.Task.WaitAsync(ct);
                    return new(LoginState.Failed, "临时失败", true);
                }
                return new(LoginState.Success, "登录成功");
            });
            await using var coordinator = new LoginCoordinator(flow, log, (_, _) => Task.CompletedTask);
            var first = coordinator.TriggerAsync("测试", new AppSettings(), new("u", "p", "telecom"));
            await entered.Task;
            Check.Equal(LoginState.Merged, (await coordinator.TriggerAsync("合并", new AppSettings(), new("u", "p", "telecom"))).State);
            Check.True(log.ReadRecent().Any(e => e.Message == "尝试 1"));
            resume.SetResult();
            Check.Equal(LoginState.Success, (await first).State);
            var entries = log.ReadRecent();
            Check.True(entries.Any(e => e.Message == "尝试 1") && entries.Any(e => e.Message == "尝试 2"));
            Check.True(entries.Any(e => e.Message.Contains("第 1 次重试")));
            Check.Equal("登录成功", entries[^1].Message);
            Check.Equal(1, Directory.GetFiles(dir, "*.jsonl").Length);
        }
        finally { Directory.Delete(dir, true); }
    }
    private static string Temp() { var path = Path.Combine(Environment.CurrentDirectory, ".tools", "tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private sealed class NullLog : ILogSink { public void Write(LogLevel level, string source, string message) { } public void Clear() { } }
    private sealed class FakeFlow(Func<CancellationToken, Task<LoginResult>> work) : ILoginFlow
    {
        public int Calls; public Func<CancellationToken, Task<LoginResult>> Work = work;
        public Task<LoginResult> ExecuteAsync(LoginRequest request, int timeoutSeconds, IProgress<LoginProgress>? progress, CancellationToken cancellationToken) { Calls++; return Work(cancellationToken); }
    }
}
