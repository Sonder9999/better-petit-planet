using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows.Media;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Skins;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace BetterPetitPlanet.GameTask.Music.Rendering.Gpu;

/// <summary>
/// Device-owned resources for a custom skin package (brushes, textures, beam, and 4-layer hold effect).
/// Disposed when switching skins or when the D2D device is destroyed.
/// </summary>
internal sealed class MusicGpuCustomSprites : IDisposable
{
    private readonly List<ID2D1Bitmap1> _textures = new();
    private readonly ID2D1SolidColorBrush[] _brushes;
    private readonly ID2D1SolidColorBrush _trackBackgroundBrush;
    private readonly ID2D1RadialGradientBrush _darkGreenGlow, _cyanGreenGlow, _warmGoldGlow;
    private ID2D1Bitmap1? _assembledBackground;
    private (float Width, float Height, float Dpi, double Scale) _backgroundKey;

    public ID2D1Bitmap1? Tap { get; }
    public ID2D1Bitmap1? Hold { get; }
    public ID2D1Bitmap1 Beam { get; }
    public ID2D1Bitmap1? HitFlower { get; internal set; }
    public ID2D1Bitmap1? HitShockwave { get; internal set; }
    public ID2D1Bitmap1? HitSpark { get; internal set; }
    public ID2D1Bitmap1? HitSparkFlower { get; internal set; }
    public ID2D1Bitmap1? Background { get; }
    public ID2D1Bitmap1? BackgroundGradient { get; }
    public ID2D1Bitmap1? BackgroundArch { get; }
    public ID2D1Bitmap1? BackgroundWing { get; }

    public double HitEffectDurationMs { get; }
    public double HitEffectScale { get; }

