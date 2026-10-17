using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace BetterPetitPlanet.GameTask.Music.Skins;

/// <summary>Data exported from the reference's active skin and effect registries.</summary>
public sealed class ReferenceSkinDefinition
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    [JsonPropertyName("character")] public string? Character { get; set; }
    [JsonPropertyName("region")] public string? Region { get; set; }
    public string? PreviewTexture { get; init; }
    public uint Backdrop { get; init; }
    public ReferenceAccent Accent { get; init; } = new();
    public uint? HoldColor { get; init; }
    public string[] NoteTextures { get; init; } = [];
    public string JudgePointTexture { get; init; } = "";
    public ReferenceBackgroundLayer[] Background { get; init; } = [];
    public ReferenceHitAnimation Hit { get; init; } = new();
    public Dictionary<string, ReferenceHitAnimation> Variants { get; init; } = [];
    public ReferenceHoldProfile? HoldProfile { get; init; }
    public ReferenceParticleStyle[] HoldStyles { get; init; } = [];
}

public sealed class ReferenceAccent
{
    public uint Primary { get; init; }
    public uint Secondary { get; init; }
}

public sealed class ReferenceBackgroundLayer
{
    public string Url { get; init; } = "";
    public ReferenceLayerFit Fit { get; init; } = new();
    public double Alpha { get; init; } = 1;
    public string Blend { get; init; } = "normal";
    public uint Tint { get; init; } = 0xffffff;
}

public sealed class ReferenceLayerFit
{
    public string Kind { get; init; } = "stretch";
    public double Scale { get; init; } = 1;
    public double BottomOffsetPx { get; init; }
    public double TopOffsetPx { get; init; }
    public double EdgeOffsetPx { get; init; }
    public double? CenterOffsetPx { get; init; }
    public double TopRatio { get; init; }
    public string YAnchor { get; init; } = "bottom";
    public string? WingUrl { get; init; }
    public string? RightUrl { get; init; }
}

public sealed class ReferenceHitAnimation
{
    public string Url { get; init; } = "";
    public string? AdditiveUrl { get; init; }
    public int FrameSize { get; init; }
    public int Columns { get; init; }
    public int FrameCount { get; init; }
    public double Fps { get; init; }
    public double DisplaySize { get; init; }
    public double DurationMs => FrameCount * 1000.0 / Fps;
    public int FrameAt(double ageMs) => !double.IsFinite(ageMs) || ageMs < 0 || ageMs >= DurationMs
        ? -1 : Math.Min(FrameCount - 1, (int)(ageMs * Fps / 1000 + 1e-7));
}

public sealed class ReferenceParticleStyle
{
    public string Texture { get; init; } = "";
    public double Size { get; init; }
    public double LifeMs { get; init; }
    public double IntervalMs { get; init; }
    public double Opacity { get; init; }
    public uint Color { get; init; }
    public string Motion { get; init; } = "";
    public string Blend { get; init; } = "normal";
}

public sealed record ReferenceSkinImage(BitmapSource Bitmap, int SourceWidth, int SourceHeight);

/// <summary>Only the selected theme's static textures are decoded. Effect frames use a bounded shared LRU.</summary>
public sealed class ReferenceSkinResources(ReferenceSkinDefinition definition,
    IReadOnlyDictionary<string, ReferenceSkinImage> images)
{
    public ReferenceSkinDefinition Definition { get; } = definition;
    public IReadOnlyDictionary<string, ReferenceSkinImage> Images { get; } = images;
}
