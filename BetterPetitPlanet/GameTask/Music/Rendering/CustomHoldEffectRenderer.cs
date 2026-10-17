using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Skins;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

/// <summary>
/// Immutable particle state for the zero-GC hold particle pool.
/// </summary>
internal struct CustomHoldParticleState
{
    public int Serial;
    public double Theta;
    public double Radius;
    public double DriftY;
    public double Scale;
    public double Alpha;
    public bool IsStar;
    public bool IsActive;
}

/// <summary>
/// Compatibility renderer for existing custom skin effect textures. The built-in Nahida skin uses its reference particle renderer.
/// Renders ambient glow, expanding rings, breathing central symbol, and drifting star/dot particles.
/// Zero GC allocations per frame through pooled structs, frozen brushes, and deterministic mathematics.
/// </summary>
internal static class CustomHoldEffectRenderer
{
    public const int MaxLanes = 7;
    public const int ParticlesPerLane = 24;
    public const double ParticleLifetimeMs = 600.0;
    public const double RingPeriodMs = 250.0;
    public const double RingLifetimeMs = 500.0;
    public const double RingScaleMin = 0.65;
    public const double RingScaleMax = 1.10;
    public const double GoldenAngleFactor = 137.508;

    // Ambient glow layer dimensions (@ 1920 design canvas)
    public const double DarkGreenDiameter = 210.0;
    public const double DarkGreenAlpha = 0.15;
    public const double CyanGreenDiameter = 170.0;
    public const double CyanGreenAlpha = 0.12;
    public const double WarmGoldDiameter = 125.0;
    public const double WarmGoldAlpha = 0.22;

    // Base dimensions (@ 1920 design canvas)
    public const double BaseRingSize = 130.0;
    public const double BaseSymbolSize = 100.0;
    public const double BaseStarSize = 28.0;
    public const double BaseDotSize = 18.0;

    // Pre-allocated frozen brushes and pens
    private static readonly Brush DarkGreenGlowBrush;
    private static readonly Brush CyanGreenGlowBrush;
    private static readonly Brush WarmGoldGlowBrush;
    private static readonly Pen FallbackRingPen;
    private static readonly Brush FallbackStarBrush;
    private static readonly Brush FallbackDotBrush;
    private static readonly Brush FallbackSymbolBrush;
    private static readonly Pen FallbackSymbolPen;

    // Pre-allocated zero-GC particle pool across 7 lanes
    private static readonly CustomHoldParticleState[,] ParticlePool = new CustomHoldParticleState[MaxLanes, ParticlesPerLane];

