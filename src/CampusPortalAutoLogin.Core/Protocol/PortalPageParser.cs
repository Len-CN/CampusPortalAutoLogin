using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CampusPortalAutoLogin.Core.Protocol;

public sealed record PortalPageData(
    Uri PortalUri,
    string Title,
    IReadOnlyDictionary<string, string> HiddenFields,
    bool IsVerifiedEportal,
    bool PasswordEncrypt,
    string PublicKeyExponent,
    string PublicKeyModulus,
    string ValidCodeUrl,
    bool RequiresOperatorCredentials = false,
    string LoginToken = "");

public sealed record PortalService(string Name, string Value, bool RequiresOperatorPassword = false);

public static partial class PortalPageParser
{
    static PortalPageParser() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static PortalPageData Parse(byte[] bytes, string? contentType, Uri portalUri)
    {
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("认证页超过允许大小。 ");
        var html = Decode(bytes, contentType);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in InputRegex().Matches(html))
        {
            var tag = match.Value;
            var key = Attribute(tag, "name") ?? Attribute(tag, "id");
            if (!string.IsNullOrEmpty(key)) fields[key] = Attribute(tag, "value") ?? "";
        }
        var title = WebUtility.HtmlDecode(TitleRegex().Match(html).Groups[1].Value.Trim());
        var verified = html.Contains("AuthInterFace.init", StringComparison.OrdinalIgnoreCase)
            && (portalUri.AbsolutePath.Contains("eportal", StringComparison.OrdinalIgnoreCase) || html.Contains("InterFace.do", StringComparison.OrdinalIgnoreCase) || title.Contains("上网认证"));
        fields.TryGetValue("passwordEncrypt", out var encrypt);
        fields.TryGetValue("publicKeyExponent", out var exponent);
        fields.TryGetValue("publicKeyModulus", out var modulus);
        return new(portalUri, title, fields, verified, string.Equals(encrypt, "true", StringComparison.OrdinalIgnoreCase), exponent ?? "", modulus ?? "", "");
    }

    public static PortalPageData ApplyPageInfo(PortalPageData page, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("pageInfo 返回格式不受支持。 ");
            var encrypt = Value(root, "passwordEncrypt") ?? page.PasswordEncrypt.ToString().ToLowerInvariant();
            return page with
            {
                PasswordEncrypt = string.Equals(encrypt, "true", StringComparison.OrdinalIgnoreCase),
                PublicKeyExponent = Value(root, "publicKeyExponent") ?? page.PublicKeyExponent,
                PublicKeyModulus = Value(root, "publicKeyModulus") ?? page.PublicKeyModulus,
                ValidCodeUrl = Value(root, "validCodeUrl") ?? page.ValidCodeUrl,
                LoginToken = Value(root, "loginToken") ?? "",
                RequiresOperatorCredentials = IsTrue(root, "isNoOperatorPwd") || IsTrue(root, "operatorPasswordRequired")
            };
        }
        catch (JsonException e) { throw new InvalidDataException("pageInfo 返回了无效 JSON。 ", e); }
    }

    public static IReadOnlyList<PortalService> ParseServices(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("服务接口返回格式不受支持。 ");
            if (!document.RootElement.TryGetProperty("serviceJson", out var services)) return [];
            using var nested = services.ValueKind == JsonValueKind.String ? JsonDocument.Parse(services.GetString() ?? "[]") : null;
            var array = nested?.RootElement ?? services;
            if (array.ValueKind != JsonValueKind.Array) throw new InvalidDataException("服务列表格式不受支持。 ");
            var result = new List<PortalService>();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("服务项格式不受支持。 ");
                var name = Value(item, "serviceShowName") ?? Value(item, "displayName") ?? Value(item, "serviceName") ?? Value(item, "name") ?? "";
                var value = Value(item, "serviceName") ?? Value(item, "value") ?? "";
                if (name.Length > 0 && value.Length > 0) result.Add(new(name, value, IsTrue(item, "isNoOperatorPwd")));
            }
            return result;
        }
        catch (JsonException e) { throw new InvalidDataException("服务接口返回了无效 JSON。 ", e); }
    }

    private static string Decode(byte[] bytes, string? contentType)
    {
        var charset = Regex.Match(contentType ?? "", "charset\\s*=\\s*['\\\"]?([^;\\s'\\\"]+)", RegexOptions.IgnoreCase).Groups[1].Value;
        if (charset.Length > 0) return GetEncoding(charset).GetString(bytes);
        var ascii = Encoding.ASCII.GetString(bytes.Take(Math.Min(bytes.Length, 4096)).ToArray());
        charset = Regex.Match(ascii, "charset\\s*=\\s*['\\\"]?([^;\\s'\\\">]+)", RegexOptions.IgnoreCase).Groups[1].Value;
        if (charset.Length > 0) return GetEncoding(charset).GetString(bytes);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(936).GetString(bytes); }
    }

    private static Encoding GetEncoding(string name) => name.Equals("gbk", StringComparison.OrdinalIgnoreCase) || name.Equals("gb2312", StringComparison.OrdinalIgnoreCase) ? Encoding.GetEncoding(936) : Encoding.GetEncoding(name);
    private static string? Attribute(string tag, string name) { var m = Regex.Match(tag, $"\\b{name}\\s*=\\s*(?:\\\"([^\\\"]*)\\\"|'([^']*)'|([^\\s>]+))", RegexOptions.IgnoreCase); return m.Success ? WebUtility.HtmlDecode(m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value) : null; }
    private static string? Value(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? value.ToString() : null;
    private static bool IsTrue(JsonElement root, string name) => string.Equals(Value(root, name), "true", StringComparison.OrdinalIgnoreCase);
    [GeneratedRegex("<input\\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex InputRegex();
    [GeneratedRegex("<title\\b[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex TitleRegex();
}

public static class ServiceSelector
{
    private static readonly IReadOnlyDictionary<string, string[]> Names = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["campus"] = ["校园网", "校园"], ["mobile"] = ["中国移动", "移动"], ["unicom"] = ["中国联通", "联通"], ["telecom"] = ["中国电信", "电信"]
    };

    public static string Select(string provider, IReadOnlyList<PortalService> services)
    {
        if (!Names.TryGetValue(provider, out var names)) throw new InvalidOperationException("未知服务商设置，需要人工认证。 ");
        var service = services.FirstOrDefault(s => names.Any(name => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase)));
        if (service is null) throw new InvalidOperationException("门户未提供所选服务商，需要人工认证。 ");
        if (service.RequiresOperatorPassword) throw new InvalidOperationException("该服务需要运营商附加密码，需要人工认证。 ");
        return service.Value;
    }
}