    public MusicGpuCustomSprites(ID2D1DeviceContext dc, MusicSkin skin)
    {
        ArgumentNullException.ThrowIfNull(dc);
        ArgumentNullException.ThrowIfNull(skin);

        HitEffectDurationMs = skin.HitEffectDurationMs > 0 ? skin.HitEffectDurationMs : 280.0;
        HitEffectScale = skin.HitEffectScale > 0 ? skin.HitEffectScale : 1.0;

        try
        {
            Tap = Upload(dc, skin.NoteTapBitmap);
            Hold = Upload(dc, skin.NoteHoldBitmap);
            HitFlower = Upload(dc, skin.HitFlowerBitmap);
            HitShockwave = Upload(dc, skin.HitShockwaveBitmap);
            HitSpark = Upload(dc, skin.HitSparkBitmap);
            HitSparkFlower = Upload(dc, skin.HitSparkFlowerBitmap);
            Background = Upload(dc, skin.BackgroundBitmap);
            BackgroundGradient = Upload(dc, skin.BackgroundGradientBitmap);
            BackgroundArch = Upload(dc, skin.BackgroundArchBitmap);
            BackgroundWing = Upload(dc, skin.BackgroundWingBitmap);

            // Hold Beam
            if (skin.HoldBeamBitmap != null)
            {
                Beam = Upload(dc, skin.HoldBeamBitmap) ?? CreateFallbackBeam(dc, skin);
            }
            else
            {
                Beam = CreateFallbackBeam(dc, skin);
            }

            // Create solid ink brushes (matching MusicGpuRenderer.Ink)
            _brushes = new ID2D1SolidColorBrush[14];
            _brushes[(int)MusicGpuRenderer.Ink.Guide] = dc.CreateSolidColorBrush(ExtractColor(skin.TrackLaneGuidePen?.Brush, new Color4(1f, 1f, 1f, 50f / 255f)));
            _brushes[(int)MusicGpuRenderer.Ink.CenterGuide] = dc.CreateSolidColorBrush(ExtractColor(skin.TrackCenterGuidePen?.Brush, new Color4(126f / 255f, 227f / 255f, 196f / 255f, 100f / 255f)));
            _brushes[(int)MusicGpuRenderer.Ink.White] = dc.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));
            _brushes[(int)MusicGpuRenderer.Ink.Judge] = dc.CreateSolidColorBrush(ExtractColor(skin.JudgementLinePen?.Brush, new Color4(1f, 1f, 1f, 230f / 255f)));
            _brushes[(int)MusicGpuRenderer.Ink.Ring] = dc.CreateSolidColorBrush(ExtractColor(skin.JudgementLineGlowBrush, new Color4(1f, 1f, 1f, 115f / 255f)));
            _brushes[(int)MusicGpuRenderer.Ink.Tap] = dc.CreateSolidColorBrush(ExtractColor(skin.TapNoteFillBrush, new Color4(127f / 255f, 227f / 255f, 196f / 255f, 240f / 255f)));
            _brushes[(int)MusicGpuRenderer.Ink.Tail] = dc.CreateSolidColorBrush(ExtractColor(skin.HoldTailCapBrush, new Color4(198f / 255f, 232f / 255f, 107f / 255f, 240f / 255f)));
            _brushes[(int)MusicGpuRenderer.Ink.HoldEdge] = dc.CreateSolidColorBrush(ExtractColor(skin.HoldBodyStrokePen?.Brush, new Color4(220f / 255f, 1f, 240f / 255f, 220f / 255f)));
            _brushes[(int)MusicGpuRenderer.Ink.Sync] = dc.CreateSolidColorBrush(ExtractColor(skin.ChordSyncFillBrush, new Color4(172f / 255f, 1f, 241f / 255f, 130f / 255f)));
            _brushes[(int)MusicGpuRenderer.Ink.Label] = dc.CreateSolidColorBrush(ExtractColor(skin.KeyLabelBrush, new Color4(43f / 255f, 61f / 255f, 85f / 255f, 1f)));
            _brushes[(int)MusicGpuRenderer.Ink.Keycap] = dc.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 209f / 255f));
            _brushes[(int)MusicGpuRenderer.Ink.KeycapActive] = dc.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 245f / 255f));
            _brushes[(int)MusicGpuRenderer.Ink.KeyManualGlow] = dc.CreateSolidColorBrush(ExtractColor(skin.KeyManualGlowBrush, new Color4(120f / 255f, 210f / 255f, 1f, 160f / 255f)));
            _brushes[(int)MusicGpuRenderer.Ink.KeyAutoGlow] = dc.CreateSolidColorBrush(ExtractColor(skin.KeyAutoGlowBrush, new Color4(126f / 255f, 227f / 255f, 196f / 255f, 180f / 255f)));

            _trackBackgroundBrush = dc.CreateSolidColorBrush(ExtractColor(skin.TrackBackgroundBrush, new Color4(18f / 255f, 49f / 255f, 74f / 255f, 180f / 255f)));

            // CustomHoldEffectRenderer radial glows
            _darkGreenGlow = CreateRadialBrush(dc, new Color4(22f / 255f, 77f / 255f, 59f / 255f, 1f));
            _cyanGreenGlow = CreateRadialBrush(dc, new Color4(120f / 255f, 227f / 255f, 197f / 255f, 1f));
            _warmGoldGlow = CreateRadialBrush(dc, new Color4(255f / 255f, 229f / 255f, 160f / 255f, 1f));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public ID2D1SolidColorBrush Brush(MusicGpuRenderer.Ink ink) => _brushes[(int)ink];

    private ID2D1Bitmap1? Upload(ID2D1DeviceContext dc, System.Windows.Media.Imaging.BitmapSource? source)
    {
        var bitmap = MusicGpuDefaultSprites.CreateBitmap(dc, source);
        if (bitmap != null) _textures.Add(bitmap);
        return bitmap;
    }

    private ID2D1Bitmap1 CreateFallbackBeam(ID2D1DeviceContext dc, MusicSkin skin)
    {
        var target = MusicGpuDefaultSprites.Target(dc, 144, 512, 96);
        _textures.Add(target);

        Color4 tapColor = ExtractColor(skin.TapNoteFillBrush, new Color4(120f / 255f, 227f / 255f, 197f / 255f, 1f));
        byte r = (byte)(tapColor.R * 255), g = (byte)(tapColor.G * 255), b = (byte)(tapColor.B * 255);
        byte rb = (byte)Math.Min(255, r + 55), gb = (byte)Math.Min(255, g + 55), bb = (byte)Math.Min(255, b + 55);

        using var beamBody = MusicGpuDefaultSprites.Gradient(dc,
            (0, 0, r, g, b), (.12, .12, r, g, b), (.4, .24, r, g, b),
            (.75, .34, r, g, b), (.95, .4, rb, gb, bb), (1, .44, rb, gb, bb));
        using var beamGlow = MusicGpuDefaultSprites.Gradient(dc,
            (0, 0, r, g, b), (.3, .18, r, g, b), (.8, .5, rb, gb, bb), (1, .7, rb, gb, bb));
        using var beamEdge = MusicGpuDefaultSprites.Gradient(dc,
            (0, .02, 255, 255, 255), (.2, .45, rb, gb, bb),
            (.65, .85, 235, 248, 255), (1, .98, 255, 255, 255));

        using var prevTarget = dc.Target;
        dc.GetDpi(out float dpiX, out float dpiY);
        try
        {
            dc.Target = target;
            dc.SetDpi(96, 96);
            dc.Clear(new Color4(0, 0, 0, 0));
            dc.FillRectangle(new Rect(10, 0, 124, 512), beamBody);
            foreach (float x in new[] { 10f, 134f })
                dc.DrawLine(new Vector2(x, 0), new Vector2(x, 512), beamGlow, 8);
            foreach (float x in new[] { 10f, 134f })
                dc.DrawLine(new Vector2(x, 0), new Vector2(x, 512), beamEdge, 2.4f);
        }
        finally
        {
            dc.Target = prevTarget;
            dc.SetDpi(dpiX, dpiY);
        }
        return target;
    }

    private static ID2D1RadialGradientBrush CreateRadialBrush(ID2D1DeviceContext dc, Color4 baseColor)
    {
        var stops = new Vortice.Direct2D1.GradientStop[]
        {
            new() { Position = 0.0f, Color = new Color4(baseColor.R, baseColor.G, baseColor.B, 1.0f) },
            new() { Position = 0.65f, Color = new Color4(baseColor.R, baseColor.G, baseColor.B, 160f / 255f) },
            new() { Position = 1.0f, Color = new Color4(baseColor.R, baseColor.G, baseColor.B, 0.0f) }
        };
        using var stopCollection = dc.CreateGradientStopCollection(stops);
        return dc.CreateRadialGradientBrush(new RadialGradientBrushProperties
        {
            Center = Vector2.Zero,
            GradientOriginOffset = Vector2.Zero,
            RadiusX = 1.0f,
            RadiusY = 1.0f
        }, stopCollection);
    }

    public void DrawBackground(ID2D1DeviceContext dc, float width, float height, float dpi, double layoutScale, float opacity)
    {
        if (opacity <= 0) return;
        var key = (width, height, dpi, layoutScale);
        if (_assembledBackground == null || _backgroundKey != key)
        {
            _assembledBackground?.Dispose();
            _assembledBackground = MusicGpuDefaultSprites.Target(dc, Math.Max(1, (int)Math.Ceiling(width * dpi / 96)),
                Math.Max(1, (int)Math.Ceiling(height * dpi / 96)), dpi);
            using var previous = dc.Target;
            var transform = dc.Transform;
            try
            {
                dc.Target = _assembledBackground;
                dc.Transform = Matrix3x2.Identity;
                dc.Clear(new Color4(0, 0, 0, 0));
                dc.FillRectangle(new Rect(0, 0, width, height), _trackBackgroundBrush);
                if (Background != null) MusicGpuDefaultSprites.Draw(dc, Background, 0, 0, width, height);
                if (BackgroundGradient != null) MusicGpuDefaultSprites.Draw(dc, BackgroundGradient, 0, 0, width, height, 0.85f);
                if (BackgroundArch != null)
                {
                    float scale = (float)(0.78 * width / 1920 * layoutScale);
                    float archW = BackgroundArch.Size.Width * scale, archH = BackgroundArch.Size.Height * scale;
                    float wingW = (BackgroundWing?.Size.Width ?? 1) * scale, wingH = (BackgroundWing?.Size.Height ?? 1) * scale;
                    float center = width / 2, bottom = (float)(height + 60 * height / 1080 * layoutScale);
                    for (int side = 0; side < 2; side++)
                    {
                        dc.Transform = side == 0 ? Matrix3x2.Identity : Matrix3x2.CreateScale(-1, 1, new Vector2(center, 0));
                        MusicGpuDefaultSprites.Draw(dc, BackgroundArch, center - archW, bottom - archH, archW, archH);
                        if (BackgroundWing != null && wingW > 0)
                            for (float x = center - archW; x > 0; x -= wingW)
                                MusicGpuDefaultSprites.Draw(dc, BackgroundWing, x - wingW, bottom - wingH, wingW, wingH);
                    }
                    dc.Transform = Matrix3x2.Identity;
                }
                _backgroundKey = key;
            }
            finally
            {
                dc.Target = previous;
                dc.Transform = transform;
            }
        }
        MusicGpuDefaultSprites.Draw(dc, _assembledBackground, 0, 0, width, height, opacity);
    }

    /// <summary>
    /// Implements the 4-layer CustomHoldEffectRenderer using Direct2D primitives and bitmaps:
    /// Layer 1: Ambient Glow (3 radial glows)
    /// Layer 2: Expanding Rings
    /// Layer 3: Central Symbol (pulsing flower)
    /// Layer 4: Star &amp; Dot drifting particles
    /// </summary>
    public void DrawHoldGlow(ID2D1DeviceContext dc, MusicOverlayConfig config,
        IReadOnlyList<MusicGpuNote> notes, double width, float scale, float judgeY, double displayTime)
    {
        float intensity = (float)Math.Clamp(config.Effects.Intensity, 0.0, 1.0);
        if (notes.Count == 0 || intensity <= 0.001f) return;

        double scaleFactor = (width / 1920.0) * config.Layout.Scale;
        double effectScale = scaleFactor * HitEffectScale;

        var originalTransform = dc.Transform;

        for (int i = 0; i < notes.Count; i++)
        {
            var note = notes[i];
            if (!note.IsHold || !note.IsHolding) continue;

            double ageMs = displayTime - note.TimeMs;
            if (ageMs < 0 || displayTime >= note.TimeMs + note.DurationMs) continue;

            float laneX = (float)MusicPlayfieldGeometry.GetLaneX(note.Lane, width, config.Layout);
            var center = new Vector2(laneX, judgeY);

            // 1. Ambient Glow (3 radial gradients)
            DrawAmbientGlow(dc, center, ageMs, (float)effectScale, intensity, originalTransform);

            // 2. Expanding Rings
            DrawExpandingRings(dc, laneX, judgeY, ageMs, effectScale, intensity, scaleFactor);

            // 3. Central Symbol (Pulsing flower / symbol)
            DrawCentralSymbol(dc, laneX, judgeY, ageMs, effectScale, intensity);

            // 4. Star & Dot Particles
            DrawParticles(dc, laneX, judgeY, ageMs, scaleFactor, effectScale, intensity, note.Lane);
        }
    }

    private void DrawAmbientGlow(ID2D1DeviceContext dc, Vector2 center, double ageMs,
        float effectScale, float intensity, Matrix3x2 originalTransform)
    {
        // 1a. Dark green shadow glow (210px @ 1920)
        float darkAlpha = (float)CustomHoldEffectRenderer.ComputeAmbientAlpha(ageMs, CustomHoldEffectRenderer.DarkGreenAlpha, intensity);
        if (darkAlpha > 0.001f)
        {
            float r1 = (float)((CustomHoldEffectRenderer.DarkGreenDiameter / 2.0) * effectScale);
            _darkGreenGlow.Opacity = darkAlpha;
            _darkGreenGlow.Transform = Matrix3x2.CreateScale(r1, r1) * Matrix3x2.CreateTranslation(center) * originalTransform;
            dc.FillEllipse(new Ellipse(center, r1, r1), _darkGreenGlow);
        }

        // 1b. Cyan green base glow (170px @ 1920)
        float cyanAlpha = (float)CustomHoldEffectRenderer.ComputeAmbientAlpha(ageMs, CustomHoldEffectRenderer.CyanGreenAlpha, intensity);
        if (cyanAlpha > 0.001f)
        {
            float r2 = (float)((CustomHoldEffectRenderer.CyanGreenDiameter / 2.0) * effectScale);
            _cyanGreenGlow.Opacity = cyanAlpha;
            _cyanGreenGlow.Transform = Matrix3x2.CreateScale(r2, r2) * Matrix3x2.CreateTranslation(center) * originalTransform;
            dc.FillEllipse(new Ellipse(center, r2, r2), _cyanGreenGlow);
        }

        // 1c. Warm gold highlight core (125px @ 1920)
        float goldAlpha = (float)CustomHoldEffectRenderer.ComputeAmbientAlpha(ageMs, CustomHoldEffectRenderer.WarmGoldAlpha, intensity);
        if (goldAlpha > 0.001f)
        {
            float r3 = (float)((CustomHoldEffectRenderer.WarmGoldDiameter / 2.0) * effectScale);
            _warmGoldGlow.Opacity = goldAlpha;
            _warmGoldGlow.Transform = Matrix3x2.CreateScale(r3, r3) * Matrix3x2.CreateTranslation(center) * originalTransform;
            dc.FillEllipse(new Ellipse(center, r3, r3), _warmGoldGlow);
        }
    }

    private void DrawExpandingRings(ID2D1DeviceContext dc, float laneX, float judgeY,
        double ageMs, double effectScale, float intensity, double scaleFactor)
    {
        int maxRingIndex = (int)(ageMs / CustomHoldEffectRenderer.RingPeriodMs);
        for (int k = Math.Max(0, maxRingIndex - 1); k <= maxRingIndex; k++)
        {
            if (CustomHoldEffectRenderer.ComputeExpandingRing(ageMs, k, out _, out double ringScale, out double envelopeAlpha))
            {
                float ringAlpha = (float)Math.Clamp(envelopeAlpha * 0.42 * intensity, 0.0, 1.0);
                if (ringAlpha <= 0.001f) continue;

                float ringDim = (float)(CustomHoldEffectRenderer.BaseRingSize * effectScale * ringScale);
                if (HitShockwave != null)
                {
                    var rect = new Rect(laneX - ringDim / 2f, judgeY - ringDim / 2f, ringDim, ringDim);
                    dc.DrawBitmap(HitShockwave, rect, ringAlpha, InterpolationMode.Linear, null, null);
                }
                else
                {
                    var brush = Brush(MusicGpuRenderer.Ink.CenterGuide);
                    float oldOpacity = brush.Opacity;
                    brush.Opacity = ringAlpha;
                    dc.DrawEllipse(new Ellipse(new Vector2(laneX, judgeY), ringDim / 2f, ringDim / 2f),
                        brush, (float)(2.0 * scaleFactor));
                    brush.Opacity = oldOpacity;
                }
            }
        }
    }

    private void DrawCentralSymbol(ID2D1DeviceContext dc, float laneX, float judgeY,
        double ageMs, double effectScale, float intensity)
    {
        double pulseScale = 1.0 + (0.06 * Math.Sin(ageMs * 2.0 * Math.PI / 400.0));
        double pulseOpacity = (0.85 + (0.15 * Math.Cos(ageMs * 2.0 * Math.PI / 250.0))) * Math.Min(1.0, ageMs / 80.0);
        float symbolAlpha = (float)Math.Clamp(pulseOpacity * intensity, 0.0, 1.0);
        if (symbolAlpha <= 0.001f) return;

        float symDim = (float)(CustomHoldEffectRenderer.BaseSymbolSize * effectScale * pulseScale);
        var flower = HitFlower ?? Tap;

        if (flower != null)
        {
            var rect = new Rect(laneX - symDim / 2f, judgeY - symDim / 2f, symDim, symDim);
            dc.DrawBitmap(flower, rect, symbolAlpha, InterpolationMode.Linear, null, null);
        }
        else
        {
            var tapBrush = Brush(MusicGpuRenderer.Ink.Tap);
            var whiteBrush = Brush(MusicGpuRenderer.Ink.White);
            float oldTap = tapBrush.Opacity;
            float oldWhite = whiteBrush.Opacity;
            tapBrush.Opacity = symbolAlpha;
            whiteBrush.Opacity = symbolAlpha;
            var center = new Vector2(laneX, judgeY);
            float radius = symDim / 2f;
            dc.FillEllipse(new Ellipse(center, radius, radius), tapBrush);
            dc.DrawEllipse(new Ellipse(center, radius, radius), whiteBrush, (float)(1.2 * effectScale));
            tapBrush.Opacity = oldTap;
            whiteBrush.Opacity = oldWhite;
        }
    }

    private void DrawParticles(ID2D1DeviceContext dc, float laneX, float judgeY,
        double ageMs, double scaleFactor, double effectScale, float intensity, int laneIndex)
    {
        for (int i = 0; i < CustomHoldEffectRenderer.ParticlesPerLane; i++)
        {
            if (CustomHoldEffectRenderer.ComputeParticle(laneIndex, i, ageMs, scaleFactor, out CustomHoldParticleState p))
            {
                float pAlpha = (float)Math.Clamp(p.Alpha * intensity, 0.0, 1.0);
                if (pAlpha <= 0.001f) continue;

                float px = (float)(laneX + (p.Radius * Math.Cos(p.Theta)));
                float py = (float)(judgeY + (p.Radius * Math.Sin(p.Theta)) + p.DriftY);

                if (p.IsStar)
                {
                    float starDim = (float)(CustomHoldEffectRenderer.BaseStarSize * effectScale * p.Scale);
                    if (HitSparkFlower != null)
                    {
                        var rect = new Rect(px - starDim / 2f, py - starDim / 2f, starDim, starDim);
                        dc.DrawBitmap(HitSparkFlower, rect, pAlpha, InterpolationMode.Linear, null, null);
                    }
                    else
                    {
                        var whiteBrush = Brush(MusicGpuRenderer.Ink.White);
                        float oldWhite = whiteBrush.Opacity;
                        whiteBrush.Opacity = pAlpha;
                        dc.FillEllipse(new Ellipse(new Vector2(px, py), starDim / 2f, starDim / 2f), whiteBrush);
                        whiteBrush.Opacity = oldWhite;
                    }
                }
                else
                {
                    float dotDim = (float)(CustomHoldEffectRenderer.BaseDotSize * effectScale * p.Scale);
                    if (HitSpark != null)
                    {
                        var rect = new Rect(px - dotDim / 2f, py - dotDim / 2f, dotDim, dotDim);
                        dc.DrawBitmap(HitSpark, rect, pAlpha, InterpolationMode.Linear, null, null);
                    }
                    else
                    {
                        var dotBrush = Brush(MusicGpuRenderer.Ink.Tap);
                        float oldDot = dotBrush.Opacity;
                        dotBrush.Opacity = pAlpha;
                        dc.FillEllipse(new Ellipse(new Vector2(px, py), dotDim / 2f, dotDim / 2f), dotBrush);
                        dotBrush.Opacity = oldDot;
                    }
                }
            }
        }
    }

    private static Color4 ExtractColor(Brush? brush, Color4 fallback)
    {
        if (brush is SolidColorBrush scb)
            return new Color4(scb.Color.R / 255f, scb.Color.G / 255f, scb.Color.B / 255f, scb.Color.A / 255f);
        return fallback;
    }

    public void Dispose()
    {
        _assembledBackground?.Dispose();
        _assembledBackground = null;
        _darkGreenGlow?.Dispose();
        _cyanGreenGlow?.Dispose();
        _warmGoldGlow?.Dispose();
        _trackBackgroundBrush?.Dispose();
        foreach (var brush in _brushes) brush?.Dispose();
        foreach (var bitmap in _textures) bitmap.Dispose();
        _textures.Clear();
    }
}
