using System;
using System.Collections.Generic;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Model;

namespace BetterPetitPlanet.GameTask.Music.Rendering.Gpu;

internal readonly record struct MusicGpuNote(
    int Lane, int TimeMs, int DurationMs, bool IsHold, bool IsHolding,
    NoteRenderBounds Head, NoteRenderBounds Tail, NoteRenderBounds Beam);

/// <summary>
/// Builds reusable CPU geometry in the same DIPs and clock domain as the WPF playfield.
/// Only chart changes copy note data; a steady-state frame does not allocate collections.
/// </summary>
internal sealed class MusicGpuFrameBuilder
{
    private MusicChartIndex? _source;
    private PerformanceEvent[] _notes = [];
    private PerformanceEvent[] _holds = [];
    private double[] _holdPrefixEnd = [];
    private readonly List<MusicGpuNote> _visible = new(128);

    public IReadOnlyList<MusicGpuNote> Notes => _visible;
    public double DisplayTimeMs { get; private set; }

    public void Build(MusicRenderSnapshot snapshot, MusicChartIndex chart,
        MusicOverlayConfig config, double monotonicMs, double width, double height, int activeLaneMask)
    {
        if (!ReferenceEquals(chart, _source)) SetChart(chart);
        _visible.Clear();
        DisplayTimeMs = snapshot.ComputeCurrentDisplayTimeMs(monotonicMs, config.VisualOffsetMs);
        if (width <= 0 || height <= 0) return;

        // Holds can begin before the visible window. Prefix maximum ends let us stop
        // before expired intervals without scanning all earlier tap notes.
        int holdEnd = LowerBound(_holds, DisplayTimeMs);
        for (int i = holdEnd - 1; i >= 0 && _holdPrefixEnd[i] > DisplayTimeMs; i--)
            Add(_holds[i]);

        double lastTime = DisplayTimeMs + config.FallDurationMs;
        for (int i = LowerBound(_notes, DisplayTimeMs); i < _notes.Length && _notes[i].TimeMs <= lastTime; i++)
            Add(_notes[i]);
        _visible.Sort(static (a, b) => a.TimeMs != b.TimeMs ? a.TimeMs.CompareTo(b.TimeMs) : a.Lane.CompareTo(b.Lane));

        void Add(PerformanceEvent note)
        {
            int duration = MusicChartIndex.GetNoteDuration(note);
            if (duration == 0 && DisplayTimeMs >= note.TimeMs) return;
            if (duration > 0 && DisplayTimeMs >= (double)note.TimeMs + duration) return;
            int lane = MusicPlayfieldGeometry.GetLaneIndex(note.Key);
            double x = MusicPlayfieldGeometry.GetLaneX(lane, width, config.Layout);
            double y = MusicPlayfieldGeometry.TimeToY(note.TimeMs, DisplayTimeMs,
                config.FallDurationMs, height, config.Layout);
            double noteWidth = MusicPlayfieldGeometry.GetNoteWidth(width, config.Layout);
            double noteHeight = MusicPlayfieldGeometry.GetNoteHeight(width, config.Layout);
            var head = new NoteRenderBounds(x - noteWidth / 2, y - noteHeight / 2, noteWidth, noteHeight);
            if (duration == 0)
            {
                _visible.Add(new MusicGpuNote(lane, note.TimeMs, 0, false, false, head, default, default));
                return;
            }

            bool held = (activeLaneMask & (1 << lane)) != 0;
            var hold = MusicPlayfieldGeometry.ComputeHoldBounds(note, DisplayTimeMs,
                config.FallDurationMs, width, height, config.Layout, held);
            // Visual caps share the textured WPF anchor, independently of input state.
            // Keep raw held/unheld beam geometry separate from the cap lifetime.
            double judgeY = MusicPlayfieldGeometry.GetJudgementY(height, config.Layout);
            head = head with { Y = Math.Min(judgeY, y) - noteHeight / 2 };
            double tailWidth = noteWidth * .85;
            double tailHeight = noteHeight * .4;
            var tail = new NoteRenderBounds(x - tailWidth / 2, hold.TailY - tailHeight / 2, tailWidth, tailHeight);
            double bodyWidth = MusicPlayfieldGeometry.GetHoldBeamBodyWidth(width, config.Layout);
            double beamTop = hold.TailY - MusicPlayfieldGeometry.GetHoldBeamTailExtension(width, config.Layout);
            double beamBottom = Math.Min(judgeY, hold.HeadY) + 6 * width / 1920 * config.Layout.Scale;
            var beam = new NoteRenderBounds(x - bodyWidth / 2, beamTop, bodyWidth, Math.Max(0, beamBottom - beamTop));
            _visible.Add(new MusicGpuNote(lane, note.TimeMs, duration, true, held, head, tail, beam));
        }
    }

    private void SetChart(MusicChartIndex chart)
    {
        var notes = new PerformanceEvent[chart.NoteCount];
        var holds = new List<PerformanceEvent>();
        for (int i = 0; i < notes.Length; i++)
        {
            var source = chart.AllNotes[i];
            // PerformanceEvent is mutable; isolate the render thread from chart editors.
            var copy = new PerformanceEvent
            {
                Key = source.Key, TimeMs = source.TimeMs,
                DurationMs = source.DurationMs, Kind = source.Kind
            };
            notes[i] = copy;
            if (MusicChartIndex.GetNoteDuration(copy) > 0) holds.Add(copy);
        }
        _notes = notes;
        _holds = holds.ToArray();
        _holdPrefixEnd = new double[_holds.Length];
        double maxEnd = double.NegativeInfinity;
        for (int i = 0; i < _holds.Length; i++)
        {
            maxEnd = Math.Max(maxEnd, (double)_holds[i].TimeMs + MusicChartIndex.GetNoteDuration(_holds[i]));
            _holdPrefixEnd[i] = maxEnd;
        }
        _source = chart;
    }

    private static int LowerBound(PerformanceEvent[] notes, double time)
    {
        int low = 0, high = notes.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (notes[middle].TimeMs < time) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}
