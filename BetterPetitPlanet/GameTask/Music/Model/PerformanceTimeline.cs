using System.Collections.Generic;

namespace BetterPetitPlanet.GameTask.Music.Model;

public sealed class PerformanceEvent
{
    public int TimeMs { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Chord { get; set; } = string.Empty;
    public int DurationMs { get; set; } = 45;
    public string? Lyric { get; set; }
}

public sealed class PerformanceTimeline
{
    public PetitSongMetadata Metadata { get; set; } = new();
    public List<PerformanceEvent> Events { get; set; } = [];
    public int TotalDurationMs { get; set; }
    public string FilePath { get; set; } = string.Empty;

    public static PerformanceTimeline Empty => new();
}
