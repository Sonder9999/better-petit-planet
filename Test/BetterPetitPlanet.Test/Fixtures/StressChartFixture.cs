using System;
using System.Collections.Generic;
using System.Linq;
using BetterPetitPlanet.GameTask.Music.Model;
using Newtonsoft.Json;

namespace BetterPetitPlanet.Test;

/// <summary>
/// Fixture providing synthetic seven-track (A/S/D/F/J/K/L) stress charts,
/// dense short notes, chords, long holds, seek targets, variable tempo,
/// and negative preparation countdown structures for testing and benchmarking.
/// </summary>
public static class StressChartFixture
{
    public static readonly string[] SevenLanes = ["A", "S", "D", "F", "J", "K", "L"];

    public static readonly Dictionary<string, string> StandardKeyMapping = new()
    {
        { "C", "A" },
        { "Dm", "S" },
        { "Em", "D" },
        { "F", "F" },
        { "G", "J" },
        { "Am", "K" },
        { "G7", "L" }
    };

    /// <summary>
    /// Creates a 7-lane synthetic performance timeline with dense short notes, chords,
    /// long holds, section markers, and specified lead-in.
    /// </summary>
    public static PerformanceTimeline CreateSevenLaneStressTimeline(
        int noteCount = 1000,
        int stepMs = 50,
        int chordInterval = 8,
        int holdInterval = 12,
        double bpm = 140.0)
    {
        var events = new List<PerformanceEvent>(noteCount);
        int currentTime = 0;
        int laneIndex = 0;

        for (int i = 0; i < noteCount; i++)
        {
            var lane = SevenLanes[laneIndex % SevenLanes.Length];
            bool isHold = (i > 0 && i % holdInterval == 0);
            int duration = isHold ? 600 : 45;

            events.Add(new PerformanceEvent
            {
                TimeMs = currentTime,
                Key = lane,
                Chord = GetChordForLane(lane),
                DurationMs = duration,
                Lyric = (i % 50 == 0) ? $"Section_{i / 50}" : null
            });

            // Periodically inject multi-lane chord (same TimeMs on distinct tracks)
            if (i > 0 && i % chordInterval == 0)
            {
                int chordLanesCount = (i % (chordInterval * 2) == 0) ? 3 : 2;
                for (int c = 1; c <= chordLanesCount; c++)
                {
                    var chordLane = SevenLanes[(laneIndex + c * 2) % SevenLanes.Length];
                    events.Add(new PerformanceEvent
                    {
                        TimeMs = currentTime,
                        Key = chordLane,
                        Chord = GetChordForLane(chordLane),
                        DurationMs = 45,
                        Lyric = null
                    });
                }
            }

            currentTime += stepMs;
            laneIndex++;
        }

        int totalDuration = events.Count > 0 ? events.Max(e => e.TimeMs + e.DurationMs) : 0;
        var sections = ExtractStressSections(events, totalDuration);

        return new PerformanceTimeline
        {
            Metadata = new PetitSongMetadata
            {
                Title = "7-Lane Peak Stress Chart",
                Artist = "Benchmark Suite",
                Bpm = bpm,
                Instrument = "guitar",
                TimeSignature = "4/4",
                Key = "C",
                Version = "1.0.0"
            },
            Events = events,
            Sections = sections,
            TotalDurationMs = totalDuration,
            FilePath = "synthetic://stress-7lane.json"
        };
    }

    /// <summary>
    /// Generates a serialized JSON string representing a 7-lane stress chart.
    /// </summary>
    public static string GenerateStressScoreJson(
        int noteCount = 500,
        int stepMs = 50,
        int chordInterval = 8,
        int holdInterval = 12)
    {
        var score = CreateStressSongScore(noteCount, stepMs, chordInterval, holdInterval);
        return JsonConvert.SerializeObject(score, Formatting.Indented);
    }

