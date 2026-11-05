using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CampusPortalAutoLogin.Animations;
using CampusPortalAutoLogin.Core.Models;
using CampusPortalAutoLogin.ViewModels;

namespace CampusPortalAutoLogin.Views;

public partial class SettingsWindow : Window
{
    private readonly App? app;
    private readonly CancellationTokenSource closing = new();
    private readonly DispatcherTimer spinner = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private Uri? portalUri;
    private bool closeAnimated;
    public SettingsViewModel Model { get; }
    public SettingsWindow(SettingsSnapshot snapshot, App? app = null)
    {
        InitializeComponent(); this.app = app; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowFrame.Attach(this, WindowBorder);
        MotionService.AttachAmbientMotion(this, Lights);
        MotionService.AttachCursorAura(this, CursorLayer, CursorAura);
        MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 30);
        Model = new(snapshot.Settings) { SaveCommand = new AsyncCommand(SaveAsync), TestCommand = new AsyncCommand(TestAsync), CancelCommand = new AsyncCommand(() => { Close(); return Task.CompletedTask; }) };
        DataContext = Model; PasswordInput.Password = snapshot.Password;
        if (snapshot.Warning is not null) Model.Status = snapshot.Warning;
        Loaded += (_, _) => { if (SystemParameters.HighContrast) Lights.Visibility = Visibility.Collapsed; MotionService.Enter(Surface); MotionService.BlurIn(Lights); MotionService.AttachTextGradientFlow(HeroTitle); MotionService.WireHover(Surface); };
        Closing += async (_, args) =>
        {
            closing.Cancel();
            if (!closeAnimated && MotionService.Enabled && app?.IsExiting != true)
            {
                args.Cancel = true; closeAnimated = true;
                await MotionService.ExitAsync(Surface); Close();
            }
        };
        Closed += (_, _) => { spinner.Stop(); PasswordInput.Clear(); closing.Dispose(); };
        var frame = 0;
        spinner.Tick += (_, _) => StatusDot.Text = MotionService.Enabled ? new[] { "● · ·", "· ● ·", "· · ●" }[frame++ % 3] : "●";
    }
    public void UpdateStatus(LoginProgress progress)
    {
        Model.Status = app?.Logs.Redactor.Redact(progress.Message) ?? progress.Message;
        StatusDot.Foreground = new SolidColorBrush(StateColor(progress.State));
        if (progress.State is LoginState.Probing or LoginState.LoggingIn) spinner.Start(); else { spinner.Stop(); StatusDot.Text = "●"; }
        MotionService.Swap(StatusText);
    }
    public void UpdateResult(LoginResult result)
    {
        UpdateStatus(new(result.State, result.Message));
        portalUri = result.PortalUri;
        PortalButton.Visibility = result.PortalUri is not null && !result.IsSuccess ? Visibility.Visible : Visibility.Collapsed;
    }
    private bool Validate(bool test)
    {
        var errors = Model.Build().Validate().ToList();
        if (test && string.IsNullOrEmpty(PasswordInput.Password)) errors.Add("测试登录前请输入密码。");
        if (errors.Count == 0) return true;
        Model.Status = string.Join(" ", errors); StatusDot.Foreground = new SolidColorBrush(StateColor(LoginState.Failed)); MotionService.Shake(AccountForm); return false;
    }
    private async Task SaveAsync()
    {
        if (!Validate(false) || app is null) return;
        Model.IsBusy = true;
        try
        {
            var result = await app.SaveAsync(Model.Build(), PasswordInput.Password);
            if (!closing.IsCancellationRequested) { if (result is null) Close(); else Model.Status = result; }
        }
        catch (Exception e) { if (!closing.IsCancellationRequested) Model.Status = "保存失败：" + e.GetType().Name; }
        finally { Model.IsBusy = false; }
    }
    private async Task TestAsync()
    {
        if (!Validate(true) || app is null) return;
        Model.IsBusy = true; PortalButton.Visibility = Visibility.Collapsed;
        try
        {
            var s = Model.Build();
            var result = await app.TestAsync(s, PasswordInput.Password, closing.Token);
            if (!closing.IsCancellationRequested) UpdateResult(result);
        }
        catch (OperationCanceledException) { }
        finally { Model.IsBusy = false; }
    }
    private void OpenPortal(object sender, RoutedEventArgs e)
    {
        if (portalUri?.Scheme is not ("http" or "https")) return;
        try { Process.Start(new ProcessStartInfo(portalUri.AbsoluteUri) { UseShellExecute = true }); }
        catch { Model.Status = "无法打开默认浏览器，请手工打开校园网认证页面。"; }
    }
    public static Color StateColor(LoginState state) => (Color)ColorConverter.ConvertFromString(state switch { LoginState.Success or LoginState.Online => "#55D6A6", LoginState.Failed => "#FF7D8A", LoginState.Warning or LoginState.Offline or LoginState.CaptchaRequired => "#F2C66D", LoginState.Idle => "#8FA6B8", _ => "#55C7D9" });
}
