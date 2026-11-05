using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CampusPortalAutoLogin.Animations;
using CampusPortalAutoLogin.Core.Models;
using Forms = System.Windows.Forms;

namespace CampusPortalAutoLogin.Tray;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ToolStripMenuItem login;
    private readonly Forms.ContextMenuStrip menu;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private Icon? ownedIcon;
    private LoginState state;
    private int frame;
    public TrayService(Action immediate, Action settings, Action logs, Action about, Action exit)
    {
        menu = new Forms.ContextMenuStrip();
        login = new Forms.ToolStripMenuItem("立即登录", null, (_, _) => immediate());
        menu.Items.Add(login); menu.Items.Add("设置", null, (_, _) => settings()); menu.Items.Add("查看日志", null, (_, _) => logs()); menu.Items.Add("关于", null, (_, _) => about()); menu.Items.Add("退出", null, (_, _) => exit());
        icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Text = "校园网自动登录 · 就绪", Visible = true };
        icon.DoubleClick += (_, _) => settings();
        timer.Tick += (_, _) => { if (MotionService.Enabled) { frame++; Draw(); } };
        Draw();
    }
    public void Update(LoginState value, string summary, bool busy)
    {
        state = value; login.Enabled = !busy;
        icon.Text = summary.Length > 63 ? summary[..63] : summary;
        if (busy) timer.Start(); else timer.Stop();
        Draw();
    }
    public void Notify(string message, bool error)
    {
        icon.BalloonTipTitle = "校园网自动登录"; icon.BalloonTipText = message.Length > 200 ? message[..200] : message;
        icon.BalloonTipIcon = error ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info; icon.ShowBalloonTip(4000);
    }
    private void Draw()
    {
        var color = state switch { LoginState.Success or LoginState.Online => Color.FromArgb(85, 214, 166), LoginState.Failed => Color.FromArgb(255, 125, 138), LoginState.Offline or LoginState.Warning or LoginState.CaptchaRequired => Color.FromArgb(242, 198, 109), _ => Color.FromArgb(85, 199, 217) };
        using var bitmap = new Bitmap(32, 32); using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var back = new SolidBrush(Color.FromArgb(11, 19, 34)); using var pen = new Pen(color, 3); using var brush = new SolidBrush(color);
        graphics.FillEllipse(back, 1, 1, 30, 30); graphics.DrawArc(pen, 6, 6, 20, 20, 205, 130); graphics.DrawArc(pen, 10, 11, 12, 12, 205, 130); graphics.FillEllipse(brush, 14, 20, 4, 4);
        if (timer.IsEnabled && MotionService.Enabled) { var angle = frame * Math.PI / 3; graphics.FillEllipse(brush, 14 + (float)Math.Cos(angle) * 12, 14 + (float)Math.Sin(angle) * 12, 4, 4); }
        var handle = bitmap.GetHicon();
        try { using var temporary = Icon.FromHandle(handle); var next = (Icon)temporary.Clone(); icon.Icon = next; ownedIcon?.Dispose(); ownedIcon = next; }
        finally { DestroyIcon(handle); }
    }
    public void Dispose() { timer.Stop(); icon.Visible = false; icon.Dispose(); ownedIcon?.Dispose(); menu.Dispose(); }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
}
