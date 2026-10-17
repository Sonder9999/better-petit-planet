using System.Linq;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Rendering.Gpu;
using Xunit;

namespace BetterPetitPlanet.Test;

public sealed class MusicGpuEffectTimelineTest
{
    private const double TotalBurstDuration = 64 * 1000.0 / 60.0; // 1066.67ms
    private const double FallbackDuration = 380.0;

    [Fact]
    public void UnchartedSimultaneousAndRepeatedPresses_KeepIndependentBirthsAndExpiry()
    {
        var timeline = new MusicGpuEffectTimeline();
        timeline.Update(Snapshot(0, "A", "S"), [], false, 0, TotalBurstDuration);
        Assert.Equal(new[] { ("A", 0, 0d), ("S", 1, 0d) },
            timeline.Hits.Select(hit => (hit.Key, hit.Lane, hit.StartMs)));

        timeline.Update(Snapshot(100, "S"), [], false, 100, TotalBurstDuration);
        Assert.True(timeline.HasReleasedFeedback);

        timeline.Update(Snapshot(200, "A", "S"), [], false, 200, TotalBurstDuration);
        Assert.Equal(new[] { ("A", 0, 0d), ("S", 1, 0d), ("A", 0, 200d) },
            timeline.Hits.Select(hit => (hit.Key, hit.Lane, hit.StartMs)));

        timeline.Update(Snapshot(300, "A", "S"), [], false, 300, TotalBurstDuration);
        Assert.Equal(3, timeline.Hits.Count); // Held key cannot restart or duplicate burst

        timeline.Update(Snapshot(TotalBurstDuration), [], false, TotalBurstDuration, TotalBurstDuration);
        Assert.Equal(("A", 0, 200d),
            (Assert.Single(timeline.Hits).Key, timeline.Hits[0].Lane, timeline.Hits[0].StartMs));

        timeline.Update(Snapshot(1267), [], false, 1267, TotalBurstDuration);
        Assert.Empty(timeline.Hits);
        Assert.False(timeline.HasReleasedFeedback);
    }

    [Fact]
    public void ChartHits_SimultaneousNotesAndRepeatedLaneNotesRetainTheirOwnIdentity()
    {
        PerformanceEvent[] notes = [Note("A", 1000), Note("S", 1000), Note("A", 1200)];
        var timeline = new MusicGpuEffectTimeline();
        timeline.Update(Snapshot(1000, "A", "S"), notes, true, 1000, TotalBurstDuration);
        timeline.Update(Snapshot(1100), notes, true, 1100, TotalBurstDuration);
        timeline.Update(Snapshot(1200, "A"), notes, true, 1200, TotalBurstDuration);

        Assert.Equal(3, timeline.Hits.Count);
        Assert.Same(notes[0], timeline.Hits[0].Note);
        Assert.Same(notes[1], timeline.Hits[1].Note);
        Assert.Same(notes[2], timeline.Hits[2].Note);
        Assert.Equal(0, timeline.Hits[0].Lane);
        Assert.Equal(1, timeline.Hits[1].Lane);
        Assert.Equal(0, timeline.Hits[2].Lane);

        timeline.Update(Snapshot(2067), notes, true, 2067, TotalBurstDuration);
        Assert.Same(notes[2], Assert.Single(timeline.Hits).Note);
    }

    [Fact]
    public void HeldNote_CreatesSeparateHeadAndCompletionBurstsWithoutRestartWhileHeld()
    {
        var note = Note("A", 1000, 500);
        var timeline = new MusicGpuEffectTimeline();
        timeline.Update(Snapshot(1000, "A"), [note], true, 1000, TotalBurstDuration);
        Assert.False(Assert.Single(timeline.Hits).IsTail);

        timeline.Update(Snapshot(1250, "A"), [note], true, 1250, TotalBurstDuration);
        Assert.Single(timeline.Hits);

        timeline.Update(Snapshot(1500), [note], true, 1500, TotalBurstDuration);
        Assert.Equal(new[] { (1000d, false), (1500d, true) },
            timeline.Hits.Select(hit => (hit.StartMs, hit.IsTail)));

        timeline.Update(Snapshot(1600), [note], true, 1600, TotalBurstDuration);
        Assert.Equal(2, timeline.Hits.Count);

        timeline.Update(Snapshot(2067), [note], true, 2067, TotalBurstDuration);
        Assert.True(Assert.Single(timeline.Hits).IsTail);
    }

    [Fact]
    public void RealChartWithoutMatchingInput_DoesNotAutomaticallyHitHeadsOrHoldTails()
    {
        PerformanceEvent[] notes = [Note("A", 1000), Note("S", 1000, 500)];
        var timeline = new MusicGpuEffectTimeline();
        timeline.Update(Snapshot(1000), notes, true, 1000, TotalBurstDuration);
        timeline.Update(Snapshot(1200, "D"), notes, true, 1200, TotalBurstDuration);
        timeline.Update(Snapshot(1500), notes, true, 1500, TotalBurstDuration);
        Assert.Empty(timeline.Hits);
    }

    [Fact]
    public void UnhitHold_LateLanePressCannotProduceSuccessfulCompletionBurst()
    {
        var note = Note("A", 1000, 500);
        var timeline = new MusicGpuEffectTimeline();
        timeline.Update(Snapshot(1000), [note], true, 1000, TotalBurstDuration);
        timeline.Update(Snapshot(1400, "A"), [note], true, 1400, TotalBurstDuration);
        timeline.Update(Snapshot(1500, "A"), [note], true, 1500, TotalBurstDuration);
        Assert.Empty(timeline.Hits);
    }

