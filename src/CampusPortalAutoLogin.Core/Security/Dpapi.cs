using System.Security.Cryptography;
namespace CampusPortalAutoLogin.Core.Security;
public static class Dpapi
{
    public static byte[] Protect(byte[] bytes) => ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
    public static byte[] Unprotect(byte[] bytes) => ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
}
