using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using CampusPortalAutoLogin.Core.Models;

namespace CampusPortalAutoLogin.ViewModels;

public class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Notify(name); return true; }
}
public sealed class AsyncCommand(Func<Task> execute) : ICommand
{
    private bool busy;
    public bool CanExecute(object? parameter) => !busy;
    public event EventHandler? CanExecuteChanged;
    public async void Execute(object? parameter)
    {
        if (busy) return; busy = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(); }
        finally { busy = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}
public sealed record ProviderOption(string Id, string Name) { public override string ToString() => Name; }
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings original;
    public static ProviderOption[] Providers { get; } = [new("campus", "校园网"), new("mobile", "中国移动"), new("unicom", "中国联通"), new("telecom", "中国电信")];
    public SettingsViewModel(AppSettings settings)
    {
        original = settings; username = settings.Username; provider = settings.ServiceProvider; autostart = settings.Autostart;
        network = settings.NetworkChange; startup = settings.RunOnStartup; detect = settings.AutoDetect; interval = settings.DetectIntervalSeconds.ToString(); scheduled = settings.ScheduledLogin; time = settings.ScheduledTime;
    }
    private string username, provider, interval, time, status = "就绪 · 配置保存在本机";
    private bool autostart, network, startup, detect, scheduled, busy;
    public string Username { get => username; set => Set(ref username, value); }
    public string ServiceProvider { get => provider; set => Set(ref provider, value); }
    public bool Autostart { get => autostart; set => Set(ref autostart, value); }
    public bool NetworkChange { get => network; set => Set(ref network, value); }
    public bool RunOnStartup { get => startup; set => Set(ref startup, value); }
    public bool AutoDetect { get => detect; set => Set(ref detect, value); }
    public string DetectInterval { get => interval; set => Set(ref interval, value); }
    public bool ScheduledLogin { get => scheduled; set => Set(ref scheduled, value); }
    public string ScheduledTime { get => time; set => Set(ref time, value); }
    public string Status { get => status; set => Set(ref status, value); }
    public bool IsBusy { get => busy; set { if (Set(ref busy, value)) Notify(nameof(CanEdit)); } }
    public bool CanEdit => !busy;
    public ICommand? SaveCommand { get; set; }
    public ICommand? TestCommand { get; set; }
    public ICommand? CancelCommand { get; set; }
    public AppSettings Build() => original with { Username = Username.Trim(), ServiceProvider = ServiceProvider, Autostart = Autostart, NetworkChange = NetworkChange, RunOnStartup = RunOnStartup, AutoDetect = AutoDetect, DetectIntervalSeconds = int.TryParse(DetectInterval, out var n) ? n : 0, ScheduledLogin = ScheduledLogin, ScheduledTime = ScheduledTime, PrivacyAcknowledged = true };
}
