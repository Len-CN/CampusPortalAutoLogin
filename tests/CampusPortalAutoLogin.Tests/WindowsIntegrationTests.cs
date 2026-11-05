using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using CampusPortalAutoLogin.Core.Models;
using CampusPortalAutoLogin.Core.Services;

namespace CampusPortalAutoLogin.Tests;

// 显式 WindowsIntegrationTests 过滤才执行；只创建带随机名称的任务及工作区临时文件。
public static class WindowsIntegrationTests
{
    [Case] public static void 当前用户目录ACL与DPAPI存储()
    {
        var path = Path.Combine(Environment.CurrentDirectory, ".tools", "acl-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings { Username = "local-test" }, "temporary-test-password");
            Check.Equal("temporary-test-password", store.Load().Password);
            var security = new DirectoryInfo(path).GetAccessControl();
            Check.True(security.AreAccessRulesProtected);
            var allowed = new[] { WindowsIdentity.GetCurrent().User!.Value, "S-1-5-18" };
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                Check.True(allowed.Contains(rule.IdentityReference.Value));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
    [Case] public static void 创建更新查询删除隔离计划任务()
    {
        var name = "CampusPortalAutoLogin-Verification-" + Guid.NewGuid().ToString("N");
        var service = new AutostartService(new NullLog(), name);
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");
        try
        {
            Check.True(!service.IsEnabled());
            service.Apply(true, executable); Check.True(service.IsEnabled());
            service.Apply(true, executable); Check.True(service.IsEnabled());
            service.Apply(false, executable); Check.True(!service.IsEnabled());
        }
        finally { service.Apply(false, executable); }
    }
    private sealed class NullLog : ILogSink { public void Write(LogLevel level, string source, string message) { } public void Clear() { } }
}
