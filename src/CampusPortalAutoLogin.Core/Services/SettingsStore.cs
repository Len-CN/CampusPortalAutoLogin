using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text.Json;
using CampusPortalAutoLogin.Core.Models;
using CampusPortalAutoLogin.Core.Security;
namespace CampusPortalAutoLogin.Core.Services;
public sealed class SettingsStore(string directory, bool restrictPermissions = true)
{
    private readonly object gate = new();
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    public string DirectoryPath => directory;
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CampusPortalAutoLogin");
    public SettingsSnapshot Load()
    {
        lock (gate)
        {
            EnsureDirectory();
            var path = Path.Combine(directory, "config.json");
            if (!File.Exists(path)) return new(new(), "");
            AppSettings settings;
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? throw new JsonException();
                if (settings.Version > 1) throw new InvalidDataException("配置来自较新版本，请升级程序，当前配置不会被覆盖。");
                if (settings.Version < 0 || (settings with { Username = "validation" }).Validate().Count != 0) throw new JsonException();
                settings = settings with { Version = 1, Username = settings.Username ?? "", SecretId = settings.SecretId ?? "" };
            }
            catch (JsonException)
            {
                File.Move(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmssfff"));
                return new(new(), "", "配置损坏，已保留备份并恢复默认设置，请重新填写账号。");
            }
            if (string.IsNullOrEmpty(settings.SecretId)) return new(settings, "");
            try
            {
                var secrets = ReadSecrets();
                if (!secrets.TryGetValue(settings.SecretId, out var password)) return new(settings, "", "未找到对应密码，请重新输入。");
                return new(settings, password);
            }
            catch (Exception e) when (e is CryptographicException or JsonException or IOException)
            { return new(settings, "", "密码无法解密，请使用原 Windows 用户或重新输入密码。"); }
        }
    }
    public void Save(AppSettings settings, string password)
    {
        var errors = settings.Validate();
        if (errors.Count != 0) throw new ArgumentException(string.Join("\n", errors));
        lock (gate)
        {
            EnsureDirectory();
            var previous = Load();
            var id = Guid.NewGuid().ToString("N");
            var secrets = new Dictionary<string, string> { [id] = password };
            // 两代密钥并存，使配置切换前后的文件都能解密。
            if (!string.IsNullOrEmpty(previous.Settings.SecretId)) secrets[previous.Settings.SecretId] = previous.Password;
            var clear = JsonSerializer.SerializeToUtf8Bytes(secrets, Options);
            try { AtomicWrite(Path.Combine(directory, "secrets.dat"), Dpapi.Protect(clear)); }
            finally { CryptographicOperations.ZeroMemory(clear); }
            AtomicWrite(Path.Combine(directory, "config.json"), JsonSerializer.SerializeToUtf8Bytes(settings with { Version = 1, SecretId = id }, Options));
        }
    }
    private Dictionary<string, string> ReadSecrets()
    {
        var clear = Dpapi.Unprotect(File.ReadAllBytes(Path.Combine(directory, "secrets.dat")));
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(clear, Options) ?? throw new JsonException(); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    private void EnsureDirectory()
    {
        Directory.CreateDirectory(directory);
        if (!restrictPermissions) return;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }
    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
