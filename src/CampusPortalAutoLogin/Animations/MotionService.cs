using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Collections.Generic;
using System.Windows.Input;

namespace CampusPortalAutoLogin.Animations;

public static class MotionService
{
    public static void AttachCursorAura(Window window, Canvas host, Ellipse aura)
    {
        var target = new TranslateTransform(); aura.RenderTransform = target; aura.Visibility = Visibility.Collapsed;
        var rendering = false;
        var pending = false;
        var position = new Point();
        var lastFrame = TimeSpan.MinValue;
        void Stop()
        {
            if (rendering) CompositionTarget.Rendering -= RenderFrame;
            rendering = false; pending = false; aura.Visibility = Visibility.Collapsed;
        }
        void Place()
        {
            target.X = position.X - aura.Width / 2;
            target.Y = position.Y - aura.Height / 2;
            pending = false;
        }
        void RenderFrame(object? sender, EventArgs e)
        {
            if (e is RenderingEventArgs frame)
            {
                if (frame.RenderingTime == lastFrame) return;
                lastFrame = frame.RenderingTime;
            }
            if (pending) Place();
            // 指针静止时退订，不为空闲光效持续请求重绘。
            CompositionTarget.Rendering -= RenderFrame; rendering = false;
        }
        void Move(object sender, MouseEventArgs e)
        {
            if (!Enabled || !window.IsVisible || window.WindowState == WindowState.Minimized) { Stop(); return; }
            // 输入事件只记录最新坐标；每帧至多提交一次，不创建追赶鼠标的动画时钟。
            position = e.GetPosition(host); pending = true;
            if (aura.Visibility != Visibility.Visible) { aura.Visibility = Visibility.Visible; Place(); }
            if (!rendering) { rendering = true; CompositionTarget.Rendering += RenderFrame; }
        }
        void Leave(object? sender, MouseEventArgs e) => Stop();
        void State(object? sender, EventArgs e) => Stop();
        void VisibilityChangedLocal(object sender, DependencyPropertyChangedEventArgs e) => Stop();
        void Settings(object? sender, PropertyChangedEventArgs e) => window.Dispatcher.BeginInvoke(Stop);
        window.MouseMove += Move; window.MouseLeave += Leave; window.StateChanged += State; window.IsVisibleChanged += VisibilityChangedLocal;
        SystemParameters.StaticPropertyChanged += Settings;
        window.Closed += (_, _) =>
        {
            window.MouseMove -= Move; window.MouseLeave -= Leave; window.StateChanged -= State; window.IsVisibleChanged -= VisibilityChangedLocal;
            SystemParameters.StaticPropertyChanged -= Settings; Stop();
        };
    }

