using System;
using System.Collections.Generic;
using System.Linq;
using BetterPetitPlanet.GameTask.Music.Model;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

/// <summary>Hit instances have independent lifetimes. Chart animation uses song time;
/// uncharted keyboard feedback uses wall time. A held key never restarts a burst.</summary>
public sealed class MusicEffectTimeline
{
    public readonly record struct Hit(string Key, double StartMs, PerformanceEvent? Note = null, bool IsTail = false);
    public List<Hit> Hits { get; } = [];
    private readonly HashSet<string> _previousKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<PerformanceEvent> _begunHolds = [];
    private double _lastTime = double.NaN;
    public bool HasReleasedFeedback => Hits.Any(hit => !_previousKeys.Contains(hit.Key));

    public void Clear()
    {
        Hits.Clear(); _previousKeys.Clear(); _begunHolds.Clear(); _lastTime = double.NaN;
    }

    public void Update(MusicRenderSnapshot snapshot, IReadOnlyList<PerformanceEvent> notes,
        bool hasChart, double time, double duration)
    {
        if (time < _lastTime) Clear();
        if (hasChart)
        {
            bool preview = snapshot.SessionId.StartsWith("preview-", StringComparison.Ordinal);
            foreach (var note in notes)
            {
                bool active = snapshot.ActiveKeys.Contains(note.Key, StringComparer.OrdinalIgnoreCase);
                TryAdd(note, false, note.TimeMs, preview || active && time - note.TimeMs < 160);
                int holdDuration = MusicChartIndex.GetNoteDuration(note);
                if (holdDuration > 0)
                    TryAdd(note, true, note.TimeMs + holdDuration,
                        preview || _begunHolds.Contains(note) && _previousKeys.Contains(note.Key) && _lastTime < note.TimeMs + holdDuration);
            }
        }
        else
        {
            foreach (string key in snapshot.ActiveKeys)
                if (!_previousKeys.Contains(key)) Hits.Add(new Hit(key, time));
        }
        _begunHolds.RemoveWhere(note => !snapshot.ActiveKeys.Contains(note.Key, StringComparer.OrdinalIgnoreCase) || time >= note.TimeMs + MusicChartIndex.GetNoteDuration(note));
        Hits.RemoveAll(hit => time - hit.StartMs >= duration || time < hit.StartMs);
        _previousKeys.Clear();
        foreach (var key in snapshot.ActiveKeys) _previousKeys.Add(key);
        _lastTime = time;

        void TryAdd(PerformanceEvent note, bool tail, double start, bool eligible)
        {
            if (!eligible || time < start || time - start >= duration) return;
            if (Hits.Any(hit => ReferenceEquals(hit.Note, note) && hit.IsTail == tail)) return;
            Hits.Add(new Hit(note.Key, start, note, tail));
            if (!tail && MusicChartIndex.GetNoteDuration(note) > 0) _begunHolds.Add(note);
        }
    }
}