    /// <summary>
    /// Creates a strongly-typed PetitSongScore object with sequence and key mappings.
    /// </summary>
    public static PetitSongScore CreateStressSongScore(
        int noteCount = 500,
        int stepMs = 50,
        int chordInterval = 8,
        int holdInterval = 12)
    {
        var sequence = new List<PetitChordEvent>();
        int currentTime = 0;
        int laneIndex = 0;

        for (int i = 0; i < noteCount; i++)
        {
            var lane = SevenLanes[laneIndex % SevenLanes.Length];
            var chord = GetChordForLane(lane);
            bool isHold = (i > 0 && i % holdInterval == 0);
            int duration = isHold ? 300 : 45;

            sequence.Add(new PetitChordEvent
            {
                TimeMs = currentTime,
                Chord = chord,
                Key = lane,
                Action = isHold ? "hold" : "press",
                DurationMs = duration,
                Bar = i / 16,
                Beat = (i % 16) / 4.0,
                Lyric = (i % 50 == 0) ? $"Section_{i}" : null
            });

            if (i > 0 && i % chordInterval == 0)
            {
                var chordLane = SevenLanes[(laneIndex + 2) % SevenLanes.Length];
                sequence.Add(new PetitChordEvent
                {
                    TimeMs = currentTime,
                    Chord = GetChordForLane(chordLane),
                    Key = chordLane,
                    Action = "press",
                    DurationMs = 45,
                    Bar = i / 16,
                    Beat = (i % 16) / 4.0
                });
            }

            currentTime += stepMs;
            laneIndex++;
        }

        return new PetitSongScore
        {
            Metadata = new PetitSongMetadata
            {
                Title = "7-Lane Stress Chart",
                Artist = "Benchmark Suite",
                Bpm = 135.0,
                Instrument = "guitar",
                TimeSignature = "4/4",
                Key = "C",
                Version = "1.0.0"
            },
            KeyMapping = new Dictionary<string, string>(StandardKeyMapping),
            Sequence = sequence
        };
    }

    /// <summary>
    /// Creates a 50,000 note stress timeline to satisfy the 50k benchmark specification.
    /// </summary>
    public static PerformanceTimeline Create50kNoteStressTimeline()
    {
        return CreateSevenLaneStressTimeline(
            noteCount: 50_000,
            stepMs: 20,
            chordInterval: 6,
            holdInterval: 18);
    }

    /// <summary>
    /// Simulates preparation countdown negative timestamps (e.g. -3000ms, -2000ms, -1000ms).
    /// </summary>
    public static List<int> CreateNegativeCountdownSequence(int leadInDurationMs = 3000, int intervalMs = 1000)
    {
        var countdowns = new List<int>();
        for (int t = -leadInDurationMs; t < 0; t += intervalMs)
        {
            countdowns.Add(t);
        }
        return countdowns;
    }

    public static string GetChordForLane(string lane) => lane switch
    {
        "A" => "C",
        "S" => "Dm",
        "D" => "Em",
        "F" => "F",
        "J" => "G",
        "K" => "Am",
        "L" => "G7",
        _ => "C"
    };

    private static List<SongSectionMarker> ExtractStressSections(List<PerformanceEvent> events, int totalDuration)
    {
        if (events.Count == 0) return [];
        return
        [
            new SongSectionMarker { TimeMs = 0, Name = "Intro", SectionType = SongSectionType.Intro, NoteIndex = 0 },
            new SongSectionMarker { TimeMs = totalDuration / 4, Name = "Verse", SectionType = SongSectionType.Verse, NoteIndex = events.Count / 4 },
            new SongSectionMarker { TimeMs = totalDuration / 2, Name = "Chorus", SectionType = SongSectionType.Chorus, NoteIndex = events.Count / 2 },
            new SongSectionMarker { TimeMs = (totalDuration * 3) / 4, Name = "Outro", SectionType = SongSectionType.Outro, NoteIndex = (events.Count * 3) / 4 }
        ];
    }
}
