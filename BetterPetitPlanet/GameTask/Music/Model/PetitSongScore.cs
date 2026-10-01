using System.Collections.Generic;
using Newtonsoft.Json;

namespace BetterPetitPlanet.GameTask.Music.Model;

public sealed class PetitSongMetadata
{
    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("artist")]
    public string Artist { get; set; } = string.Empty;

    [JsonProperty("bpm")]
    public double Bpm { get; set; } = 120.0;

    [JsonProperty("time_signature")]
    public string TimeSignature { get; set; } = "4/4";

    [JsonProperty("instrument")]
    public string Instrument { get; set; } = "guitar";

    [JsonProperty("key")]
    public string Key { get; set; } = "C";

    [JsonProperty("version")]
    public string Version { get; set; } = "1.0.0";
}

public sealed class PetitChordEvent
{
    [JsonProperty("time_ms")]
    public int TimeMs { get; set; }

    [JsonProperty("chord")]
    public string Chord { get; set; } = string.Empty;

    [JsonProperty("key")]
    public string Key { get; set; } = string.Empty;

    [JsonProperty("action")]
    public string Action { get; set; } = "press";

    [JsonProperty("duration_ms")]
    public int DurationMs { get; set; } = 45;

    [JsonProperty("bar")]
    public int? Bar { get; set; }

    [JsonProperty("beat")]
    public double? Beat { get; set; }

    [JsonProperty("lyric")]
    public string? Lyric { get; set; }
}

public sealed class PetitSongScore
{
    [JsonProperty("metadata")]
    public PetitSongMetadata Metadata { get; set; } = new();

    [JsonProperty("key_mapping")]
    public Dictionary<string, string> KeyMapping { get; set; } = [];

    [JsonProperty("sequence")]
    public List<PetitChordEvent> Sequence { get; set; } = [];
}
