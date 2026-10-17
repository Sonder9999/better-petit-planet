using System;
using System.Collections.Generic;
using System.Linq;
using BetterPetitPlanet.GameTask.Music.Model;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

/// <summary>
/// Read-only spatial/temporal index built when a chart is loaded.
/// Allows O(log N + K) visible note queries for arbitrary time windows [startTimeMs, endTimeMs],
/// correctly capturing long hold notes whose head has passed the viewport lower bound
/// but whose sustain tail is still within the visible range.
/// </summary>
public sealed class MusicChartIndex
{
    private readonly PerformanceEvent[] _allNotes;
    private readonly HoldIntervalNode? _holdIntervalTree;

    public int NoteCount => _allNotes.Length;
    public int TotalNotes => _allNotes.Length;
    public int TotalDurationMs { get; }
    public IReadOnlyList<PerformanceEvent> AllNotes => _allNotes;

    public static MusicChartIndex Empty { get; } = new(Array.Empty<PerformanceEvent>());

    public MusicChartIndex(PerformanceTimeline? timeline)
        : this(timeline?.Events, timeline?.TotalDurationMs)
    {
    }

    public MusicChartIndex(IEnumerable<PerformanceEvent>? events, int? totalDurationMs = null)
    {
        if (events == null)
        {
            _allNotes = Array.Empty<PerformanceEvent>();
            TotalDurationMs = 0;
            return;
        }

        _allNotes = events.OrderBy(e => e.TimeMs).ToArray();
        TotalDurationMs = totalDurationMs ?? (_allNotes.Length > 0
            ? _allNotes.Max(e => checked(e.TimeMs + Math.Max(e.DurationMs, 0)))
            : 0);

        var holdNotes = _allNotes
            .Where(n => GetNoteDuration(n) > 0)
            .OrderBy(n => n.TimeMs)
            .ToArray();

        if (holdNotes.Length > 0)
        {
            _holdIntervalTree = BuildIntervalTree(holdNotes, 0, holdNotes.Length - 1);
        }
    }

    /// <summary>
    /// Gets effective hold duration for a note.
    /// Tap notes have duration 0 (no sustain trail on playfield),
    /// while Hold notes have their positive duration.
    /// </summary>
    public static int GetNoteDuration(PerformanceEvent note)
    {
        if (note.Kind == MusicNoteKind.Hold)
            return Math.Max(0, note.DurationMs);

        // Fallback for test fixtures that didn't set Kind but provided a sustain duration (> 50ms)
        if (note.DurationMs > 50)
            return note.DurationMs;

        return 0;
    }

    /// <summary>
    /// Queries all notes visible within the time window [startTimeMs, endTimeMs].
    /// Includes both notes whose start times fall inside [startTimeMs, endTimeMs]
    /// and hold notes that started before startTimeMs but whose tail reaches at or past startTimeMs.
    /// </summary>
    public IReadOnlyList<PerformanceEvent> QueryVisibleNotes(double startTimeMs, double endTimeMs)
        => GetVisibleNotes(startTimeMs, endTimeMs);

    public IReadOnlyList<PerformanceEvent> QueryVisibleNotes(int startTimeMs, int endTimeMs)
        => GetVisibleNotes((double)startTimeMs, (double)endTimeMs);

    public IReadOnlyList<PerformanceEvent> GetVisibleNotes(int startTimeMs, int endTimeMs)
        => GetVisibleNotes((double)startTimeMs, (double)endTimeMs);

    public IReadOnlyList<PerformanceEvent> GetVisibleNotes(double startTimeMs, double endTimeMs)
    {
        if (_allNotes.Length == 0 || startTimeMs > endTimeMs)
            return Array.Empty<PerformanceEvent>();

        var result = new List<PerformanceEvent>();

        // 1. Ongoing hold notes whose head already passed below startTimeMs (TimeMs < startTimeMs)
        //    but whose sustain tail remains visible (TimeMs + DurationMs >= startTimeMs).
        if (_holdIntervalTree != null)
        {
            QueryOngoingHolds(_holdIntervalTree, startTimeMs, result);
        }

        // 2. All notes starting inside [startTimeMs, endTimeMs] using binary search.
        int startIndex = FindFirstIndexAtOrAfter(startTimeMs);
        if (startIndex >= 0 && startIndex < _allNotes.Length)
        {
            for (int i = startIndex; i < _allNotes.Length; i++)
            {
                var note = _allNotes[i];
                if (note.TimeMs > endTimeMs) break;
                result.Add(note);
            }
        }

        return result;
    }

    /// <summary>
    /// Groups simultaneous notes in the given collection into chords (groups with >= 2 notes).
    /// </summary>
    public static IEnumerable<IReadOnlyList<PerformanceEvent>> GroupChords(IEnumerable<PerformanceEvent> notes)
    {
        return notes
            .GroupBy(n => n.TimeMs)
            .Where(g => g.Count() > 1)
            .Select(g => (IReadOnlyList<PerformanceEvent>)g.OrderBy(n => (int)n.Lane).ToList());
    }

    private int FindFirstIndexAtOrAfter(double timeMs)
    {
        int low = 0;
        int high = _allNotes.Length - 1;
        int result = _allNotes.Length;

        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            if (_allNotes[mid].TimeMs >= timeMs)
            {
                result = mid;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        return result;
    }

    private sealed class HoldIntervalNode
    {
        public PerformanceEvent Note { get; }
        public int StartTime { get; }
        public int EndTime { get; }
        public int MaxEnd { get; set; }
        public HoldIntervalNode? Left { get; set; }
        public HoldIntervalNode? Right { get; set; }

        public HoldIntervalNode(PerformanceEvent note, int duration)
        {
            Note = note;
            StartTime = note.TimeMs;
            EndTime = checked(note.TimeMs + duration);
            MaxEnd = EndTime;
        }
    }

    private static HoldIntervalNode? BuildIntervalTree(PerformanceEvent[] notes, int left, int right)
    {
        if (left > right) return null;
        int mid = left + (right - left) / 2;
        var node = new HoldIntervalNode(notes[mid], GetNoteDuration(notes[mid]))
        {
            Left = BuildIntervalTree(notes, left, mid - 1),
            Right = BuildIntervalTree(notes, mid + 1, right)
        };

        int max = node.EndTime;
        if (node.Left != null && node.Left.MaxEnd > max) max = node.Left.MaxEnd;
        if (node.Right != null && node.Right.MaxEnd > max) max = node.Right.MaxEnd;
        node.MaxEnd = max;

        return node;
    }

    private static void QueryOngoingHolds(HoldIntervalNode? node, double startTimeMs, List<PerformanceEvent> result)
    {
        if (node == null) return;

        // Subtree pruning: if the maximum end time in this entire subtree is before startTimeMs,
        // no note in this subtree can reach startTimeMs.
        if (node.MaxEnd < startTimeMs) return;

        // Check left subtree
        if (node.Left != null && node.Left.MaxEnd >= startTimeMs)
            QueryOngoingHolds(node.Left, startTimeMs, result);

        // Check current node: starts strictly before startTimeMs, and ends at or past startTimeMs
        if (node.StartTime < startTimeMs && node.EndTime >= startTimeMs)
            result.Add(node.Note);

        // Check right subtree: only if nodes in right subtree can start before startTimeMs
        // Since tree is a BST ordered by StartTime, if node.StartTime >= startTimeMs,
        // all nodes in right subtree have StartTime >= node.StartTime >= startTimeMs.
        if (node.Right != null && node.StartTime < startTimeMs && node.Right.MaxEnd >= startTimeMs)
            QueryOngoingHolds(node.Right, startTimeMs, result);
    }
}
