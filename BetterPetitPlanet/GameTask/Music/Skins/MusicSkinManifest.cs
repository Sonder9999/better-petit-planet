using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace BetterPetitPlanet.GameTask.Music.Skins;

/// <summary>
/// Serializable schema model representing a skin package metadata (skin.json).
/// </summary>
public sealed class MusicSkinManifest
{
    [JsonRequired, JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonRequired, JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonRequired, JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonRequired, JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = [];

    [JsonRequired, JsonPropertyName("contentHashes")]
    public Dictionary<string, string> ContentHashes { get; set; } = [];

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("preview")]
    public string? Preview { get; set; }

    [JsonPropertyName("colors")]
    public MusicSkinColors? Colors { get; set; }

    [JsonPropertyName("textures")]
    public MusicSkinTextures? Textures { get; set; }

    [JsonPropertyName("effects")]
    public MusicSkinEffects? Effects { get; set; }
}

public sealed class MusicSkinColors
{
    [JsonPropertyName("primaryAccent")]
    public string? PrimaryAccent { get; set; }

    [JsonPropertyName("secondaryAccent")]
    public string? SecondaryAccent { get; set; }

    [JsonPropertyName("holdColor")]
    public string? HoldColor { get; set; }

    [JsonPropertyName("trackBackground")]
    public string? TrackBackground { get; set; }

    [JsonPropertyName("laneGuide")]
    public string? LaneGuide { get; set; }

    [JsonPropertyName("centerGuide")]
    public string? CenterGuide { get; set; }

    [JsonPropertyName("judgementLine")]
    public string? JudgementLine { get; set; }

    [JsonPropertyName("keyLabel")]
    public string? KeyLabel { get; set; }

    [JsonPropertyName("keyAutoGlow")]
    public string? KeyAutoGlow { get; set; }

    [JsonPropertyName("keyManualGlow")]
    public string? KeyManualGlow { get; set; }
}

public sealed class MusicSkinTextures
{
    [JsonPropertyName("notePoint")]
    public string? NotePoint { get; set; }

    [JsonPropertyName("noteTap")]
    public string? NoteTap { get; set; }

    [JsonPropertyName("noteHold")]
    public string? NoteHold { get; set; }

    [JsonPropertyName("background")]
    public string? Background { get; set; }

    [JsonPropertyName("backgroundGradient")]
    public string? BackgroundGradient { get; set; }

    [JsonPropertyName("backgroundArch")]
    public string? BackgroundArch { get; set; }

    [JsonPropertyName("backgroundWing")]
    public string? BackgroundWing { get; set; }

    [JsonPropertyName("holdBeam")]
    public string? HoldBeam { get; set; }
}

public sealed class MusicSkinEffects
{
    [JsonPropertyName("hitFlower")]
    public string? HitFlower { get; set; }

    [JsonPropertyName("hitShockwave")]
    public string? HitShockwave { get; set; }

    [JsonPropertyName("hitSpark")]
    public string? HitSpark { get; set; }

    [JsonPropertyName("hitRay")]
    public string? HitRay { get; set; }

    [JsonPropertyName("hitEffectDurationMs")]
    public double HitEffectDurationMs { get; set; } = 280.0;

    [JsonPropertyName("hitEffectScale")]
    public double HitEffectScale { get; set; } = 1.0;
}

/// <summary>
/// Lightweight metadata descriptor for UI dropdown / skin selection.
/// </summary>
public sealed record MusicSkinInfo(
    string Id,
    string DisplayName,
    string? Author = null,
    string? Description = null,
    bool IsBuiltIn = false,
    ImageSource? IconSource = null,
    string? CharacterName = null,
    string? Region = null
)
{
    public string FullTitle => !string.IsNullOrWhiteSpace(CharacterName) && !DisplayName.Contains(CharacterName)
        ? $"{CharacterName} · {DisplayName}"
        : DisplayName;

    public override string ToString() => FullTitle;
}

