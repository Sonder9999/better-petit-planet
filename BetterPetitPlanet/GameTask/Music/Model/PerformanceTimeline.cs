using System;
using System.Collections.Generic;

namespace BetterPetitPlanet.GameTask.Music.Model;

public sealed class PerformanceEvent
{
    public string NoteId { get; set; } = string.Empty;
    public string? SyncGroupId { get; set; }
    public MusicNoteKind Kind { get; set; }
    public string NoteType
    {
        get => Kind == MusicNoteKind.Hold ? "hold" : "tap";
        set => Kind = value switch { "tap" => MusicNoteKind.Tap, "hold" => MusicNoteKind.Hold,
            _ => throw new ArgumentException("Note type must be tap or hold.", nameof(value)) };
    }
    public MusicLane Lane => Enum.Parse<MusicLane>(Key);
    public int TimeMs { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Chord { get; set; } = string.Empty;
    public int DurationMs { get; set; } = 45;
    public string? Lyric { get; set; }
}

public enum MusicLane { A, S, D, F, J, K, L }
public enum MusicNoteKind { Tap, Hold }

public sealed class PerformanceTimeline
{
    public PetitSongMetadata Metadata { get; set; } = new();
    public List<PerformanceEvent> Events { get; set; } = [];
    public List<SongSectionMarker> Sections { get; set; } = [];
    public int TotalDurationMs { get; set; }
    public string FilePath { get; set; } = string.Empty;

    public static PerformanceTimeline Empty => new();
}
