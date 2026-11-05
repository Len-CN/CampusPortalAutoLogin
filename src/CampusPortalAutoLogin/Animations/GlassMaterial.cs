using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CampusPortalAutoLogin.Animations;

public static class GlassMaterial
{
    public static Brush Noise { get; } = CreateNoise();
    private static Brush CreateNoise()
    {
        const int size = 128;
        var pixels = new byte[size * size * 4];
        var random = new Random(2718);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var value = (byte)random.Next(256);
            pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
            pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, size, size), Opacity = .025 };
        brush.Freeze(); return brush;
    }
    public static Brush Moonlight { get; } = Light(Color.FromArgb(85, 124, 156, 196));
    public static Brush SteelLight { get; } = Light(Color.FromArgb(90, 51, 81, 122));
    public static Brush WarmLight { get; } = Light(Color.FromArgb(32, 228, 184, 99));
    private static Brush Light(Color color)
    {
        // 场景只由三个纯色光源组成，预先执行 180% 饱和度变换；文字/玻璃叠层不参与变色。
        var luminance = .2126 * color.R + .7152 * color.G + .0722 * color.B;
        byte Channel(byte value) => (byte)Math.Clamp(Math.Round(luminance + 1.8 * (value - luminance)), 0, 255);
        var saturated = Color.FromArgb(color.A, Channel(color.R), Channel(color.G), Channel(color.B));
        var brush = new RadialGradientBrush(saturated, Colors.Transparent) { RadiusX = .65, RadiusY = .65 };
        brush.Freeze(); return brush;
    }
}
