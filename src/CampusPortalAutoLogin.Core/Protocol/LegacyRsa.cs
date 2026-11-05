using System.Globalization;
using System.Numerics;

namespace CampusPortalAutoLogin.Core.Protocol;

public static class LegacyRsa
{
    public static string EncryptPassword(string password, string exponentHex, string modulusHex)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (exponentHex is null || exponentHex.Length > 8 || !TryHex(exponentHex, out var exponent) || exponent <= 0) throw new ArgumentException("RSA 公钥指数无效。", nameof(exponentHex));
        if (modulusHex is null || modulusHex.Length is < 8 or > 1024 || !TryHex(modulusHex, out var modulus) || modulus <= 0) throw new ArgumentException("RSA 公钥模数无效。", nameof(modulusHex));

        var encoded = EportalEncoding.DoubleEncode(password);
        var chars = encoded.Reverse().Select(c => (byte)c).ToArray();
        var digitCount = (modulusHex.TrimStart('0').Length + 3) / 4;
        var chunkSize = (digitCount - 1) * 2;
        if (chunkSize <= 0) throw new ArgumentException("RSA 公钥模数过短。", nameof(modulusHex));

        var blocks = new List<string>();
        for (var offset = 0; offset < chars.Length; offset += chunkSize)
        {
            var bytes = new byte[chunkSize + 1];
            Array.Copy(chars, offset, bytes, 0, Math.Min(chunkSize, chars.Length - offset));
            var encrypted = BigInteger.ModPow(new BigInteger(bytes), exponent, modulus);
            var hex = Convert.ToHexString(encrypted.ToByteArray(isUnsigned: true, isBigEndian: true)).ToLowerInvariant();
            blocks.Add(hex.PadLeft((hex.Length + 3) / 4 * 4, '0'));
        }
        return string.Join(' ', blocks);
    }

    private static bool TryHex(string? value, out BigInteger number)
    {
        number = BigInteger.Zero;
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => !Uri.IsHexDigit(c))) return false;
        return BigInteger.TryParse("0" + value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out number);
    }
}
