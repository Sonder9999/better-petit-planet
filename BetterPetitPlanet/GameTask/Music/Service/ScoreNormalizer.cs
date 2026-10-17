using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterPetitPlanet.GameTask.Music.Model;

namespace BetterPetitPlanet.GameTask.Music.Service;

public static class ScoreNormalizer
{
    public static PerformanceTimeline CopyTimeline(PerformanceTimeline source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var notes = source.Events.Select(note => new PerformanceEvent
        {
            NoteId = note.NoteId,
            Key = note.Key, TimeMs = note.TimeMs,
            DurationMs = note.Kind == MusicNoteKind.Tap && note.DurationMs > 0
                ? Math.Clamp(note.DurationMs, 20, 500) : note.DurationMs,
            Kind = note.Kind, Chord = note.Chord, Lyric = note.Lyric
        }).OrderBy(note => note.TimeMs).ToList();
        Validate(notes);
        int leadIn = NormalizeOrigin(notes);
        int duration = notes.Count > 0 ? notes.Max(note => checked(note.TimeMs + note.DurationMs)) : 0;
        var timeline = new PerformanceTimeline
        {
            Metadata = new PetitSongMetadata
            {
                Title = source.Metadata.Title, Artist = source.Metadata.Artist, Bpm = source.Metadata.Bpm,
                TimeSignature = source.Metadata.TimeSignature, Instrument = source.Metadata.Instrument,
                Key = source.Metadata.Key, Version = source.Metadata.Version
            }, Events = notes, FilePath = source.FilePath,
            TotalDurationMs = leadIn > 0 ? duration : Math.Max(duration, source.TotalDurationMs),
            Sections = source.Sections.Select(section => new SongSectionMarker
            {
                Name = section.Name, ShortName = section.ShortName, SectionType = section.SectionType,
                NoteIndex = section.NoteIndex, TimeMs = Math.Max(0, section.TimeMs - leadIn),
                NormalizedProgress = section.NormalizedProgress
            }).ToList()
        };
        if (timeline.Sections.Count == 0) timeline.Sections = PetitScoreParser.ExtractSections(notes, timeline.TotalDurationMs);
        return timeline;
    }

    public static List<PerformanceEvent> Normalize(PetitSongScore score)
    {
        if (score.Sequence == null || score.Sequence.Count == 0)
            throw new InvalidDataException("Score sequence is empty.");
        var events = score.Sequence.Select((note, index) =>
        {
            if (note == null) throw new InvalidDataException($"Event {index}: null note.");
            var key = note.Key?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(key) && score.KeyMapping != null &&
                score.KeyMapping.TryGetValue(note.Chord ?? "", out var mapped))
                key = mapped.Trim().ToUpperInvariant();
            var action = note.Action?.Trim().ToLowerInvariant();
            if (action is not ("press" or "tap" or "hold"))
                throw new InvalidDataException($"Event {index}: unknown action {action}.");
            if (note.Type is not (null or "tap" or "hold"))
                throw new InvalidDataException($"Event {index}: unknown type {note.Type}.");
            bool hold = note.Type == "hold";
            if (note.DurationMs <= 0) throw new InvalidDataException($"Event {index}: duration must be positive.");
            return new PerformanceEvent
            {
                NoteId = $"note-{index}", Key = key ?? "", TimeMs = note.TimeMs,
                DurationMs = hold ? note.DurationMs : Math.Clamp(note.DurationMs, 20, 500),
                Kind = hold ? MusicNoteKind.Hold : MusicNoteKind.Tap,
                Chord = note.Chord ?? "", Lyric = note.Lyric
            };
        }).OrderBy(note => note.TimeMs).ToList();
        Validate(events);
        NormalizeOrigin(events);
        return events;
    }

    public static void Validate(IReadOnlyList<PerformanceEvent> events)
    {
        var active = new Dictionary<MusicLane, (int LastStart, int End, int HoldEnd)>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < events.Count; i++)
        {
            var note = events[i];
            if (!string.IsNullOrEmpty(note.NoteId) && !ids.Add(note.NoteId))
                throw new InvalidDataException($"Event {i}: duplicate note ID '{note.NoteId}'.");
            if (note.TimeMs < 0 || note.DurationMs <= 0 || (long)note.TimeMs + note.DurationMs > int.MaxValue ||
                !Enum.TryParse<MusicLane>(note.Key, out var lane) || !Enum.IsDefined(lane) || note.Key != lane.ToString() ||
                !Enum.IsDefined(note.Kind))
                throw new InvalidDataException($"Event {i}: invalid time, duration, kind or lane '{note.Key}'.");
            if (i > 0 && events[i - 1].TimeMs > note.TimeMs)
                throw new InvalidDataException($"Event {i}: notes must be ordered.");
            bool exists = active.TryGetValue(lane, out var prior);
            if (exists && (note.TimeMs == prior.LastStart ||
                note.TimeMs < (note.Kind == MusicNoteKind.Hold ? prior.End : prior.HoldEnd)))
                throw new InvalidDataException($"Event {i}: duplicate or overlapping hold on lane {lane}.");
            int end = note.TimeMs + note.DurationMs;
            active[lane] = (note.TimeMs, Math.Max(prior.End, end),
                note.Kind == MusicNoteKind.Hold ? Math.Max(prior.HoldEnd, end) : prior.HoldEnd);
        }
    }

    public static int NormalizeOrigin(IReadOnlyList<PerformanceEvent> events)
    {
        if (events.Count == 0) return 0;
        var ids = events.Where(note => !string.IsNullOrEmpty(note.NoteId)).Select(note => note.NoteId)
            .ToHashSet(StringComparer.Ordinal);
        int nextId = 0;
        int leadIn = events[0].TimeMs;
        for (int i = 0; i < events.Count; i++)
        {
            var note = events[i];
            note.TimeMs -= leadIn;
            if (string.IsNullOrEmpty(note.NoteId))
            {
                string generated;
                do { generated = $"note-{nextId++}"; } while (!ids.Add(generated));
                note.NoteId = generated;
            }
        }
        foreach (var group in events.GroupBy(e => e.TimeMs))
            foreach (var note in group)
                note.SyncGroupId = group.Count() > 1 ? $"time-{note.TimeMs}" : null;
        return leadIn;
    }
}
