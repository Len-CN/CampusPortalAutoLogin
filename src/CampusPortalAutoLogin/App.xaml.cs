using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CampusPortalAutoLogin.Core.Logging;
using CampusPortalAutoLogin.Core.Models;
using CampusPortalAutoLogin.Core.Protocol;
using CampusPortalAutoLogin.Core.Scheduling;
using CampusPortalAutoLogin.Core.Services;
using CampusPortalAutoLogin.Tray;
using CampusPortalAutoLogin.Views;

namespace CampusPortalAutoLogin;

public partial class App : Application
{
    private Mutex? mutex;
    private EventWaitHandle? showEvent, stopEvent;
    private RegisteredWaitHandle? showWait, stopWait;
    private SettingsStore? store;
    private SettingsSnapshot snapshot = new(new(), "");
    private LoginCoordinator? coordinator;
    private SchedulerService? scheduler;
    private AutostartService? autostart;
    private TrayService? tray;
    private SettingsWindow? settingsWindow;
    private LogWindow? logWindow;
    private bool exiting, ownsMutex;
    private readonly SemaphoreSlim configurationGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private string? lastNotification;
    private DateTime lastNotificationTime;
    public bool IsExiting => exiting;
    public LogService Logs { get; private set; } = null!;
    public LoginResult LastResult { get; private set; } = new(LoginState.Idle, "就绪");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, error) =>
        {
            Logs?.Write(LogLevel.Error, "应用", "界面异常：" + error.Exception.GetType().Name);
            error.Handled = true;
            tray?.Notify("操作失败，请查看日志。", true);
        };
        TaskScheduler.UnobservedTaskException += (_, error) => { Logs?.Write(LogLevel.Error, "应用", "后台异常：" + error.Exception.GetType().Name); error.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Logs?.Write(LogLevel.Error, "应用", "应用发生未处理异常，即将退出。");
        try
        {
            ApplyAccessibility();
            SystemParameters.StaticPropertyChanged += SystemSettingsChanged;
            if (e.Args.FirstOrDefault() == "--smoke-test")
            {
                if (e.Args.Length != 2) { Shutdown(2); return; }
                await SmokeAsync(Path.GetFullPath(e.Args[1])); Shutdown(); return;
            }
            var sid = WindowsIdentity.GetCurrent().User!.Value;
            var instanceName = "Local\\CampusPortalAutoLogin-" + sid;
            if (e.Args.Contains("--shutdown"))
            {
                if (EventWaitHandle.TryOpenExisting(instanceName + "-stop", out var signal)) { using (signal) signal.Set(); }
                Shutdown(); return;
            }
            Logs = new LogService(Path.Combine(SettingsStore.DefaultDirectory, "logs"));
            autostart = new AutostartService(Logs);
            if (e.Args.Contains("--remove-autostart")) { autostart.Apply(false, Environment.ProcessPath!); Shutdown(); return; }
            mutex = new Mutex(true, instanceName, out var created); ownsMutex = created;
            if (!created)
            {
                if (EventWaitHandle.TryOpenExisting(instanceName + "-show", out var signal)) { using (signal) signal.Set(); }
                Shutdown(); return;
            }
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + "-show");
            stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + "-stop");
            showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => Dispatcher.BeginInvoke(ShowSettings), null, Timeout.Infinite, false);
            stopWait = ThreadPool.RegisterWaitForSingleObject(stopEvent, (_, _) => Dispatcher.BeginInvoke(async () => await ExitAsync()), null, Timeout.Infinite, false);
            store = new SettingsStore(SettingsStore.DefaultDirectory);
            snapshot = store.Load();
            Logs.Redactor.SetCredentials(snapshot.Settings.Username, snapshot.Password);
            coordinator = new LoginCoordinator(new EportalLoginFlow(Logs), Logs);
            coordinator.ProgressChanged += progress => Dispatcher.BeginInvoke(() =>
            {
                tray?.Update(progress.State, "校园网自动登录 · " + Logs.Redactor.Redact(progress.Message), coordinator.IsBusy);
                settingsWindow?.UpdateStatus(progress);
            });
            coordinator.Completed += result => Dispatcher.BeginInvoke(() => Complete(result));
            tray = new TrayService(() => _ = TriggerAsync("立即登录"), ShowSettings, ShowLogs, ShowAbout, () => _ = ExitAsync());
            scheduler = new SchedulerService(TriggerAsync, Logs);
            SessionEnding += (_, _) => { coordinator?.Cancel(); _ = ExitAsync(); };
            Logs.Write(LogLevel.Info, "应用", "程序已启动。");
            if (!snapshot.Settings.PrivacyAcknowledged)
            {
                MessageBox.Show("账号仅保存于本机；密码使用当前 Windows 用户密钥加密。\n\n日志只记录脱敏后的网络状态和认证结果，不上传遥测。您可随时删除配置和日志。\n\n首次请填写账号并保存设置后启用自动运行。", "校园网自动登录 · 隐私说明", MessageBoxButton.OK, MessageBoxImage.Information);
                ShowSettings();
            }
            else
            {
                try { await Task.Run(() => autostart.Apply(snapshot.Settings.Autostart, Environment.ProcessPath!)); }
                catch (Exception error) { Logs.Write(LogLevel.Error, "自启", "任务更新失败：" + error.GetType().Name); tray.Notify("开机自启更新失败，请检查任务计划程序权限。", true); }
                if (exiting) return;
                scheduler.Apply(snapshot.Settings);
                if (snapshot.Warning is not null || e.Args.Contains("--settings")) ShowSettings();
            }
        }
        catch (Exception error)
        {
            // 不输出异常原文，避免网络地址或凭据被第三方异常消息带出。
            if (e.Args.Contains("--smoke-test"))
            {
                if (e.Args.Length == 2) { Directory.CreateDirectory(e.Args[1]); File.WriteAllText(Path.Combine(e.Args[1], "smoke-error.txt"), error.ToString()); }
            }
            else MessageBox.Show("启动失败：" + error.GetType().Name + "。请检查配置目录和系统权限。", "校园网自动登录", MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync(1);
        }
    }
    private void SystemSettingsChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(ApplyAccessibility);
    private void ApplyAccessibility()
    {
        if (SystemParameters.HighContrast)
        {
            Resources["BackgroundBrush"] = SystemColors.WindowBrush; Resources["PanelBrush"] = SystemColors.WindowBrush;
            Resources["PopupBrush"] = SystemColors.WindowBrush; Resources["AccentBrightBrush"] = SystemColors.WindowTextBrush; Resources["InputBrush"] = SystemColors.WindowBrush; Resources["TextBrush"] = SystemColors.WindowTextBrush;
            Resources["MutedBrush"] = SystemColors.WindowTextBrush; Resources["AccentBrush"] = SystemColors.HighlightBrush; Resources["BorderBrush"] = SystemColors.WindowTextBrush;
        }
        else foreach (var key in new[] { "PopupBrush", "AccentBrightBrush", "BackgroundBrush", "PanelBrush", "InputBrush", "TextBrush", "MutedBrush", "AccentBrush", "BorderBrush" }) Resources.Remove(key);
    }
    private async Task TriggerAsync(string source)
    {
        if (coordinator is null || exiting) return;
        var current = snapshot;
        if (string.IsNullOrWhiteSpace(current.Settings.Username) || string.IsNullOrEmpty(current.Password))
        {
            Logs.Write(LogLevel.Warning, source, "未配置完整凭据，已跳过登录。");
            if (source == "立即登录") await Dispatcher.InvokeAsync(ShowSettings);
            return;
        }
        await coordinator.TriggerAsync(source, current.Settings, new(current.Settings.Username, current.Password, current.Settings.ServiceProvider));
    }
    public Task<LoginResult> TestAsync(AppSettings settings, string password, CancellationToken token)
    {
        Logs.Redactor.AddCredentials(settings.Username, password);
        return coordinator!.TriggerAsync("测试登录", settings, new(settings.Username, password, settings.ServiceProvider), token);
    }
    public async Task<string?> SaveAsync(AppSettings settings, string password)
    {
        await configurationGate.WaitAsync(lifetime.Token);
        try
        {
        lifetime.Token.ThrowIfCancellationRequested();
        Logs.Redactor.AddCredentials(settings.Username, password);
        await Task.Run(() => store!.Save(settings, password));
        lifetime.Token.ThrowIfCancellationRequested();
        snapshot = new(settings, password); Logs.Redactor.SetCredentials(settings.Username, password);
        scheduler!.Apply(settings);
        try { await Task.Run(() => autostart!.Apply(settings.Autostart, Environment.ProcessPath!)); }
        catch (Exception e) { Logs.Write(LogLevel.Error, "自启", "任务更新失败：" + e.GetType().Name); return "配置已保存，但开机自启更新失败，请检查任务计划程序权限后再次保存。"; }
        Logs.Write(LogLevel.Success, "设置", "配置已保存，调度已更新。"); return null;
        }
        finally { configurationGate.Release(); }
    }
    private void Complete(LoginResult result)
    {
        if (exiting) return;
        result = result with { Message = Logs.Redactor.Redact(result.Message) };
        LastResult = result;
        tray?.Update(result.State, result.IsSuccess ? $"校园网自动登录 · {result.Message} {DateTime.Now:HH:mm:ss}" : "校园网自动登录 · " + result.Message, coordinator?.IsBusy ?? false);
        settingsWindow?.UpdateResult(result);
        if (result.State == LoginState.Cancelled) return;
        if (result.Message != lastNotification || DateTime.Now - lastNotificationTime > TimeSpan.FromMinutes(5))
        {
            tray?.Notify(result.Message, !result.IsSuccess); lastNotification = result.Message; lastNotificationTime = DateTime.Now;
        }
        if (result.State == LoginState.CaptchaRequired) { ShowSettings(); settingsWindow?.UpdateResult(result); }
    }
    private void ShowSettings()
    {
        if (exiting) return;
        if (settingsWindow is null) { settingsWindow = new SettingsWindow(snapshot, this); settingsWindow.Closed += (_, _) => settingsWindow = null; }
        settingsWindow.Show(); settingsWindow.WindowState = WindowState.Normal; settingsWindow.Activate();
    }
    private void ShowLogs()
    {
        if (logWindow is null) { logWindow = new LogWindow(Logs); logWindow.Closed += (_, _) => logWindow = null; }
        logWindow.Show(); logWindow.WindowState = WindowState.Normal; logWindow.Activate();
    }
    private void ShowAbout() => MessageBox.Show("校园网自动登录 1.0.3\n\nWindows 10/11 · ePortal\n账号与日志仅保存于本机，无遥测。\n验证码需要在校园网原页面人工完成。", "关于", MessageBoxButton.OK, MessageBoxImage.Information);
    public async Task ExitAsync(int code = 0)
    {
        if (exiting) return; exiting = true;
        lifetime.Cancel(); coordinator?.Cancel();
        settingsWindow?.Close(); logWindow?.Close();
        if (scheduler is not null) await scheduler.DisposeAsync();
        if (coordinator is not null) await coordinator.DisposeAsync();
        await configurationGate.WaitAsync(); configurationGate.Release();
        tray?.Dispose(); tray = null;
        Shutdown(code);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= SystemSettingsChanged;
        showWait?.Unregister(null); stopWait?.Unregister(null); showEvent?.Dispose(); stopEvent?.Dispose();
        tray?.Dispose(); if (ownsMutex) mutex?.ReleaseMutex(); mutex?.Dispose(); base.OnExit(e);
    }
    private async Task SmokeAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var priorError = Path.Combine(directory, "smoke-error.txt"); if (File.Exists(priorError)) File.Delete(priorError);
        Logs = new LogService(Path.Combine(directory, "sample-logs"));
        Logs.Clear();
        Logs.Write(LogLevel.Info, "网络变化", "检测到网络变化，等待 3 秒稳定。");
        Logs.Write(LogLevel.Info, "探测", "发现 ePortal 认证门户，动态参数已隐藏。");
        Logs.Write(LogLevel.Success, "登录", "示例：登录成功。此处为界面测试数据，未访问网络。");
        Logs.Write(LogLevel.Warning, "定时执行", "示例：认证时段尚未开始，已停止重试。");
        foreach (var message in new[] { "状态：空闲，等待下一次触发。", "网络地址发生变化。", "防抖结束，开始探测。", "联网标记匹配，无需认证。", "配置已保存，调度已更新。", "计划任务路径已检查。", "下次定时执行：明天 07:00。", "本轮日志已保留。" }) Logs.Write(LogLevel.Info, "界面示例", message);
        var settings = new SettingsWindow(new(new AppSettings { Username = "示例账号" }, "demo-password"));
        settings.Show(); await Task.Delay(750); settings.UpdateResult(new(LoginState.Online, "网络已连通 · 无需重复登录")); await Task.Delay(500);
        var heroTitle = (System.Windows.Controls.TextBlock)settings.FindName("HeroTitle");
        if (Animations.MotionService.Enabled && (heroTitle.Foreground is not LinearGradientBrush titleBrush || !titleBrush.RelativeTransform.HasAnimatedProperties))
            throw new InvalidOperationException("Text Gradient Flow 未挂载。");
        if (settings.FindName("CursorAura") is not System.Windows.Shapes.Ellipse)
            throw new InvalidOperationException("Cursor Aura 未挂载。");
        if (FindVisual<Animations.BeamBorder>(settings) is null)
            throw new InvalidOperationException("Border Beam 未挂载。");
        var flowTransform = (heroTitle.Foreground as LinearGradientBrush)?.RelativeTransform as TranslateTransform;
        var initialFlow = flowTransform?.X ?? 0;
        settings.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = System.Windows.Input.Mouse.MouseMoveEvent });
        var cursorAura = (System.Windows.Shapes.Ellipse)settings.FindName("CursorAura");
        if (Animations.MotionService.Enabled && cursorAura.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Cursor Aura 未响应窗口内指针移动。");
        var cursorLayer = (System.Windows.Controls.Canvas)settings.FindName("CursorLayer");
        // 改变相对坐标来模拟一批鼠标移动，无需移动用户真实指针。
        if (Animations.MotionService.Enabled)
        {
            Point expected = default;
            for (var index = 1; index <= 20; index++)
            {
                cursorLayer.RenderTransform = new TranslateTransform(index * 6, index * 4);
                expected = System.Windows.Input.Mouse.GetPosition(cursorLayer);
                settings.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount)
                    { RoutedEvent = System.Windows.Input.Mouse.MouseMoveEvent });
            }
            await Task.Delay(80);
            var cursorTransform = (TranslateTransform)cursorAura.RenderTransform;
            if (Math.Abs(cursorTransform.X + cursorAura.Width / 2 - expected.X) > 1 || Math.Abs(cursorTransform.Y + cursorAura.Height / 2 - expected.Y) > 1)
                throw new InvalidOperationException("Cursor Aura 未及时跟随到最新指针坐标。");
            cursorLayer.RenderTransform = Transform.Identity;
        }
        settings.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent });
        if (cursorAura.Visibility != Visibility.Collapsed) throw new InvalidOperationException("指针离开后 Cursor Aura 未隐藏。");
        var lightPanel = (System.Windows.Controls.Panel)settings.FindName("Lights");
        var lightTransform = (TranslateTransform)lightPanel.Children[0].RenderTransform;
        var initialLightX = lightTransform.X;
        await Task.Delay(1100);
        if (Animations.MotionService.Enabled && Math.Abs(lightTransform.X - initialLightX) < .1)
            throw new InvalidOperationException("背景光晕没有随时间移动。");
        if (Animations.MotionService.Enabled && Math.Abs((flowTransform?.X ?? 0) - initialFlow) < .01)
            throw new InvalidOperationException("标题渐变没有随时间流动。");
        foreach (var scale in new[] { 1d, 1.25, 1.5 }) Capture(settings, Path.Combine(directory, $"settings-{scale:0.##}.png"), scale);
        var logs = new LogWindow(Logs); logs.Show(); await Task.Delay(900); Capture(logs, Path.Combine(directory, "logs.png"), 1);
        var filter = (System.Windows.Controls.ComboBox)logs.FindName("Filter"); filter.SelectedIndex = 2;
        var list = (System.Windows.Controls.ListBox)logs.FindName("Entries");
        if (list.Items.Count != 1 || ((LogEntry)list.Items[0]).Level != LogLevel.Success) throw new InvalidOperationException("日志级别筛选失败。");
        filter.SelectedIndex = 0;
        var autoScroll = (System.Windows.Controls.CheckBox)logs.FindName("AutoScroll"); autoScroll.IsChecked = false;
        var selectedEntry = list.Items[0]; list.SelectedItem = selectedEntry;
        Logs.Write(LogLevel.Info, "测试", "追加时保留选择");
        await Task.Delay(300);
        if (!ReferenceEquals(list.SelectedItem, selectedEntry)) throw new InvalidOperationException("新日志追加后用户选择被清除。");
        await Task.Run(() => { Logs.Write(LogLevel.Error, "测试", "即将清空的记录"); Logs.Clear(); });
        await Task.Delay(300);
        if (list.Items.Count != 0 || Logs.ReadRecent().Count != 0) throw new InvalidOperationException("清空日志后窗口仍有旧记录。");
        Logs.Write(LogLevel.Warning, "测试", "清空后的新记录");
        await Task.Delay(300);
        if (list.Items.Count != 1 || ((LogEntry)list.Items[0]).Message != "清空后的新记录") throw new InvalidOperationException("清空后新日志未正确显示。");
        var settingsCaption = FindVisual<WindowTitleBar>(settings)!;
        ((System.Windows.Controls.Button)settingsCaption.FindName("MinimizeButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (settings.WindowState != WindowState.Minimized) throw new InvalidOperationException("自绘最小化按钮无效。");
        if (lightTransform.HasAnimatedProperties) throw new InvalidOperationException("窗口最小化后背景动画仍在运行。");
        if (flowTransform?.HasAnimatedProperties == true) throw new InvalidOperationException("窗口最小化后标题渐变仍在运行。");
        settings.WindowState = WindowState.Normal;
        if (Animations.MotionService.Enabled && !lightTransform.HasAnimatedProperties) throw new InvalidOperationException("还原窗口后背景动画未恢复。");
        var logCaption = FindVisual<WindowTitleBar>(logs)!;
        ((System.Windows.Controls.Button)logCaption.FindName("MaximizeButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        if (logs.WindowState != WindowState.Maximized) throw new InvalidOperationException("自绘最大化按钮无效。");
        ((System.Windows.Controls.Button)logCaption.FindName("MaximizeButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        ((System.Windows.Controls.Button)logCaption.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        ((System.Windows.Controls.Button)settingsCaption.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await Task.Delay(250);
        if (logs.IsVisible || settings.IsVisible) throw new InvalidOperationException("自绘关闭按钮无效。");
        File.WriteAllText(Path.Combine(directory, "smoke-result.txt"), "WPF 设置与日志窗口创建、布局、渲染，自绘最小化/最大化/还原/关闭，日志筛选通过；验证批量鼠标移动后光效及时到达最新位置、离开隐藏，标题与环境光运动，最小化暂停/还原恢复；验证后台清日志后窗口清空、后续新记录正常显示。已输出 100%/125%/150% 像素预览。未发网络、未修改真实配置与计划任务。像素渲染不替代实机 DPI 切换或 GPU 帧率测量。\n");
    }
    private static void Capture(Window window, string path, double scale)
    {
        window.UpdateLayout();
        var visual = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * scale), (int)Math.Ceiling(visual.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var result = FindVisual<T>(VisualTreeHelper.GetChild(root, i)); if (result is not null) return result; }
        return null;
    }
}
