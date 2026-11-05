using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using CampusPortalAutoLogin.Core.Models;

namespace CampusPortalAutoLogin.Core.Services;

public sealed class AutostartService(ILogSink log, string? taskName = null)
{
    public const string TaskName = "CampusPortalAutoLogin";
    public string RegistrationName { get; } = taskName ?? TaskName + "-" + WindowsIdentity.GetCurrent().User!.Value;
    public bool IsEnabled()
    {
        dynamic? service = null, folder = null, task = null;
        try { service = Connect(); folder = service.GetFolder("\\"); task = folder.GetTask(RegistrationName); return task.Enabled; }
        catch (Exception e) when ((uint)e.HResult is 0x80070002 or 0x80070003) { return false; }
        finally { Release(task); Release(folder); Release(service); }
    }
    public void Apply(bool enabled, string executable)
    {
        dynamic? service = null, folder = null, existing = null, registered = null;
        try
        {
            service = Connect(); folder = service.GetFolder("\\");
            if (!enabled)
            {
                try { folder.DeleteTask(RegistrationName, 0); log.Write(LogLevel.Info, "自启", "已删除登录任务。"); }
                catch (Exception e) when ((uint)e.HResult is 0x80070002 or 0x80070003) { log.Write(LogLevel.Info, "自启", "登录任务原本不存在。"); }
                return;
            }
            var sid = WindowsIdentity.GetCurrent().User!.Value;
            // CREATE_OR_UPDATE 保留旧任务直到新定义成功注册，避免先删后建失败丢失自启。
            registered = folder.RegisterTask(RegistrationName, CreateXml(executable, sid), 6, sid, null, 3, null);
            log.Write(LogLevel.Info, "自启", "已创建或更新当前用户登录任务，延迟 10 秒启动。");
        }
        finally { Release(registered); Release(existing); Release(folder); Release(service); }
    }
    public static string CreateXml(string executable, string sid)
    {
        var path = SecurityElement.Escape(System.IO.Path.GetFullPath(executable));
        var user = SecurityElement.Escape(sid);
        return $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>校园网自动登录：当前用户登录后运行</Description></RegistrationInfo>
          <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>
          <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
          <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Enabled>true</Enabled></Settings>
          <Actions Context="Author"><Exec><Command>{path}</Command><WorkingDirectory>{SecurityElement.Escape(System.IO.Path.GetDirectoryName(executable))}</WorkingDirectory></Exec></Actions>
        </Task>
        """;
    }
    private static dynamic Connect() { var service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!; ((dynamic)service).Connect(); return service; }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
