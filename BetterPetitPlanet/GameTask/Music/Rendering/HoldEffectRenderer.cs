using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Skins;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

/// <summary>One renderer per held note, preserving the original
/// ten-layer schedules and ordered normal/additive sprites over the local backdrop.</summary>
public sealed class HoldEffectRenderer
{
    private static readonly IReadOnlyDictionary<string, MusicSpritePixels> Textures = LoadTextures();
    private readonly DrawingVisual _capture = new();
    private RenderTargetBitmap? _backdrop;
    private WriteableBitmap? _output;
    private byte[] _pixels = [];
    private int _size;
    private readonly int _noteIndex;
    private readonly IReadOnlyList<HoldParticleStyle> _styles;

    public HoldEffectRenderer(int noteIndex = 0, IReadOnlyList<HoldParticleStyle>? styles = null)
    {
        _noteIndex = noteIndex;
        _styles = styles ?? NahidaHoldParticles.Styles;
    }
    public static bool HasSource => Textures.Count == 5;

    private static IReadOnlyDictionary<string, MusicSpritePixels> LoadTextures()
    {
        var textures = new Dictionary<string, MusicSpritePixels>();
        foreach (var name in new[] { "clover", "ring", "star", "glow", "dot" })
        {
            string? path = MusicDefaultSkin.ResolveAssetPath(Path.Combine("Reference", "assets", "hold", name + ".png"))
                ?? MusicDefaultSkin.ResolveAssetPath(name + ".png")
                ?? MusicDefaultSkin.ResolveAssetPath("hold-" + name + ".png");
            if (path == null) continue;
            try
            {
                textures[name] = new MusicSpritePixels(path);
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException or FileFormatException)
            {
                // A damaged optional sprite disables source particles; do not poison this static type.
                // HasSource remains false and the default-skin diagnostic reports the incomplete set.
            }
        }
        return textures;
    }

    /// <summary>The image includes the backdrop. Replace its rectangle in the scene;
    /// do not alpha-blend the same backdrop a second time.</summary>
    public BitmapSource? Render(Drawing backdrop, Rect bounds, double ageMs, double intensity = 1, double pixelsPerDip = 1)
    {
        if (!HasSource || !double.IsFinite(ageMs) || ageMs < 0 || bounds.Width <= 0 || intensity <= 0) return null;
        int size = Math.Clamp((int)Math.Ceiling(bounds.Width * Math.Clamp(pixelsPerDip, .5, 4)), 1, 960);
        if (size != _size)
        {
            _size = size;
            _pixels = new byte[size * size * 4];
            _backdrop = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            _output = new WriteableBitmap(size, size, 96, 96, PixelFormats.Pbgra32, null);
        }
        using (var dc = _capture.RenderOpen())
        {
            dc.PushTransform(new MatrixTransform(size / bounds.Width, 0, 0, size / bounds.Height,
                -bounds.X * size / bounds.Width, -bounds.Y * size / bounds.Height));
            dc.DrawDrawing(backdrop);
            dc.Pop();
        }
        using (MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.CaptureMs))
        { _backdrop!.Clear(); _backdrop.Render(_capture); }
        using (MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.CopyPixelsMs))
            _backdrop.CopyPixels(_pixels, size * 4, 0);
        using (MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.BlendMs))
        {
        for (int layer = 0; layer < _styles.Count; layer++)
        {
            var style = _styles[layer];
            for (int slot = 0; slot < style.Slots; slot++)
                if (NahidaHoldParticles.Evaluate(style, _noteIndex, layer, slot, ageMs) is { } particle)
                    Textures[style.Texture].Blend(_pixels, size, particle, style, size / 288.0, intensity);
        }
        }
        using (MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.WritePixelsMs))
            _output!.WritePixels(new Int32Rect(0, 0, size, size), _pixels, size * 4, 0);
        return _output;
    }

    public static Rect Bounds(double x, double y, double width, MusicOverlayLayoutConfig layout, double effectScale = 1)
    {
        double size = 288 * width / 1920 * layout.Scale * effectScale;
        return new Rect(x - size / 2, y - size / 2, size, size);
    }

    // Compatibility for offscreen tools. Live rendering supplies the real backdrop
    // to Render() and owns an emitter per note rather than sharing a global lane pool.
    public static void DrawHoldingEffect(DrawingContext dc, double laneX, double judgeY, double ageMs,
        double playfieldWidth, MusicOverlayLayoutConfig layout, MusicSkin? skin = null, double intensity = 1, int laneIndex = 0)
    {
        using var diagnostic = MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.HoldMs);
        if (playfieldWidth <= 0 || ageMs < 0 || intensity <= 0) return;
        if (skin?.IsCustom == true)
        {
            CustomHoldEffectRenderer.DrawHoldingEffect(dc, laneX, judgeY, ageMs, playfieldWidth, layout, skin, intensity, laneIndex);
            return;
        }
        var bounds = Bounds(laneX, judgeY, playfieldWidth, layout, skin?.HitEffectScale ?? 1);
        var renderer = new HoldEffectRenderer(laneIndex);
        var bitmap = renderer.Render(new DrawingGroup(), bounds, ageMs, intensity);
        if (bitmap != null) dc.DrawImage(bitmap, bounds);
    }
}
