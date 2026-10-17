using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Skins;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

/// <summary>
/// High-performance 64-frame hit burst animation renderer modeled after Nahida (静思浮梦).
/// Pre-warms and freezes all 64 CroppedBitmap frames from the 4096x4096 sprite atlas during startup.
/// Reuses atlas frames during rendering. The fallback uses cubic ease-out over the skin's configured duration.
/// </summary>
public static class HitBurstEffectRenderer
{
    public const int TotalFrames = 64;
    public const int AtlasCols = 8;
    public const int AtlasRows = 8;
    public const int FrameSize = 512;
    public const double BaseBurstSize = 576.0;
    public const double TargetFps = 60.0;
    public const double FrameDurationMs = 1000.0 / TargetFps; // ~16.6667ms
    public const double TotalDurationMs = TotalFrames * 1000.0 / TargetFps; // 1066.6667ms
    public const double FallbackDurationMs = 380.0;

    // Pre-allocated frozen fallback brushes and pens
    private static readonly Pen FallbackRingPen;
    private static readonly Brush FallbackCloverBrush;
    private static readonly Pen FallbackCloverPen;

    // Pre-sliced 64 CroppedBitmap frames
    public static readonly BitmapSource[]? DefaultAtlasFrames;

    static HitBurstEffectRenderer()
    {
        var ringBrush = new SolidColorBrush(Color.FromArgb(220, 126, 227, 196));
        ringBrush.Freeze();
        FallbackRingPen = new Pen(ringBrush, 2.5);
        FallbackRingPen.Freeze();

        var cloverBrush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        cloverBrush.GradientStops.Add(new GradientStop(Color.FromArgb(240, 255, 255, 230), 0.0));
        cloverBrush.GradientStops.Add(new GradientStop(Color.FromArgb(200, 126, 227, 196), 0.5));
        cloverBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 126, 227, 196), 1.0));
        cloverBrush.Freeze();
        FallbackCloverBrush = cloverBrush;

        var strokeBrush = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));
        strokeBrush.Freeze();
        FallbackCloverPen = new Pen(strokeBrush, 1.5);
        FallbackCloverPen.Freeze();

        DefaultAtlasFrames = MusicDefaultSkin.HitAtlasFrames;
    }

    /// <summary>
    /// Slices an 8x8 sprite atlas into 64 frozen CroppedBitmap frames.
    /// Used during skin loading so the render loop can reuse the sliced frames.
    /// </summary>
    public static BitmapSource[]? SliceAtlas(BitmapSource? atlas)
    {
        if (atlas == null || atlas.PixelWidth < AtlasCols * FrameSize || atlas.PixelHeight < AtlasRows * FrameSize) return null;

        var frames = new BitmapSource[TotalFrames];
        for (int i = 0; i < TotalFrames; i++)
        {
            int col = i % AtlasCols;
            int row = i / AtlasCols;
            var cropped = new CroppedBitmap(atlas, new Int32Rect(col * FrameSize, row * FrameSize, FrameSize, FrameSize));
            cropped.Freeze();
            frames[i] = cropped;
        }
        return frames;
    }

    /// <summary>
    /// Calculates current frame index for 60fps playback (16.67ms per frame).
    /// Returns -1 when elapsed time is outside the valid animation window.
    /// </summary>
    public static int ComputeFrameIndex(double elapsedMs)
    {
        if (!double.IsFinite(elapsedMs) || elapsedMs < 0 || elapsedMs >= TotalDurationMs)
        {
            return -1;
        }

        int frame = (int)(elapsedMs * TargetFps / 1000.0 + 1e-7);
        return frame is >= 0 and < TotalFrames ? frame : -1;
    }

    /// <summary>
    /// Computes scaled rendering diameter centered at judgement line based on 1920 design canvas.
    /// </summary>
    public static double ComputeBurstSize(double playfieldWidth, MusicOverlayLayoutConfig layout, double scaleMultiplier = 1.0)
    {
        double scaleFactor = (playfieldWidth / 1920.0) * layout.Scale;
        return BaseBurstSize * scaleFactor * Math.Max(0.1, scaleMultiplier);
    }

    /// <summary>
    /// Computes normalized progress and cubic ease-out for fallback animation.
    /// Returns true if within active 380ms duration.
    /// </summary>
    public static bool ComputeFallbackProgress(double elapsedMs, out double progress, out double easeOut)
    {
        if (elapsedMs < 0 || elapsedMs > FallbackDurationMs)
        {
            progress = 1.0;
            easeOut = 1.0;
            return false;
        }

        progress = Math.Clamp(elapsedMs / FallbackDurationMs, 0.0, 1.0);
        double inv = 1.0 - progress;
        easeOut = 1.0 - (inv * inv * inv);
        return true;
    }

    /// <summary>
    /// Renders hit burst animation at (laneX, judgeY).
    /// Direct array lookup into pre-sliced CroppedBitmap[64] for authentic Nahida burst,
    /// or smooth 380ms cubic ease-out fallback.
    /// </summary>
    public static bool DrawHitBurst(
        DrawingContext dc,
        double laneX,
        double judgeY,
        double elapsedMs,
        double playfieldWidth,
        MusicOverlayLayoutConfig layout,
        MusicSkin? skin = null,
        double alpha = 1.0)
    {
        using var diagnostic = MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.HitMs);
        if (alpha <= 0.001 || elapsedMs < 0 || playfieldWidth <= 0)
        {
            return false;
        }

        var frames = skin?.HitAtlasFrames ?? (skin?.IsCustom == true ? null : DefaultAtlasFrames);

        if (frames != null && frames.Length >= TotalFrames)
        {
            int frameIndex = ComputeFrameIndex(elapsedMs);
            if (frameIndex < 0 || frameIndex >= frames.Length)
            {
                return false;
            }

            var frame = frames[frameIndex];
            double size = ComputeBurstSize(playfieldWidth, layout, skin?.HitEffectScale ?? 1.0);
            var rect = new Rect(laneX - size / 2.0, judgeY - size / 2.0, size, size);

            dc.PushOpacity(Math.Clamp(alpha, 0.0, 1.0));
            dc.DrawImage(frame, rect);
            dc.Pop();
            return true;
        }

        if (ComputeFallbackProgress(elapsedMs * FallbackDurationMs / Duration(skin), out double progress, out double easeOut))
        {
            DrawFallback(dc, laneX, judgeY, progress, easeOut, playfieldWidth, layout, skin, alpha);
            return true;
        }

        return false;
    }

    public static double Duration(MusicSkin? skin) =>
        skin?.Reference is { } reference ? reference.Definition.Hit.DurationMs :
        (skin?.HitAtlasFrames ?? (skin?.IsCustom == true ? null : DefaultAtlasFrames)) is { Length: >= TotalFrames }
            ? TotalDurationMs : skin?.IsCustom == true ? Math.Max(1, skin.HitEffectDurationMs) : FallbackDurationMs;

    private static void DrawFallback(
        DrawingContext dc,
        double laneX,
        double judgeY,
        double progress,
        double easeOut,
        double playfieldWidth,
        MusicOverlayLayoutConfig layout,
        MusicSkin? skin,
        double alpha)
    {
        double scaleFactor = (playfieldWidth / 1920.0) * layout.Scale * (skin?.HitEffectScale ?? 1.0);

        var shockwaveBitmap = skin?.HitShockwaveBitmap ?? (skin?.IsCustom == true ? null : MusicDefaultSkin.HitShockwaveBitmap);
        var flowerBitmap = skin?.HitFlowerBitmap ?? (skin?.IsCustom == true ? null : MusicDefaultSkin.NoteTapBitmap);

        // 1. Expanding shockwave ring (Cubic ease-out expanding from 25px to 80px)
        double ringRadius = (25.0 + 55.0 * easeOut) * scaleFactor;
        double ringAlpha = Math.Clamp(alpha * (1.0 - easeOut) * 0.85, 0.0, 1.0);

        if (ringAlpha > 0.001)
        {
            if (shockwaveBitmap != null)
            {
                double swSize = ringRadius * 2.0;
                var swRect = new Rect(laneX - swSize / 2.0, judgeY - swSize / 2.0, swSize, swSize);
                dc.PushOpacity(ringAlpha);
                dc.DrawImage(shockwaveBitmap, swRect);
                dc.Pop();
            }
            else
            {
                dc.PushOpacity(ringAlpha);
                dc.DrawEllipse(null, FallbackRingPen, new Point(laneX, judgeY), ringRadius, ringRadius);
                dc.Pop();
            }
        }

        // 2. Central clover blooming (Expanding scale 0.5 -> 1.1 with smooth linear fade)
        double cloverScale = 0.5 + 0.6 * easeOut;
        double cloverSize = 110.0 * scaleFactor * cloverScale;
        double cloverAlpha = Math.Clamp(alpha * (1.0 - progress) * 0.95, 0.0, 1.0);

        if (cloverAlpha > 0.001)
        {
            if (flowerBitmap != null)
            {
                var flRect = new Rect(laneX - cloverSize / 2.0, judgeY - cloverSize / 2.0, cloverSize, cloverSize);
                dc.PushOpacity(cloverAlpha);
                dc.DrawImage(flowerBitmap, flRect);
                dc.Pop();
            }
            else
            {
                dc.PushOpacity(cloverAlpha);
                dc.DrawEllipse(FallbackCloverBrush, FallbackCloverPen, new Point(laneX, judgeY), cloverSize / 2.0, cloverSize / 2.0);
                dc.Pop();
            }
        }
    }
}
