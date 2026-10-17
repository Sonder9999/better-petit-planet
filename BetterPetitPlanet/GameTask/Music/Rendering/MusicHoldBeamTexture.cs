using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

/// <summary>Native equivalent of beam-texture.ts (144 x 512, edges at 10/134).</summary>
internal static class MusicHoldBeamTexture
{
    public static BitmapSource Default { get; } = Create(0x78e3c5);

    public static BitmapSource Create(uint color)
    {
        byte r = (byte)(color >> 16), g = (byte)(color >> 8), b = (byte)color;
        byte rb = (byte)Math.Min(255, r + 55), gb = (byte)Math.Min(255, g + 55), bb = (byte)Math.Min(255, b + 55);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Gradient(
                (0, 0, r, g, b), (.12, .12, r, g, b), (.4, .24, r, g, b),
                (.75, .34, r, g, b), (.95, .4, rb, gb, bb), (1, .44, rb, gb, bb)), null, new Rect(10, 0, 124, 512));
            var glow = new Pen(Gradient((0, 0, r, g, b), (.3, .18, r, g, b),
                (.8, .5, rb, gb, bb), (1, .7, rb, gb, bb)), 8);
            var edge = new Pen(Gradient((0, .02, 255, 255, 255), (.2, .45, rb, gb, bb),
                (.65, .85, 235, 248, 255), (1, .98, 255, 255, 255)), 2.4);
            foreach (double x in new[] { 10.0, 134.0 }) dc.DrawLine(glow, new Point(x, 0), new Point(x, 512));
            foreach (double x in new[] { 10.0, 134.0 }) dc.DrawLine(edge, new Point(x, 0), new Point(x, 512));
        }
        var bitmap = new RenderTargetBitmap(144, 512, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        return bitmap;
    }

    private static Brush Gradient(params (double Position, double Alpha, byte R, byte G, byte B)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 512), MappingMode = BrushMappingMode.Absolute };
        foreach (var stop in stops) brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(stop.Alpha * 255), stop.R, stop.G, stop.B), stop.Position));
        brush.Freeze();
        return brush;
    }
}
