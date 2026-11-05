using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CampusPortalAutoLogin.Views;

public partial class WindowTitleBar : UserControl
{
    public WindowTitleBar()
    {
        InitializeComponent();
        Loaded += (_, _) => MaximizeButton.Visibility = Window.GetWindow(this)?.ResizeMode == ResizeMode.CanResize ? Visibility.Visible : Visibility.Collapsed;
    }
    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
        {
            while (source is not null) { if (source is Button) return; source = System.Windows.Media.VisualTreeHelper.GetParent(source); }
        }
        var window = Window.GetWindow(this);
        if (window is null) return;
        if (e.ClickCount == 2 && window.ResizeMode == ResizeMode.CanResize) { ToggleMaximize(window); return; }
        if (e.LeftButton == MouseButtonState.Pressed) window.DragMove();
    }
    private void ShowSystemMenu(object sender, MouseButtonEventArgs e) { var window = Window.GetWindow(this); if (window is not null) SystemCommands.ShowSystemMenu(window, PointToScreen(e.GetPosition(this))); }
    private void Minimize(object sender, RoutedEventArgs e) { var window = Window.GetWindow(this); if (window is not null) window.WindowState = WindowState.Minimized; }
    private void Maximize(object sender, RoutedEventArgs e) { var window = Window.GetWindow(this); if (window is not null) ToggleMaximize(window); }
    private static void ToggleMaximize(Window window) => window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();
}
