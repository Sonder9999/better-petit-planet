using System;
using System.IO;

namespace BetterPetitPlanet.Core.Config;

public enum MusicOverlayBackend
{
    Wpf = 0,
    Gpu = 1
}

public sealed class MusicOverlayConfig
{
    public bool Enabled { get; set; }
    public MusicOverlayBackend Backend { get; set; } = MusicOverlayBackend.Wpf;
    public string SkinId { get; set; } = "default";
    public int FallDurationMs { get; set; } = 3000;
    public int VisualOffsetMs { get; set; }
    public MusicOverlayLayoutConfig Layout { get; set; } = new();
    public MusicOverlayEffectsConfig Effects { get; set; } = new();

    // Session readiness compatibility parameters
    public int ReadySuccessCount { get; set; } = 2;
    public int ReadyFailureCount { get; set; } = 3;
    public int DetectionFreshnessMs { get; set; } = 2000;
    public int CaptureFreshnessMs { get; set; } = 2000;
    public int SnapshotIntervalMs { get; set; } = 50;
    public int WindowPollIntervalMs { get; set; } = 100;

    // Backward compatibility read-only field (no browser/frontend loading)
    public string AssetDirectory { get; set; } = "Assets/MusicOverlay";

    public MusicOverlayConfig Clone()
    {
        var copy = (MusicOverlayConfig)MemberwiseClone();
        copy.Layout = (Layout ?? new()).Clone();
        copy.Effects = (Effects ?? new()).Clone();
        return copy;
    }

    // Publish only fields edited by the settings page, preserving references held by hot services.
    public void ApplySettingsFrom(MusicOverlayConfig source)
    {
        Enabled = source.Enabled;
        Backend = source.Backend;
        SkinId = source.SkinId;
        FallDurationMs = source.FallDurationMs;
        VisualOffsetMs = source.VisualOffsetMs;
        Layout ??= new();
        Layout.X = source.Layout.X;
        Layout.Y = source.Layout.Y;
        Layout.Width = source.Layout.Width;
        Layout.Height = source.Layout.Height;
        Layout.Scale = source.Layout.Scale;
        Layout.JudgementLine = source.Layout.JudgementLine;
        Effects ??= new();
        Effects.HitBursts = source.Effects.HitBursts;
        Effects.HoldGlow = source.Effects.HoldGlow;
        Effects.SyncLines = source.Effects.SyncLines;
        Effects.LaneGuides = source.Effects.LaneGuides;
        Effects.ShowKeyLabels = source.Effects.ShowKeyLabels;
        Effects.Intensity = source.Effects.Intensity;
        Effects.ShowBackgroundDeco = source.Effects.ShowBackgroundDeco;
        Effects.BackgroundOpacity = source.Effects.BackgroundOpacity;
    }

    public void Validate()
    {
        if (!Enum.IsDefined(Backend)) throw new ArgumentException("谱面渲染后端无效");
        if (string.IsNullOrWhiteSpace(SkinId) || SkinId.Length > 128)
            throw new ArgumentException("皮肤标识无效");
        if (string.IsNullOrWhiteSpace(AssetDirectory) || Path.IsPathRooted(AssetDirectory) || AssetDirectory.Contains(".."))
            throw new ArgumentException("资源目录必须为应用相对路径");

        Check(FallDurationMs, 500, 10000);
        Check(VisualOffsetMs, -2000, 2000);
        Check(ReadySuccessCount, 1, 20);
        Check(ReadyFailureCount, 1, 20);
        Check(DetectionFreshnessMs, 200, 30000);
        Check(CaptureFreshnessMs, 200, 30000);
        Check(SnapshotIntervalMs, 16, 1000);
        Check(WindowPollIntervalMs, 20, 1000);

        Layout ??= new();
        Effects ??= new();
        Layout.Validate();
        Effects.Validate();
    }

    internal static void Check(double value, double min, double max)
    {
        if (!double.IsFinite(value) || value < min || value > max)
            throw new ArgumentOutOfRangeException(nameof(value));
    }
}

public sealed class MusicOverlayLayoutConfig
{
    internal MusicOverlayLayoutConfig Clone() => (MusicOverlayLayoutConfig)MemberwiseClone();
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1;
    public double Height { get; set; } = 0.78;
    public double Scale { get; set; } = 1;
    public double JudgementLine { get; set; } = 0.95;

    public void Validate()
    {
        MusicOverlayConfig.Check(X, 0, 1);
        MusicOverlayConfig.Check(Y, 0, 1);
        MusicOverlayConfig.Check(Width, 0.1, 1);
        MusicOverlayConfig.Check(Height, 0.1, 1);
        MusicOverlayConfig.Check(Scale, 0.5, 2);
        MusicOverlayConfig.Check(JudgementLine, 0.1, 1);
        if (X + Width > 1.0001 || Y + Height > 1.0001)
            throw new ArgumentException("轨道区域超出游戏客户区");
    }
}

public sealed class MusicOverlayEffectsConfig
{
    internal MusicOverlayEffectsConfig Clone() => (MusicOverlayEffectsConfig)MemberwiseClone();
    public bool HitBursts { get; set; } = true;
    public bool HoldGlow { get; set; } = true;
    public bool SyncLines { get; set; } = true;
    public bool LaneGuides { get; set; } = true;

    public bool ShowKeyLabels { get; set; } = true;
    public bool KeyLabels
    {
        get => ShowKeyLabels;
        set => ShowKeyLabels = value;
    }

    public double Intensity { get; set; } = 1.0;

    public bool ShowBackgroundDeco { get; set; } = true;
    public bool BackgroundEnabled
    {
        get => ShowBackgroundDeco;
        set => ShowBackgroundDeco = value;
    }

    public double BackgroundOpacity { get; set; } = 0.8;

    public void Validate()
    {
        MusicOverlayConfig.Check(Intensity, 0, 1);
        MusicOverlayConfig.Check(BackgroundOpacity, 0, 1);
    }
}
