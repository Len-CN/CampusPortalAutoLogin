using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace CampusPortalAutoLogin.Views;

internal static class WindowFrame
{
    public static void Attach(Window window, Border frame)
    {
        void Round() => frame.Clip = new RectangleGeometry(new Rect(0, 0, frame.ActualWidth, frame.ActualHeight), window.WindowState == WindowState.Maximized ? 0 : frame.CornerRadius.TopLeft, window.WindowState == WindowState.Maximized ? 0 : frame.CornerRadius.TopLeft);
        frame.SizeChanged += (_, _) => Round();
        window.StateChanged += (_, _) => Round();
        window.MaxWidth = SystemParameters.WorkArea.Width;
        window.MaxHeight = SystemParameters.WorkArea.Height;
        if (window.ResizeMode != ResizeMode.CanResize) return;
        window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook((IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled) =>
        {
            if (message != 0x84 || window.WindowState == WindowState.Maximized) return IntPtr.Zero; // WM_NCHITTEST
            var packed = lparam.ToInt64();
            var point = window.PointFromScreen(new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff)));
            const double edge = 7;
            var left = point.X < edge; var right = point.X > window.ActualWidth - edge; var top = point.Y < edge; var bottom = point.Y > window.ActualHeight - edge;
            var hit = top ? left ? 13 : right ? 14 : 12 : bottom ? left ? 16 : right ? 17 : 15 : left ? 10 : right ? 11 : 0;
            if (hit == 0) return IntPtr.Zero;
            handled = true; return new IntPtr(hit);
        });
    }
}