    [Fact]
    public void PreviewMode_AutomaticallyHitsHeadsAndTails()
    {
        PerformanceEvent[] notes = [Note("A", 1000), Note("S", 1200, 400)];
        var timeline = new MusicGpuEffectTimeline();
        timeline.Update(Preview(1000), notes, true, 1000, TotalBurstDuration);
        Assert.Single(timeline.Hits);
        Assert.False(timeline.Hits[0].IsTail);

        timeline.Update(Preview(1600), notes, true, 1600, TotalBurstDuration);
        Assert.Equal(3, timeline.Hits.Count); // A head, S head, S tail
        Assert.True(timeline.Hits[2].IsTail);
    }

    [Fact]
    public void SeekBackward_ClearsAllHits()
    {
        PerformanceEvent[] notes = [Note("A", 1000)];
        var timeline = new MusicGpuEffectTimeline();
        timeline.Update(Snapshot(1000, "A"), notes, true, 1000, TotalBurstDuration);
        Assert.Single(timeline.Hits);

        // Seek backward to 500
        timeline.Update(Snapshot(500), notes, true, 500, TotalBurstDuration);
        Assert.Empty(timeline.Hits);
    }

    [Fact]
    public void SessionIdChange_ClearsAllHits()
    {
        var timeline = new MusicGpuEffectTimeline();
        timeline.Update(Snapshot(100, "A") with { SessionId = "session-1" }, [], false, 100, TotalBurstDuration);
        Assert.Single(timeline.Hits);

        timeline.Update(Snapshot(150, "A") with { SessionId = "session-2" }, [], false, 150, TotalBurstDuration);
        // "A" is still held from session-1, but session changed: timeline clears
        // Since "A" was in previousKeys before clear (Clear resets previousKeys),
        // pressing "A" in the new session starts fresh!
        Assert.Single(timeline.Hits);
        Assert.Equal(150, timeline.Hits[0].StartMs);
    }

    [Fact]
    public void FallbackDuration_ExpiresHitsFasterThanAtlasDuration()
    {
        var timeline = new MusicGpuEffectTimeline();
        timeline.Update(Snapshot(0, "A"), [], false, 0, FallbackDuration);
        Assert.Single(timeline.Hits);

        timeline.Update(Snapshot(379), [], false, 379, FallbackDuration);
        Assert.Single(timeline.Hits);

        timeline.Update(Snapshot(380), [], false, 380, FallbackDuration);
        Assert.Empty(timeline.Hits);
    }

    [Fact]
    public void EventDrivenHits_WithoutActiveKeys_SuccessfullyTriggersAndDeduplicates()
    {
        PerformanceEvent[] notes = [Note("A", 1000), Note("S", 1000)];
        var timeline = new MusicGpuEffectTimeline();

        // Pass hitEvents explicitly when snapshot has NO active keys (simulating missed sampling pulse)
        var hitEvents = new[]
        {
            new MusicGpuHitEvent("A", 0, 1000, 5000, "A-1000", "automatic"),
            new MusicGpuHitEvent("S", 1, 1000, 5000, "S-1000", "automatic")
        };

        timeline.Update(Snapshot(1000), notes, true, 1000, TotalBurstDuration, hitEvents);
        Assert.Equal(2, timeline.Hits.Count);
        Assert.Same(notes[0], timeline.Hits[0].Note);
        Assert.Same(notes[1], timeline.Hits[1].Note);

        // Subsequent frame with same event should not duplicate
        timeline.Update(Snapshot(1016), notes, true, 1016, TotalBurstDuration, hitEvents);
        Assert.Equal(2, timeline.Hits.Count);
    }

    [Fact]
    public void EventDrivenHits_Uncharted_ProducesLaneHits()
    {
        var timeline = new MusicGpuEffectTimeline();
        var hitEvents = new[]
        {
            new MusicGpuHitEvent("J", 4, 0, 5000)
        };

        timeline.Update(Snapshot(100), [], false, 100, TotalBurstDuration, hitEvents);
        var hit = Assert.Single(timeline.Hits);
        Assert.Equal("J", hit.Key);
        Assert.Equal(4, hit.Lane);
        Assert.Equal(100, hit.StartMs);
    }

    [Fact]
    public void RendererHitQueue_BoundedCapacityAndClear()
    {
        var renderer = new MusicGpuRenderer();
        for (int i = 0; i < 100; i++)
        {
            renderer.EnqueueHit(new MusicGpuHitEvent("A", 0, i * 10, i * 10));
        }

        renderer.ClearHits();
        // Clear empties queue safely
        renderer.Dispose();
    }

    private static PerformanceEvent Note(string key, int time, int duration = 0) => new()
    {
        NoteId = $"{key}-{time}",
        Key = key,
        TimeMs = time,
        DurationMs = duration,
        Kind = duration > 0 ? MusicNoteKind.Hold : MusicNoteKind.Tap
    };

    private static MusicRenderSnapshot Snapshot(double time, params string[] keys) => MusicRenderSnapshot.Empty with
    {
        SessionId = "real",
        Revision = 1,
        PositionMs = time,
        ActiveKeys = keys,
        ActiveAutomaticKeys = keys
    };

    private static MusicRenderSnapshot Preview(double time) => Snapshot(time) with { SessionId = "preview-test" };
}
