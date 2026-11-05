using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CampusPortalAutoLogin.Animations;

/// <summary>无色玻璃场景取光、顶边高光、底边暗缘与四秒一圈的窄边扫光。</summary>
public sealed class BeamBorder : Border
{
    private readonly Stopwatch clock = new();
    private bool rendering;
    private TimeSpan lastFrame;
    private const int Pieces = 80;
    private static readonly Pen[] Trail = CreateTrail();
    private Window? window;
    private PathGeometry? outline;
    private readonly SolidColorBrush hoverLight = new(Color.FromArgb(10, 255, 255, 255)) { Opacity = 0 };
    private static readonly Brush Shadow = CreateShadow();
    private static readonly Pen RimPen = CreateRimPen();
    private static readonly Brush Luminance = new LinearGradientBrush(Color.FromArgb(18, 255, 255, 255), Colors.Transparent, 90);
    private VisualBrush? backdrop;
    private FrameworkElement? scene;

    public BeamBorder()
    {
        Loaded += LoadedGlass; Unloaded += UnloadedGlass;
        SizeChanged += (_, _) => { outline = null; InvalidateVisual(); };
        MouseEnter += (_, _) => Hover(1); MouseLeave += (_, _) => Hover(0);
    }
    private void Hover(double value)
    {
        if (!MotionService.Enabled) return;
        hoverLight.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(value, TimeSpan.FromMilliseconds(250))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
    private void LoadedGlass(object sender, RoutedEventArgs e)
    {
        if (window is not null) return;
        window = Window.GetWindow(this); if (window is null) return;
        scene = window.FindName("Lights") as FrameworkElement;
        if (scene is not null) backdrop = new VisualBrush(scene) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        window.StateChanged += StateChanged; IsVisibleChanged += VisibilityChanged;
        SystemParameters.StaticPropertyChanged += SystemChanged; Update();
    }
    private void UnloadedGlass(object sender, RoutedEventArgs e)
    {
        StopRendering(); clock.Stop();
        if (window is not null) window.StateChanged -= StateChanged;
        IsVisibleChanged -= VisibilityChanged; SystemParameters.StaticPropertyChanged -= SystemChanged;
        window = null; backdrop = null; scene = null;
    }
    private void StateChanged(object? sender, EventArgs e) => Update();
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Update();
    private void SystemChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(Update);
    private void Update()
    {
        if (MotionService.Enabled && IsVisible && window?.WindowState != WindowState.Minimized)
        {
            clock.Start();
            if (!rendering) { rendering = true; CompositionTarget.Rendering += RenderFrame; }
        }
        else { StopRendering(); clock.Stop(); }
        InvalidateVisual();
    }
    private void StopRendering()
    {
        if (!rendering) return;
        CompositionTarget.Rendering -= RenderFrame; rendering = false;
    }
    private void RenderFrame(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame)
        {
            if (frame.RenderingTime == lastFrame) return;
            lastFrame = frame.RenderingTime;
        }
        InvalidateVisual();
    }
    private static Pen[] CreateTrail()
    {
        var pens = new Pen[Pieces];
        for (var i = 0; i < Pieces; i++)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(185 * (i + 1) / Pieces), 243, 220, 168)), 1.3);
            pen.Freeze(); pens[i] = pen;
        }
        return pens;
    }
    private static Brush CreateShadow()
    {
        var brush = new SolidColorBrush(Color.FromArgb(7, 3, 7, 18)); brush.Freeze(); return brush;
    }
    private static Pen CreateRimPen()
    {
        var pen = new Pen(new LinearGradientBrush(Color.FromArgb(65, 255, 255, 255), Color.FromArgb(95, 2, 6, 16), 90), 1);
        pen.Freeze(); return pen;
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 3 || ActualHeight < 3) return;
        var rect = new Rect(.7, .7, ActualWidth - 1.4, ActualHeight - 1.4);
        var radius = Math.Max(0, CornerRadius.TopLeft - 1);
        // 阴影只绘制在装饰背景上，不把子控件文字送入 Effect 离屏纹理。
        if (!SystemParameters.HighContrast)
            for (var i = 8; i >= 1; i--)
            {
                var shadow = rect; shadow.Inflate(i * 1.5, i * 1.5); shadow.Offset(0, 4);
                dc.DrawRoundedRectangle(Shadow, null, shadow, radius + i * 1.5, radius + i * 1.5);
            }
        if (backdrop is not null && scene is not null && !SystemParameters.HighContrast)
        {
            backdrop.Viewbox = new Rect(TranslatePoint(new Point(), scene), RenderSize);
            dc.PushOpacity(.7); dc.DrawRoundedRectangle(backdrop, null, rect, radius, radius); dc.Pop();
        }
        base.OnRender(dc);
        if (SystemParameters.HighContrast) return;
        dc.DrawRoundedRectangle(Luminance, RimPen, rect, radius, radius);
        dc.DrawRoundedRectangle(hoverLight, null, rect, radius, radius);
        if (!MotionService.Enabled) return;
        outline ??= Geometry.Combine(new RectangleGeometry(rect, radius, radius), Geometry.Empty, GeometryCombineMode.Union, null).GetFlattenedPathGeometry();
        // 沿圆角轮廓描绘窄尾迹，以经过的时间确定周期，不受屏幕刷新率影响。
        var phase = (clock.Elapsed.TotalSeconds % 4) / 4;
        for (var i = 0; i < Pieces; i++)
        {
            var from = (phase - .16 + i * .16 / Pieces + 1) % 1;
            var to = (from + .16 / Pieces) % 1; if (to < from) continue;
            outline.GetPointAtFractionLength(from, out var a, out _); outline.GetPointAtFractionLength(to, out var b, out _);
            dc.DrawLine(Trail[i], a, b);
        }
    }
}
