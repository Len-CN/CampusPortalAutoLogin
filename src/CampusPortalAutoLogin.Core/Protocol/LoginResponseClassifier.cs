using System.Text.Json;
using CampusPortalAutoLogin.Core.Models;

namespace CampusPortalAutoLogin.Core.Protocol;

public static class LoginResponseClassifier
{
    public static LoginResult Classify(string json, Uri? portalUri)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var result = Get(root, "result");
            var message = Get(root, "message") ?? Get(root, "msg") ?? "认证失败。";
            if (string.Equals(result, "success", StringComparison.OrdinalIgnoreCase)) return new(LoginState.Success, "登录成功。", PortalUri: portalUri);
            if (ContainsAny(message, "已经在线", "已在线")) return new(LoginState.Online, "账号已经在线。", false, portalUri);
            if (ContainsAny(message, "验证码")) return new(LoginState.CaptchaRequired, "认证需要验证码，请在浏览器中人工完成。", PortalUri: portalUri);
            if (ContainsAny(message, "密码", "用户名", "账号", "服务", "运营商", "时段", "时间段", "参数", "缺失")) return new(LoginState.Failed, SafeMessage(message), false, portalUri);
            return new(LoginState.Failed, SafeMessage(message), true, portalUri);
        }
        catch (JsonException) { return new(LoginState.Failed, "认证服务器返回了无法识别的响应。", true, portalUri); }
    }

    private static string? Get(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.ToString() : null;
    private static bool ContainsAny(string value, params string[] words) => words.Any(value.Contains);
    private static string SafeMessage(string message) => message.Length <= 160 ? message : message[..160];
}
