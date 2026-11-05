using System.Net.NetworkInformation;
using Microsoft.Win32;
using CampusPortalAutoLogin.Core.Models;

namespace CampusPortalAutoLogin.Core.Scheduling;

public sealed class SchedulerService : IAsyncDisposable
{
    private readonly Func<string, Task> trigger;
    private readonly ILogSink log;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private readonly ScheduleClock clock = new();
    private readonly Task loop;
    private AppSettings settings = new();
    private DateTime nextDetect;
    private CancellationTokenSource? networkDebounce;
    private DateTime lastNetworkTrigger = DateTime.MinValue;
    private bool started;
    private string? networkFingerprint;
    private int networkPollTick;
    public SchedulerService(Func<string, Task> trigger, ILogSink log)
    {
        this.trigger = trigger; this.log = log;
        NetworkChange.NetworkAddressChanged += NetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += AvailabilityChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.TimeChanged += TimeChanged;
        loop = RunAsync();
    }
    public void Apply(AppSettings value)
    {
        lock (sync)
        {
            if (lifetime.IsCancellationRequested) return;
            settings = value; clock.Reset(DateTime.Now, value);
            nextDetect = DateTime.Now.AddSeconds(value.DetectIntervalSeconds);
            networkDebounce?.Cancel();
            if (!started) { started = true; if (value.RunOnStartup) _ = SafeTrigger("启动运行"); }
        }
    }
    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                bool daily, detect;
                lock (sync)
                {
                    var now = DateTime.Now;
                    daily = started && clock.TakeDue(now, settings);
                    detect = started && settings.AutoDetect && now >= nextDetect;
                    if (detect) nextDetect = now.AddSeconds(settings.DetectIntervalSeconds);
                }
                if (daily) _ = SafeTrigger("定时执行");
                if (detect) _ = SafeTrigger("自动检测");
                if (++networkPollTick % 5 == 0)
                {
                    try
                    {
                        var value = NetworkFingerprint.Read();
                        if (networkFingerprint is not null && networkFingerprint != value) DebounceNetwork();
                        networkFingerprint = value;
                    }
                    catch (Exception e) when (e is NetworkInformationException or System.ComponentModel.Win32Exception or DllNotFoundException) { }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    private void NetworkChanged(object? sender, EventArgs e) => DebounceNetwork();
    private void AvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) { if (e.IsAvailable) DebounceNetwork(); }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) DebounceNetwork(); }
    private void TimeChanged(object? sender, EventArgs e)
    {
        // tick 使用本地时间；向前调整跨过执行点可补一次，向后调整仍按日期去重。
        lock (sync) nextDetect = DateTime.Now.AddSeconds(settings.DetectIntervalSeconds);
    }
    private void DebounceNetwork()
    {
        lock (sync)
        {
            if (!started || !settings.NetworkChange || lifetime.IsCancellationRequested) return;
            networkDebounce?.Cancel(); networkDebounce?.Dispose();
            networkDebounce = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            _ = AfterNetworkAsync(networkDebounce.Token);
        }
    }
    private async Task AfterNetworkAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            lock (sync)
            {
                if ((DateTime.UtcNow - lastNetworkTrigger).TotalSeconds < 3) return;
                lastNetworkTrigger = DateTime.UtcNow;
            }
            await SafeTrigger("网络变化").ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }
    private async Task SafeTrigger(string source)
    {
        if (lifetime.IsCancellationRequested) return;
        try { await trigger(source).ConfigureAwait(false); }
        catch (Exception e) { log.Write(LogLevel.Error, source, "调度失败：" + e.GetType().Name); }
    }
    public async ValueTask DisposeAsync()
    {
        NetworkChange.NetworkAddressChanged -= NetworkChanged; NetworkChange.NetworkAvailabilityChanged -= AvailabilityChanged;
        SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.TimeChanged -= TimeChanged;
        lifetime.Cancel();
        lock (sync) networkDebounce?.Cancel();
        await loop.ConfigureAwait(false);
    }
}
