using System.Globalization;

namespace CampusPortalAutoLogin.Core.Models;

public sealed record AppSettings
{
    public int Version { get; init; } = 1;
    public string Username { get; init; } = "";
    public string ServiceProvider { get; init; } = "telecom";
    public bool Autostart { get; init; } = true;
    public bool NetworkChange { get; init; } = true;
    public bool RunOnStartup { get; init; } = true;
    public bool AutoDetect { get; init; }
    public int DetectIntervalSeconds { get; init; } = 45;
    public bool ScheduledLogin { get; init; } = true;
    public string ScheduledTime { get; init; } = "07:00";
    public int RequestTimeoutSeconds { get; init; } = 12;
    public int MaxRetries { get; init; } = 3;
    public int[] RetryDelaysSeconds { get; init; } = [5, 15, 30];
    public int LogRetentionDays { get; init; } = 30;
    public string SecretId { get; init; } = "";
    public bool PrivacyAcknowledged { get; init; }
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Username)) errors.Add("用户名不能为空。");
        if (Username?.Length > 256) errors.Add("用户名过长。");
        if (ServiceProvider is not ("campus" or "mobile" or "unicom" or "telecom")) errors.Add("请选择服务商。");
        if (DetectIntervalSeconds is < 5 or > 3600) errors.Add("检测间隔须为 5 至 3600 秒。");
        if (!TimeOnly.TryParseExact(ScheduledTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) errors.Add("执行时间须为 HH:mm，例如 07:00。");
        if (RequestTimeoutSeconds is < 1 or > 120) errors.Add("请求超时须为 1 至 120 秒。");
        if (MaxRetries is < 0 or > 5) errors.Add("重试次数须为 0 至 5。");
        if (RetryDelaysSeconds is null || RetryDelaysSeconds.Length == 0 || RetryDelaysSeconds.Any(s => s is < 1 or > 300)) errors.Add("重试等待须为 1 至 300 秒。");
        if (LogRetentionDays is < 1 or > 365) errors.Add("日志保留须为 1 至 365 天。");
        return errors;
    }
}
public sealed record SettingsSnapshot(AppSettings Settings, string Password, string? Warning = null);
