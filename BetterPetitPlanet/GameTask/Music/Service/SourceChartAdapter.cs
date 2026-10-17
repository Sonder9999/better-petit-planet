using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BetterPetitPlanet.GameTask.Music.Model;

namespace BetterPetitPlanet.GameTask.Music.Service;

public static class SourceChartAdapter
{
    private static readonly HashSet<string> Buttons = ["Left_Left", "Left_Down", "Left_Right", "Right_Left",
        "Right_Down", "Right_Right", "Right_Up"];

    public static PerformanceTimeline AdaptJson(string json, IReadOnlyDictionary<string, MusicLane> laneMapping,
        string title = "")
    {
        ArgumentNullException.ThrowIfNull(laneMapping);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("buttonSequence", out var sequence) ||
            sequence.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Source chart requires a buttonSequence array.");
        var notes = new List<PerformanceEvent>();
        int index = 0;
        foreach (var raw in sequence.EnumerateArray())
        {
            string context = $"Event {index++}";
            if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("time", out var time) ||
                time.ValueKind != JsonValueKind.Number || !time.TryGetInt32(out int start) || start < 0)
                throw new InvalidDataException($"{context}: invalid or missing time.");
            bool hold = false;
            if (raw.TryGetProperty("isLongPress", out var longPress))
            {
                if (longPress.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException($"{context}: isLongPress must be boolean.");
                hold = longPress.GetBoolean();
            }
            int duration = 45;
            if (raw.TryGetProperty("longPressTime", out var length))
            {
                if (length.ValueKind != JsonValueKind.Number || !length.TryGetInt32(out int parsed) || parsed < 0)
                    throw new InvalidDataException($"{context}: invalid longPressTime.");
                if (hold) duration = parsed;
            }
            else if (hold) throw new InvalidDataException($"{context}: hold requires longPressTime.");
            if (hold && duration <= 0) throw new InvalidDataException($"{context}: hold duration must be positive.");
            // Source melody-only entries have no gameplay lane and are intentionally excluded.
            if (!raw.TryGetProperty("button", out var button)) continue;
            if (button.ValueKind != JsonValueKind.String || button.GetString() is not { } name ||
                !Buttons.Contains(name) || !laneMapping.TryGetValue(name, out var lane) || !Enum.IsDefined(lane))
                throw new InvalidDataException($"{context}: source button needs an explicit valid lane mapping.");
            notes.Add(new()
            {
                NoteId = $"source-{index - 1}", Key = lane.ToString(), TimeMs = start,
                DurationMs = duration, Kind = hold ? MusicNoteKind.Hold : MusicNoteKind.Tap
            });
        }
        return ScoreNormalizer.CopyTimeline(new()
        {
            Metadata = new() { Title = title, Instrument = "recorder" },
            Events = notes.OrderBy(note => note.TimeMs).ToList()
        });
    }
}
