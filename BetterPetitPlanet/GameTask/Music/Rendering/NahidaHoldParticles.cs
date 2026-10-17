using System;
using System.Collections.Generic;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

public readonly record struct HoldParticleStyle(string Texture, double Size, double LifeMs,
    double IntervalMs, double Opacity, uint Color, string Motion, bool Additive)
{
    public int Slots => IntervalMs > 0 ? (int)Math.Ceiling(LifeMs / IntervalMs) : 1;
}
public readonly record struct HoldParticle(double X, double Y, double Size, double Rotation, double Alpha);

/// <summary>The actually enabled Nahida branch in hold-effect.ts. Nahida is deliberately
/// absent from HOLD_PROFILES; its unused compiled atlas is not the runtime reference.</summary>
public static class NahidaHoldParticles
{
    public static IReadOnlyList<HoldParticleStyle> Styles { get; } = Array.AsReadOnly<HoldParticleStyle>([
        new("glow", 210, 1500, 0, .15, 0x164d3b, "ambient", false),
        new("glow", 170, 1500, 0, .12, 0x78e3c5, "ambient", false),
        new("glow", 125, 1000, 0, .22, 0xffe5a0, "ambient", true),
        new("clover", 154, 300, 250, .42, 0xfff0c0, "expand", true),
        new("ring", 190, 350, 250, .2, 0xffecbb, "expand", true),
        new("ring", 202, 500, 250, .19, 0x78e3c5, "expand", false),
        new("ring", 148, 500, 200, .12, 0x78e3c5, "expand", true),
        new("clover", 183, 600, 400, .16, 0xffffff, "expand", false),
        new("dot", 16, 380, 100, .88, 0x78e3c5, "drift", true),
        new("star", 34, 500, 200, .86, 0xffedac, "drift", true),
    ]);

    public static HoldParticle? Evaluate(int noteIndex, int layerIndex, int slot, double ageMs)
        => Evaluate(Styles[layerIndex], noteIndex, layerIndex, slot, ageMs);

    public static HoldParticle? Evaluate(HoldParticleStyle style, int noteIndex, int layerIndex, int slot, double ageMs)
    {
        if (!double.IsFinite(ageMs) || ageMs < 0) return null;
        if (slot < 0 || slot >= style.Slots) return null;
        if (style.Motion == "ambient")
            return new HoldParticle(0, 0, style.Size, 0, style.Opacity * Math.Min(1, ageMs / 100 + .3));
        double serial = Math.Floor(ageMs / style.IntervalMs) - slot;
        double progress = (ageMs - serial * style.IntervalMs) / style.LifeMs;
        if (serial < 0 || progress >= 1) return null;
        double alpha = style.Opacity * Math.Min(1, progress * 8) * (1 - progress);
        if (style.Motion == "expand")
            return new HoldParticle(0, 0, style.Size * (.65 + progress * .45), 0, alpha);
        double angle = (noteIndex * 41 + layerIndex * 73 + serial * 137.508) * Math.PI / 180;
        double radius = 48 + progress * 52;
        return new HoldParticle(Math.Cos(angle) * radius, Math.Sin(angle) * radius - progress * 12,
            style.Size * (1 - progress * .45), angle * .2, alpha);
    }
}