    static CustomHoldEffectRenderer()
    {
        // 1. Dark green shadow glow: #164d3b
        DarkGreenGlowBrush = CreateRadialBrush(Color.FromRgb(22, 77, 59));

        // 2. Cyan green base glow: #78e3c5
        CyanGreenGlowBrush = CreateRadialBrush(Color.FromRgb(120, 227, 197));

        // 3. Warm gold highlight core: #ffe5a0
        WarmGoldGlowBrush = CreateRadialBrush(Color.FromRgb(255, 229, 160));

        // 4. Expanding ring fallback pen
        var ringBrush = new SolidColorBrush(Color.FromArgb(200, 126, 227, 196));
        ringBrush.Freeze();
        FallbackRingPen = new Pen(ringBrush, 2.0);
        FallbackRingPen.Freeze();

        // 5. Particle fallback brushes
        var starBrush = new SolidColorBrush(Color.FromArgb(240, 255, 240, 180));
        starBrush.Freeze();
        FallbackStarBrush = starBrush;

        var dotBrush = new SolidColorBrush(Color.FromArgb(220, 120, 227, 197));
        dotBrush.Freeze();
        FallbackDotBrush = dotBrush;

        // 6. Central symbol fallback
        var symBrush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        symBrush.GradientStops.Add(new GradientStop(Color.FromArgb(240, 255, 255, 230), 0.0));
        symBrush.GradientStops.Add(new GradientStop(Color.FromArgb(200, 120, 227, 197), 0.7));
        symBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 120, 227, 197), 1.0));
        symBrush.Freeze();
        FallbackSymbolBrush = symBrush;

        var symPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1.2);
        symPen.Freeze();
        FallbackSymbolPen = symPen;
    }

    private static Brush CreateRadialBrush(Color color)
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(255, color.R, color.G, color.B), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(160, color.R, color.G, color.B), 0.65));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1.0));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Computes fade-in alpha envelope for ambient glow layer: alpha = opacity * Math.Min(1.0, ageMs / 100.0 + 0.3)
    /// </summary>
    public static double ComputeAmbientAlpha(double ageMs, double baseOpacity, double intensity = 1.0)
    {
        double envelope = Math.Min(1.0, (ageMs / 100.0) + 0.3);
        return Math.Clamp(baseOpacity * envelope * intensity, 0.0, 1.0);
    }

    /// <summary>
    /// Evaluates expanding ring scale (0.65 -> 1.10) and envelope Math.Min(1.0, 8 * p) * (1.0 - p).
    /// </summary>
    public static bool ComputeExpandingRing(double ageMs, int ringIndex, out double p, out double scale, out double alpha)
    {
        p = 0.0;
        scale = RingScaleMin;
        alpha = 0.0;

        double ringSpawnTime = ringIndex * RingPeriodMs;
        double ringAge = ageMs - ringSpawnTime;

        if (ringAge is < 0 or > RingLifetimeMs)
        {
            return false;
        }

        p = ringAge / RingLifetimeMs;
        scale = RingScaleMin + (RingScaleMax - RingScaleMin) * p;
        alpha = Math.Min(1.0, 8.0 * p) * (1.0 - p);
        return true;
    }

    /// <summary>
    /// Computes particle dynamics using golden angle distribution, drift and size decay.
    /// </summary>
    public static bool ComputeParticle(
        int laneIndex,
        int slotIndex,
        double ageMs,
        double scaleFactor,
        out CustomHoldParticleState particle)
    {
        particle = default;
        if (slotIndex is < 0 or >= ParticlesPerLane || ageMs < 0)
        {
            return false;
        }

        double stagger = slotIndex * (ParticleLifetimeMs / ParticlesPerLane);
        if (ageMs < stagger)
        {
            return false;
        }

        double totalAge = ageMs - stagger;
        int cycle = (int)(totalAge / ParticleLifetimeMs);
        double particleAge = totalAge - (cycle * ParticleLifetimeMs);
        double p = particleAge / ParticleLifetimeMs;

        int serial = (cycle * ParticlesPerLane) + slotIndex;
        double seed = (laneIndex + 1) * 57.29578;
        double theta = (seed + (serial * GoldenAngleFactor)) * Math.PI / 180.0;

        double radius = (48.0 + (52.0 * p)) * scaleFactor;
        double driftY = -12.0 * p * scaleFactor;
        double scale = 1.0 - (0.45 * p);
        double alpha = Math.Sin(p * Math.PI) * Math.Min(1.0, ageMs / 80.0);

        particle = new CustomHoldParticleState
        {
            Serial = serial,
            Theta = theta,
            Radius = radius,
            DriftY = driftY,
            Scale = scale,
            Alpha = alpha,
            IsStar = (slotIndex % 2 == 0),
            IsActive = true
        };

        ParticlePool[Math.Clamp(laneIndex, 0, MaxLanes - 1), slotIndex] = particle;
        return true;
    }

    /// <summary>
    /// Renders complete holding effect across all 4 layers: Ambient Glow, Expanding Ring, Central Symbol, Star &amp; Dot Particles.
    /// </summary>
    public static void DrawHoldingEffect(
        DrawingContext dc,
        double laneX,
        double judgeY,
        double ageMs,
        double playfieldWidth,
        MusicOverlayLayoutConfig layout,
        MusicSkin? skin = null,
        double intensity = 1.0,
        int laneIndex = 0)
    {
        if (ageMs < 0 || playfieldWidth <= 0 || intensity <= 0.001)
        {
            return;
        }

        double scaleFactor = (playfieldWidth / 1920.0) * layout.Scale;
        double effectScale = scaleFactor * (skin?.HitEffectScale ?? 1.0);

        // 1. Layer 1: Ambient Glow (Permanent 3-layer gradient with ageMs entrance envelope)
        DrawAmbientGlow(dc, laneX, judgeY, ageMs, effectScale, intensity);

        // 2. Layer 2: Expanding Ring (250ms period, 500ms life, expanding 0.65 -> 1.10)
        DrawExpandingRings(dc, laneX, judgeY, ageMs, effectScale, intensity, skin);

        // 3. Layer 3: Central Symbol (Breathing pulse scale 400ms, opacity 250ms)
        DrawCentralSymbol(dc, laneX, judgeY, ageMs, effectScale, intensity, skin);

        // 4. Layer 4: Star & Dot Drifting Particles (Golden angle distribution, upward drift, size decay)
        DrawParticles(dc, laneX, judgeY, ageMs, scaleFactor, effectScale, intensity, laneIndex, skin);
    }

    private static void DrawAmbientGlow(
        DrawingContext dc,
        double laneX,
        double judgeY,
        double ageMs,
        double effectScale,
        double intensity)
    {
        var center = new Point(laneX, judgeY);

        // 1a. Dark green shadow glow (210px @ 1920)
        double darkAlpha = ComputeAmbientAlpha(ageMs, DarkGreenAlpha, intensity);
        if (darkAlpha > 0.001)
        {
            double r1 = (DarkGreenDiameter / 2.0) * effectScale;
            dc.PushOpacity(darkAlpha);
            dc.DrawEllipse(DarkGreenGlowBrush, null, center, r1, r1);
            dc.Pop();
        }

        // 1b. Cyan green base glow (170px @ 1920)
        double cyanAlpha = ComputeAmbientAlpha(ageMs, CyanGreenAlpha, intensity);
        if (cyanAlpha > 0.001)
        {
            double r2 = (CyanGreenDiameter / 2.0) * effectScale;
            dc.PushOpacity(cyanAlpha);
            dc.DrawEllipse(CyanGreenGlowBrush, null, center, r2, r2);
            dc.Pop();
        }

        // 1c. Warm gold highlight core (125px @ 1920)
        double goldAlpha = ComputeAmbientAlpha(ageMs, WarmGoldAlpha, intensity);
        if (goldAlpha > 0.001)
        {
            double r3 = (WarmGoldDiameter / 2.0) * effectScale;
            dc.PushOpacity(goldAlpha);
            dc.DrawEllipse(WarmGoldGlowBrush, null, center, r3, r3);
            dc.Pop();
        }
    }

    private static void DrawExpandingRings(
        DrawingContext dc,
        double laneX,
        double judgeY,
        double ageMs,
        double effectScale,
        double intensity,
        MusicSkin? skin)
    {
        var shockwaveBitmap = skin?.HitShockwaveBitmap ?? MusicDefaultSkin.HitShockwaveBitmap;
        int maxRingIndex = (int)(ageMs / RingPeriodMs);

        for (int k = Math.Max(0, maxRingIndex - 1); k <= maxRingIndex; k++)
        {
            if (ComputeExpandingRing(ageMs, k, out _, out double ringScale, out double envelopeAlpha))
            {
                double ringAlpha = Math.Clamp(envelopeAlpha * 0.42 * intensity, 0.0, 1.0);
                if (ringAlpha <= 0.001) continue;

                double ringDim = BaseRingSize * effectScale * ringScale;
                if (shockwaveBitmap != null)
                {
                    var rect = new Rect(laneX - ringDim / 2.0, judgeY - ringDim / 2.0, ringDim, ringDim);
                    dc.PushOpacity(ringAlpha);
                    dc.DrawImage(shockwaveBitmap, rect);
                    dc.Pop();
                }
                else
                {
                    dc.PushOpacity(ringAlpha);
                    dc.DrawEllipse(null, FallbackRingPen, new Point(laneX, judgeY), ringDim / 2.0, ringDim / 2.0);
                    dc.Pop();
                }
            }
        }
    }

    private static void DrawCentralSymbol(
        DrawingContext dc,
        double laneX,
        double judgeY,
        double ageMs,
        double effectScale,
        double intensity,
        MusicSkin? skin)
    {
        double pulseScale = 1.0 + (0.06 * Math.Sin(ageMs * 2.0 * Math.PI / 400.0));
        double pulseOpacity = (0.85 + (0.15 * Math.Cos(ageMs * 2.0 * Math.PI / 250.0))) * Math.Min(1.0, ageMs / 80.0);
        double symbolAlpha = Math.Clamp(pulseOpacity * intensity, 0.0, 1.0);

        if (symbolAlpha <= 0.001) return;

        double symDim = BaseSymbolSize * effectScale * pulseScale;
        var flowerBitmap = skin?.HitFlowerBitmap ?? MusicDefaultSkin.HitFlowerBitmap;

        if (flowerBitmap != null)
        {
            var rect = new Rect(laneX - symDim / 2.0, judgeY - symDim / 2.0, symDim, symDim);
            dc.PushOpacity(symbolAlpha);
            dc.DrawImage(flowerBitmap, rect);
            dc.Pop();
        }
        else
        {
            dc.PushOpacity(symbolAlpha);
            dc.DrawEllipse(FallbackSymbolBrush, FallbackSymbolPen, new Point(laneX, judgeY), symDim / 2.0, symDim / 2.0);
            dc.Pop();
        }
    }

    private static void DrawParticles(
        DrawingContext dc,
        double laneX,
        double judgeY,
        double ageMs,
        double scaleFactor,
        double effectScale,
        double intensity,
        int laneIndex,
        MusicSkin? skin)
    {
        var sparkFlowerBitmap = skin?.HitSparkFlowerBitmap ?? MusicDefaultSkin.HitSparkFlowerBitmap;
        var sparkDotBitmap = skin?.HitSparkBitmap ?? MusicDefaultSkin.HitSparkBitmap;

        for (int i = 0; i < ParticlesPerLane; i++)
        {
            if (ComputeParticle(laneIndex, i, ageMs, scaleFactor, out CustomHoldParticleState p))
            {
                double pAlpha = Math.Clamp(p.Alpha * intensity, 0.0, 1.0);
                if (pAlpha <= 0.001) continue;

                double px = laneX + (p.Radius * Math.Cos(p.Theta));
                double py = judgeY + (p.Radius * Math.Sin(p.Theta)) + p.DriftY;

                if (p.IsStar)
                {
                    double starDim = BaseStarSize * effectScale * p.Scale;
                    if (sparkFlowerBitmap != null)
                    {
                        var rect = new Rect(px - starDim / 2.0, py - starDim / 2.0, starDim, starDim);
                        dc.PushOpacity(pAlpha);
                        dc.DrawImage(sparkFlowerBitmap, rect);
                        dc.Pop();
                    }
                    else
                    {
                        dc.PushOpacity(pAlpha);
                        dc.DrawEllipse(FallbackStarBrush, null, new Point(px, py), starDim / 2.0, starDim / 2.0);
                        dc.Pop();
                    }
                }
                else
                {
                    double dotDim = BaseDotSize * effectScale * p.Scale;
                    if (sparkDotBitmap != null)
                    {
                        var rect = new Rect(px - dotDim / 2.0, py - dotDim / 2.0, dotDim, dotDim);
                        dc.PushOpacity(pAlpha);
                        dc.DrawImage(sparkDotBitmap, rect);
                        dc.Pop();
                    }
                    else
                    {
                        dc.PushOpacity(pAlpha);
                        dc.DrawEllipse(FallbackDotBrush, null, new Point(px, py), dotDim / 2.0, dotDim / 2.0);
                        dc.Pop();
                    }
                }
            }
        }
    }
}
