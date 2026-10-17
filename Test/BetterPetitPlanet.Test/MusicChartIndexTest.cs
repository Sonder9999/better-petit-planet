using System;
using System.Collections.Generic;
using System.Linq;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Rendering;
using Xunit;

namespace BetterPetitPlanet.Test;

public class MusicChartIndexTest
{
    [Fact]
    public void HoldNote_CrossingViewportLowerBound_IsRetrieved()
    {
        // Hold note starts at 1000ms, duration 2000ms (ends at 3000ms) on lane A
        var holdNote = new PerformanceEvent
        {
            NoteId = "hold-1",
            Key = "A",
            TimeMs = 1000,
            DurationMs = 2000,
            Kind = MusicNoteKind.Hold
        };

        var index = new MusicChartIndex([holdNote]);

        // 1. Viewport [1500, 2500]: Head (1000ms) has already passed below 1500ms,
        // but tail (3000ms) is still visible. Must be retrieved!
        var visibleNotes = index.GetVisibleNotes(1500, 2500);
        Assert.Single(visibleNotes);
        Assert.Equal("hold-1", visibleNotes[0].NoteId);

        // 2. Viewport [3000, 3500]: Exactly touching the tail at 3000ms
        var atTail = index.GetVisibleNotes(3000, 3500);
        Assert.Single(atTail);

        // 3. Viewport [3001, 4000]: Completely past the tail
        var pastTail = index.GetVisibleNotes(3001, 4000);
        Assert.Empty(pastTail);

        // 4. Viewport [0, 800]: Has not spawned yet
        var beforeSpawn = index.GetVisibleNotes(0, 800);
        Assert.Empty(beforeSpawn);

        // 5. Viewport [500, 1000]: Touching the head
        var atHead = index.GetVisibleNotes(500, 1000);
        Assert.Single(atHead);
    }

    [Fact]
    public void DenseChords_IntervalQuery_RetrievesAllSimultaneousNotes()
    {
        var notes = new List<PerformanceEvent>();
        string[] lanes = ["A", "S", "D", "F", "J", "K", "L"];

        // 7 simultaneous notes forming a peak chord at 2000ms
        for (int i = 0; i < lanes.Length; i++)
        {
            notes.Add(new PerformanceEvent
            {
                NoteId = $"chord-{lanes[i]}",
                Key = lanes[i],
                TimeMs = 2000,
                DurationMs = 45,
                Kind = MusicNoteKind.Tap
            });
        }

        // Additional notes before and after
        notes.Add(new PerformanceEvent { NoteId = "pre", Key = "A", TimeMs = 1000, DurationMs = 45 });
        notes.Add(new PerformanceEvent { NoteId = "post", Key = "L", TimeMs = 3000, DurationMs = 45 });

        var index = new MusicChartIndex(notes);

        var query = index.GetVisibleNotes(1800, 2200);
        Assert.Equal(7, query.Count);
        Assert.All(query, n => Assert.Equal(2000, n.TimeMs));

        var chords = MusicChartIndex.GroupChords(query).ToList();
        Assert.Single(chords);
        Assert.Equal(7, chords[0].Count);
    }

    [Fact]
    public void EmptyChart_ReturnsEmptyListWithoutThrowing()
    {
        var nullTimelineIndex = new MusicChartIndex(timeline: null);
        Assert.Equal(0, nullTimelineIndex.NoteCount);
        Assert.Empty(nullTimelineIndex.GetVisibleNotes(0, 5000));

        var emptyListIndex = new MusicChartIndex(events: []);
        Assert.Equal(0, emptyListIndex.NoteCount);
        Assert.Empty(emptyListIndex.GetVisibleNotes(0, 5000));

        Assert.Equal(0, MusicChartIndex.Empty.NoteCount);
        Assert.Empty(MusicChartIndex.Empty.GetVisibleNotes(0, 5000));
    }

    [Fact]
    public void BoundaryConditions_NotesExactlyOnEdges()
    {
        var notes = new List<PerformanceEvent>
        {
            new() { NoteId = "n1", Key = "A", TimeMs = 1000, DurationMs = 45 },
            new() { NoteId = "n2", Key = "S", TimeMs = 2000, DurationMs = 45 },
            new() { NoteId = "n3", Key = "D", TimeMs = 3000, DurationMs = 45 }
        };

        var index = new MusicChartIndex(notes);

        // Exact boundaries [1000, 3000] contains all 3
        var all = index.GetVisibleNotes(1000, 3000);
        Assert.Equal(["n1", "n2", "n3"], all.Select(n => n.NoteId).ToArray());

        // Interior range [1001, 2999] contains only n2
        var midOnly = index.GetVisibleNotes(1001, 2999);
        Assert.Equal(["n2"], midOnly.Select(n => n.NoteId).ToArray());

        // Touching lower edge [999, 1000] contains n1
        var lowerEdge = index.GetVisibleNotes(999, 1000);
        Assert.Equal(["n1"], lowerEdge.Select(n => n.NoteId).ToArray());

        // Touching upper edge [3000, 3001] contains n3
        var upperEdge = index.GetVisibleNotes(3000, 3001);
        Assert.Equal(["n3"], upperEdge.Select(n => n.NoteId).ToArray());

        // Inverted range (startTime > endTime) returns empty without exception
        Assert.Empty(index.GetVisibleNotes(2000, 1000));
    }

    [Fact]
    public void MixedHoldAndTapNotes_SortedOrderIsMaintained()
    {
        var notes = new List<PerformanceEvent>
        {
            new() { NoteId = "hold-early", Key = "A", TimeMs = 500, DurationMs = 1500, Kind = MusicNoteKind.Hold }, // Ends at 2000
            new() { NoteId = "tap-1", Key = "S", TimeMs = 1200, DurationMs = 45, Kind = MusicNoteKind.Tap },
            new() { NoteId = "tap-2", Key = "D", TimeMs = 1600, DurationMs = 45, Kind = MusicNoteKind.Tap }
        };

        var index = new MusicChartIndex(notes);

        // Window [1000, 1800]: hold-early starts before window (500ms < 1000ms),
        // but ends at 2000ms >= 1000ms.
        var visible = index.GetVisibleNotes(1000, 1800);
        Assert.Equal(3, visible.Count);
        Assert.Equal(["hold-early", "tap-1", "tap-2"], visible.Select(n => n.NoteId).ToArray());
    }
}
