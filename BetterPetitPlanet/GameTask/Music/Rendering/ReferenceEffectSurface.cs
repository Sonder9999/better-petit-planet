using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

/// <summary>Reusable sprite passes for one age and scale; they never contain the backdrop.</summary>
internal readonly record struct ReferenceEffectChannels(int Size, byte[] Normal, byte[] Light);

/// <summary>A local reusable capture. Its output replaces the captured rectangle, including on transparent overlays.</summary>
internal sealed class ReferenceEffectSurface
{
    private readonly DrawingVisual _capture = new();
    private RenderTargetBitmap? _bitmap;
    private WriteableBitmap? _output;
    public int Size { get; private set; }
    public byte[] Back { get; private set; } = [];
    public byte[] Normal { get; private set; } = [];
    public byte[] Light { get; private set; } = [];
    public ReferenceEffectChannels Channels => new(Size, Normal, Light);

    public void Capture(Drawing scene, Rect bounds, double pixelsPerDip)
    {
        int size = Math.Clamp((int)Math.Ceiling(bounds.Width * Math.Clamp(pixelsPerDip, .5, 4)), 1, 1536);
        if (size != Size)
        {
            Size = size; Back = new byte[size * size * 4]; Normal = new byte[Back.Length]; Light = new byte[Back.Length];
            _bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            _output = new WriteableBitmap(size, size, 96, 96, PixelFormats.Pbgra32, null);
        }
        Array.Clear(Normal); Array.Clear(Light);
        using (var dc = _capture.RenderOpen())
        {
            dc.PushTransform(new MatrixTransform(size / bounds.Width, 0, 0, size / bounds.Height,
                -bounds.X * size / bounds.Width, -bounds.Y * size / bounds.Height));
            dc.DrawDrawing(scene); dc.Pop();
        }
        using (MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.CaptureMs))
        { _bitmap!.Clear(); _bitmap.Render(_capture); }
        using (MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.CopyPixelsMs))
            _bitmap.CopyPixels(Back, size * 4, 0);
    }

    public BitmapSource Finish()
    {
        using var diagnostic = MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.WritePixelsMs);
        _output!.WritePixels(new Int32Rect(0, 0, Size, Size), Back, Size * 4, 0);
        return _output;
    }

    public static DrawingGroup Replace(Drawing scene, BitmapSource image, Rect bounds, Geometry clip)
    {
        var replacement = new DrawingGroup();
        using var dc = replacement.Open();
        var innerBounds = new Rect(bounds.X + 0.5, bounds.Y + 0.5, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
        dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, clip, new RectangleGeometry(innerBounds)));
        dc.DrawDrawing(scene); dc.Pop(); dc.DrawImage(image, bounds);
        return replacement;
    }
}
