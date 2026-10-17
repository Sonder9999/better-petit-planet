using System;
using System.Collections.Generic;
using System.Linq;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Rendering;

namespace BetterPetitPlanet.GameTask.Music.Rendering.Gpu;

/// <summary>
/// Lightweight hit event received from input or playback scheduling.
/// </summary>
public readonly record struct MusicGpuHitEvent(
    string LaneKey,
    int LaneIndex,
    double ChartTimeMs,
    double HostMonotonicMs,
    string? NoteId = null,
    string Source = "automatic"
);

/// <summary>
/// GPU effect timeline tracking hit burst lifecycles.
/// Follows the exact rules of <see cref="MusicEffectTimeline"/>:
/// independent burst births, monotonic/song clock tracking, session reset on ID change,
/// backward seek clearance, hold head-tail tracking, and key-press edge detection.
/// </summary>
public sealed class MusicGpuEffectTimeline
{
    public readonly record struct Hit(string Key, int Lane, double StartMs, PerformanceEvent? Note = null, bool IsTail = false)
    {
        public Hit(string key, double startMs, PerformanceEvent? note = null, bool isTail = false)
            : this(key, MusicPlayfieldGeometry.GetLaneIndex(key), startMs, note, isTail)
        {
        }
    }

    public List<Hit> Hits { get; } = [];
    private readonly HashSet<string> _previousKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<PerformanceEvent> _begunHolds = [];
    private double _lastTime = double.NaN;
    private string? _lastSessionId;

    public bool HasReleasedFeedback => Hits.Any(hit => !_previousKeys.Contains(hit.Key));

    public void Clear()
    {
        Hits.Clear();
        _previousKeys.Clear();
        _begunHolds.Clear();
        _lastTime = double.NaN;
    }

    public void Update(MusicRenderSnapshot snapshot, MusicChartIndex chart,
        double time, double duration, double fallDurationMs,
        IReadOnlyList<MusicGpuHitEvent>? hitEvents = null)
    {
        bool hasChart = chart.NoteCount > 0;
        var notes = hasChart
            ? chart.GetVisibleNotes(time - duration, time + fallDurationMs)
            : Array.Empty<PerformanceEvent>();
        Update(snapshot, notes, hasChart, time, duration, hitEvents);
    }

    public void Update(MusicRenderSnapshot snapshot, IReadOnlyList<PerformanceEvent> notes,
        bool hasChart, double time, double duration)
        => Update(snapshot, notes, hasChart, time, duration, null);

    public void Update(MusicRenderSnapshot snapshot, IReadOnlyList<PerformanceEvent> notes,
        bool hasChart, double time, double duration,
        IReadOnlyList<MusicGpuHitEvent>? hitEvents)
    {
        if (!string.Equals(_lastSessionId, snapshot.SessionId, StringComparison.Ordinal))
        {
            Clear();
            _lastSessionId = snapshot.SessionId;
        }

        if (time < _lastTime) Clear();

        // 1. Process explicit hit events (event-driven pipeline)
        if (hitEvents != null && hitEvents.Count > 0)
        {
            for (int i = 0; i < hitEvents.Count; i++)
            {
                var hitEvent = hitEvents[i];
                int lane = hitEvent.LaneIndex;
                if (lane < 0 || lane >= 7)
                {
                    lane = MusicPlayfieldGeometry.GetLaneIndex(hitEvent.LaneKey);
                }
                if (lane < 0 || lane >= 7) continue;

                string key = string.IsNullOrEmpty(hitEvent.LaneKey)
                    ? MusicPlayfieldGeometry.LaneKeys[lane]
                    : hitEvent.LaneKey;

                if (hasChart)
                {
                    PerformanceEvent? matchedNote = null;
                    if (!string.IsNullOrEmpty(hitEvent.NoteId))
                    {
                        for (int n = 0; n < notes.Count; n++)
                        {
                            if (string.Equals(notes[n].NoteId, hitEvent.NoteId, StringComparison.Ordinal))
                            {
                                matchedNote = notes[n];
                                break;
                            }
                        }
                    }

                    if (matchedNote == null && hitEvent.ChartTimeMs >= 0)
                    {
                        double bestDiff = double.MaxValue;
                        for (int n = 0; n < notes.Count; n++)
                        {
                            var note = notes[n];
                            if (MusicPlayfieldGeometry.GetLaneIndex(note.Key) == lane)
                            {
                                double diff = Math.Abs(note.TimeMs - hitEvent.ChartTimeMs);
                                if (diff <= 150 && diff < bestDiff)
                                {
                                    bestDiff = diff;
                                    matchedNote = note;
                                }
                            }
                        }
                    }

                    if (matchedNote != null)
                    {
                        if (!Hits.Any(hit => ReferenceEquals(hit.Note, matchedNote) && !hit.IsTail))
                        {
                            Hits.Add(new Hit(matchedNote.Key, lane, time, matchedNote, false));
                            if (MusicChartIndex.GetNoteDuration(matchedNote) > 0)
                            {
                                _begunHolds.Add(matchedNote);
                            }
                        }
                    }
                    else
                    {
                        Hits.Add(new Hit(key, lane, time));
                    }
                }
                else
                {
                    Hits.Add(new Hit(key, lane, time));
                }
            }
        }

        // 2. Process preview mode, hold tails, and snapshot fallback
        if (hasChart)
        {
            bool preview = snapshot.SessionId.StartsWith("preview-", StringComparison.Ordinal);
            foreach (var note in notes)
            {
                bool active = snapshot.ActiveKeys.Contains(note.Key, StringComparer.OrdinalIgnoreCase);
                TryAdd(note, false, note.TimeMs, preview || (active && time - note.TimeMs < 160));
                int holdDuration = MusicChartIndex.GetNoteDuration(note);
                if (holdDuration > 0)
                {
                    TryAdd(note, true, note.TimeMs + holdDuration,
                        preview || (_begunHolds.Contains(note) && _previousKeys.Contains(note.Key) && _lastTime < note.TimeMs + holdDuration));
                }
            }
        }
        else
        {
            foreach (string key in snapshot.ActiveKeys)
            {
                if (!_previousKeys.Contains(key))
                {
                    int lane = MusicPlayfieldGeometry.GetLaneIndex(key);
                    if (lane >= 0 && !Hits.Any(h => h.Lane == lane && h.StartMs == time))
                    {
                        Hits.Add(new Hit(key, lane, time));
                    }
                }
            }
        }

        _begunHolds.RemoveWhere(note => !snapshot.ActiveKeys.Contains(note.Key, StringComparer.OrdinalIgnoreCase)
                                     || time >= note.TimeMs + MusicChartIndex.GetNoteDuration(note));
        Hits.RemoveAll(hit => time - hit.StartMs >= duration || time < hit.StartMs);
        _previousKeys.Clear();
        foreach (var key in snapshot.ActiveKeys) _previousKeys.Add(key);
        _lastTime = time;

        void TryAdd(PerformanceEvent note, bool tail, double start, bool eligible)
        {
            if (!eligible || time < start || time - start >= duration) return;
            if (Hits.Any(hit => ReferenceEquals(hit.Note, note) && hit.IsTail == tail)) return;
            int lane = MusicPlayfieldGeometry.GetLaneIndex(note.Key);
            if (lane < 0) return;
            Hits.Add(new Hit(note.Key, lane, start, note, tail));
            if (!tail && MusicChartIndex.GetNoteDuration(note) > 0) _begunHolds.Add(note);
        }
    }
}
