using System.Text;

namespace CampusPortalAutoLogin.Core.Protocol;

public static class EportalEncoding
{
    public static string DoubleEncode(string value) => EncodeURIComponent(EncodeURIComponent(value));

    public static string EncodeURIComponent(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (b < 128 && (char.IsAsciiLetterOrDigit(c) || "-_.!~*'()".Contains(c))) result.Append(c);
            else result.Append('%').Append(b.ToString("X2"));
        }
        return result.ToString();
    }
}