    public static void AttachTextGradientFlow(TextBlock text)
    {
        var window = Window.GetWindow(text); if (window is null) return;
        // 仅在香槟与月光白之间流动，避免复制示例里与风格规范冲突的紫粉色。
        var brush = new LinearGradientBrush { StartPoint = new Point(-1, 0), EndPoint = new Point(2, 0) };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(228, 184, 99), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(243, 220, 168), .3));
        brush.GradientStops.Add(new GradientStop(Colors.White, .5));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(228, 184, 99), .8));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(243, 220, 168), 1));
        var translate = new TranslateTransform(); brush.RelativeTransform = translate;
        void Update()
        {
            if (Enabled && window.IsVisible && window.WindowState != WindowState.Minimized)
            {
                text.Foreground = brush;
                var flow = new DoubleAnimation(-.65, .65, TimeSpan.FromSeconds(1.5)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                Timeline.SetDesiredFrameRate(flow, 24); translate.BeginAnimation(TranslateTransform.XProperty, flow);
            }
            else
            {
                translate.BeginAnimation(TranslateTransform.XProperty, null);
                text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            }
        }
        void State(object? sender, EventArgs e) => Update();
        void VisibilityChangedLocal(object sender, DependencyPropertyChangedEventArgs e) => Update();
        void Settings(object? sender, PropertyChangedEventArgs e) => window.Dispatcher.BeginInvoke(Update);
        window.StateChanged += State; window.IsVisibleChanged += VisibilityChangedLocal; SystemParameters.StaticPropertyChanged += Settings;
        window.Closed += (_, _) => { window.StateChanged -= State; window.IsVisibleChanged -= VisibilityChangedLocal; SystemParameters.StaticPropertyChanged -= Settings; translate.BeginAnimation(TranslateTransform.XProperty, null); };
        Update();
    }
    public static void AttachAmbientMotion(Window window, Panel lights)
    {
        var transforms = new List<TranslateTransform>();
        foreach (UIElement light in lights.Children)
        {
            if (light is FrameworkElement named && named.Name == "CursorAura") continue;
            var transform = new TranslateTransform(); light.RenderTransform = transform; transforms.Add(transform);
        }
        var running = false;
        void Update()
        {
            var shouldRun = Enabled && window.IsVisible && window.WindowState != WindowState.Minimized;
            if (shouldRun == running) return;
            running = shouldRun;
            for (var index = 0; index < transforms.Count; index++)
            {
                var transform = transforms[index];
                if (!running)
                {
                    transform.BeginAnimation(TranslateTransform.XProperty, null);
                    transform.BeginAnimation(TranslateTransform.YProperty, null);
                    continue;
                }
                var direction = index % 2 == 0 ? 1 : -1;
                DoubleAnimation Drift(double offset, int seconds)
                {
                    var animation = new DoubleAnimation(0, offset, TimeSpan.FromSeconds(seconds))
                    {
                        AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    };
                    Timeline.SetDesiredFrameRate(animation, 24);
                    return animation;
                }
                transform.BeginAnimation(TranslateTransform.XProperty, Drift(direction * 52, 9 + index * 3));
                transform.BeginAnimation(TranslateTransform.YProperty, Drift(direction * 30, 12 + index * 2));
            }
        }
        void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Update();
        void StateChanged(object? sender, EventArgs e) => Update();
        void SystemChanged(object? sender, PropertyChangedEventArgs e) => window.Dispatcher.BeginInvoke(Update);
        window.IsVisibleChanged += VisibilityChanged; window.StateChanged += StateChanged;
        SystemParameters.StaticPropertyChanged += SystemChanged;
        window.Closed += (_, _) =>
        {
            window.IsVisibleChanged -= VisibilityChanged; window.StateChanged -= StateChanged;
            SystemParameters.StaticPropertyChanged -= SystemChanged;
            foreach (var transform in transforms) { transform.BeginAnimation(TranslateTransform.XProperty, null); transform.BeginAnimation(TranslateTransform.YProperty, null); }
        };
        Update();
    }
    public static bool Enabled => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
    public static void Enter(FrameworkElement element)
    {
        if (!Enabled) return;
        var translate = new TranslateTransform(); var scale = new ScaleTransform(1, 1);
        var transforms = new TransformGroup(); transforms.Children.Add(scale); transforms.Children.Add(translate);
        element.RenderTransform = transforms; element.RenderTransformOrigin = new Point(.5, .5);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(550)) { EasingFunction = ease });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(550)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.97, 1, TimeSpan.FromMilliseconds(350)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.97, 1, TimeSpan.FromMilliseconds(350)) { EasingFunction = ease });
    }
    public static void Shake(FrameworkElement element)
    {
        if (!Enabled) return;
        var translate = new TranslateTransform(); element.RenderTransform = translate;
        var frames = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(500) };
        foreach (var (ms, x) in new[] { (0, 0d), (70, -4d), (150, 4d), (230, -3d), (310, 3d), (400, -1d), (500, 0d) }) frames.KeyFrames.Add(new LinearDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));
        translate.BeginAnimation(TranslateTransform.XProperty, frames);
    }
    public static void Swap(FrameworkElement element)
    {
        if (!Enabled) return;
        var translate = new TranslateTransform(); element.RenderTransform = translate;
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.3, 1, TimeSpan.FromMilliseconds(380)));
    }
    public static void WireHover(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button)
            {
                button.ApplyTemplate();
                // 光晕与位移仅作用于空的背景层，文字始终留在独立前景层。
                var surface = button.Template.FindName("Surface", button) as Border;
                if (surface is null) { WireHover(child); continue; }
                var glow = new DropShadowEffect { Color = Color.FromRgb(228, 184, 99), BlurRadius = 20, ShadowDepth = 5, Opacity = 0 };
                var lift = new TranslateTransform(); surface.RenderTransform = lift;
                surface.Effect = glow;
                button.MouseEnter += (_, _) =>
                {
                    if (!Enabled) return;
                    glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(.28, TimeSpan.FromMilliseconds(500)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                    lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-1, TimeSpan.FromMilliseconds(500)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                };
                button.MouseLeave += (_, _) =>
                {
                    glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(Enabled ? 500 : 0)));
                    lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(Enabled ? 500 : 0)));
                };
                button.PreviewMouseDown += (_, _) => { if (Enabled) lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200))); };
                button.PreviewMouseUp += (_, _) => { if (Enabled) lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-1, TimeSpan.FromMilliseconds(500))); };
                if (button.Content is string label && label.Contains('↗'))
                {
                    var content = new Grid(); var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
                    var underlineScale = new ScaleTransform(0, 1);
                    var underline = new Border { Height = 1, Background = button.Foreground, VerticalAlignment = VerticalAlignment.Bottom, RenderTransform = underlineScale };
                    content.Children.Add(text); content.Children.Add(underline); button.Content = content;
                    button.MouseEnter += (_, _) => underlineScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(Enabled ? 300 : 0)));
                    button.MouseLeave += (_, _) => underlineScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(Enabled ? 300 : 0)));
                }
            }
            WireHover(child);
        }
    }
    public static void BlurIn(FrameworkElement background)
    {
        if (!Enabled || background.Effect is not BlurEffect effect) return;
        effect.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(85, 55, TimeSpan.FromMilliseconds(550)));
    }
    public static async Task ExitAsync(FrameworkElement element)
    {
        if (!Enabled) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
        await Task.Delay(150);
    }
}
