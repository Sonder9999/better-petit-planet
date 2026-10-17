using System.Windows.Media;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.View.Controls.Music;

namespace BetterPetitPlanet.Test;

public sealed class MusicEffectTimelineTest
{
    // Reference hit-animation.ts: 64 atlas frames played at 60fps, with no extra fade.
    private const double BurstDuration = 64 * 1000.0 / 60;

    [Fact]
    public void UnchartedSimultaneousAndRepeatedSameLanePresses_KeepIndependentBirthsAndExpiry()
    {
        var timeline = new MusicEffectTimeline();
        timeline.Update(Snapshot(0, "A", "S"), [], false, 0, BurstDuration);
        Assert.Equal(new[] { ("A", 0d), ("S", 0d) }, timeline.Hits.Select(hit => (hit.Key, hit.StartMs)));
        timeline.Update(Snapshot(100, "S"), [], false, 100, BurstDuration);
        Assert.True(timeline.HasReleasedFeedback);
        timeline.Update(Snapshot(200, "A", "S"), [], false, 200, BurstDuration);
        Assert.Equal(new[] { ("A", 0d), ("S", 0d), ("A", 200d) }, timeline.Hits.Select(hit => (hit.Key, hit.StartMs)));
        timeline.Update(Snapshot(300, "A", "S"), [], false, 300, BurstDuration);
        Assert.Equal(3, timeline.Hits.Count); // A held key cannot restart or duplicate a burst.
        timeline.Update(Snapshot(BurstDuration), [], false, BurstDuration, BurstDuration);
        Assert.Equal(("A", 200d), (Assert.Single(timeline.Hits).Key, timeline.Hits[0].StartMs));
        timeline.Update(Snapshot(1267), [], false, 1267, BurstDuration);
        Assert.Empty(timeline.Hits);
        Assert.False(timeline.HasReleasedFeedback);
    }

    [Fact]
    public void PreviewSeek_ReconstructsRecentHeadAndTailHitsIdenticallyToSequentialPlayback()
    {
        PerformanceEvent[] notes = [Note("A", 1000), Note("S", 1100), Note("A", 1300, 400)];
        var sequential = new MusicEffectTimeline();
        foreach (double time in new[] { 0, 1000, 1100, 1300, 1700, 1750 })
            sequential.Update(Preview(time), notes, true, time, BurstDuration);
        var direct = new MusicEffectTimeline();
        direct.Update(Preview(1750), notes, true, 1750, BurstDuration);
        Assert.Equal(sequential.Hits, direct.Hits);
        Assert.Equal(new[] { (1000d, false), (1100d, false), (1300d, false), (1700d, true) },
            direct.Hits.Select(hit => (hit.StartMs, hit.IsTail)));

        direct.Update(Preview(1200), notes, true, 1200, BurstDuration);
        Assert.Equal(new[] { 1000d, 1100d }, direct.Hits.Select(hit => hit.StartMs));
        direct.Update(Preview(2500), notes, true, 2500, BurstDuration);
        var tail = Assert.Single(direct.Hits);
        Assert.True(tail.IsTail);
        Assert.Equal(1700, tail.StartMs);
        direct.Update(Preview(0), notes, true, 0, BurstDuration);
        Assert.Empty(direct.Hits);
    }

    [Fact]
    public void ChartHits_SimultaneousNotesAndRepeatedLaneNotesRetainTheirOwnIdentity()
    {
        PerformanceEvent[] notes = [Note("A", 1000), Note("S", 1000), Note("A", 1200)];
        var timeline = new MusicEffectTimeline();
        timeline.Update(Snapshot(1000, "A", "S"), notes, true, 1000, BurstDuration);
        timeline.Update(Snapshot(1100), notes, true, 1100, BurstDuration);
        timeline.Update(Snapshot(1200, "A"), notes, true, 1200, BurstDuration);
        Assert.Equal(3, timeline.Hits.Count);
        Assert.Same(notes[0], timeline.Hits[0].Note);
        Assert.Same(notes[1], timeline.Hits[1].Note);
        Assert.Same(notes[2], timeline.Hits[2].Note);
        timeline.Update(Snapshot(2067), notes, true, 2067, BurstDuration);
        Assert.Same(notes[2], Assert.Single(timeline.Hits).Note);
    }

    [Fact]
    public void HeldNote_CreatesSeparateHeadAndCompletionBurstsWithoutRestartWhileHeld()
    {
        var note = Note("A", 1000, 500);
        var timeline = new MusicEffectTimeline();
        timeline.Update(Snapshot(1000, "A"), [note], true, 1000, BurstDuration);
        Assert.False(Assert.Single(timeline.Hits).IsTail);
        timeline.Update(Snapshot(1250, "A"), [note], true, 1250, BurstDuration);
        Assert.Single(timeline.Hits);
        timeline.Update(Snapshot(1500), [note], true, 1500, BurstDuration);
        Assert.Equal(new[] { (1000d, false), (1500d, true) }, timeline.Hits.Select(hit => (hit.StartMs, hit.IsTail)));
        timeline.Update(Snapshot(1600), [note], true, 1600, BurstDuration);
        Assert.Equal(2, timeline.Hits.Count);
        timeline.Update(Snapshot(2067), [note], true, 2067, BurstDuration);
        Assert.True(Assert.Single(timeline.Hits).IsTail);
    }

    [Fact]
    public void RealChartWithoutMatchingInput_DoesNotAutomaticallyHitHeadsOrHoldTails()
    {
        PerformanceEvent[] notes = [Note("A", 1000), Note("S", 1000, 500)];
        var timeline = new MusicEffectTimeline();
        timeline.Update(Snapshot(1000), notes, true, 1000, BurstDuration);
        timeline.Update(Snapshot(1200, "D"), notes, true, 1200, BurstDuration);
        timeline.Update(Snapshot(1500), notes, true, 1500, BurstDuration);
        Assert.Empty(timeline.Hits);
    }

    [Fact]
    public void UnhitHold_LateLanePressCannotProduceSuccessfulCompletionBurst()
    {
        var note = Note("A", 1000, 500);
        var timeline = new MusicEffectTimeline();
        timeline.Update(Snapshot(1000), [note], true, 1000, BurstDuration);
        timeline.Update(Snapshot(1450, "A"), [note], true, 1450, BurstDuration);
        Assert.Empty(timeline.Hits); // The head was missed; this press is outside its hit window.
        timeline.Update(Snapshot(1500), [note], true, 1500, BurstDuration);
        Assert.Empty(timeline.Hits);
    }

    [Fact]
    public void LongSuccessfulHold_StillCompletesAfterItsHeadBurstHasExpired()
    {
        var note = Note("A", 1000, 3000);
        var timeline = new MusicEffectTimeline();
        timeline.Update(Snapshot(1000, "A"), [note], true, 1000, BurstDuration);
        timeline.Update(Snapshot(2500, "A"), [note], true, 2500, BurstDuration);
        Assert.Empty(timeline.Hits);
        timeline.Update(Snapshot(4000), [note], true, 4000, BurstDuration);
        var tail = Assert.Single(timeline.Hits);
        Assert.True(tail.IsTail);
        Assert.Equal(4000, tail.StartMs);
    }

    [Fact]
    public void HoldReleasedEarly_CannotBeRevivedByUnrelatedLatePressOnSameLane()
    {
        var note = Note("A", 1000, 1000);
        var timeline = new MusicEffectTimeline();
        timeline.Update(Snapshot(1000, "A"), [note], true, 1000, BurstDuration);
        timeline.Update(Snapshot(1300), [note], true, 1300, BurstDuration);
        timeline.Update(Snapshot(1900, "A"), [note], true, 1900, BurstDuration);
        timeline.Update(Snapshot(2000), [note], true, 2000, BurstDuration);
        Assert.DoesNotContain(timeline.Hits, hit => hit.IsTail);
    }

    [Fact]
    public void Control_UsesFrozenSongTimeForChartEffectsAndWallTimeForUnchartedEffects() => MusicRenderingTestHost.Run(() =>
    {
        double clock = 0;
        var chart = new MusicPlayfieldControl(() => clock, _ => { }, _ => { });
        chart.EffectsConfig.ShowKeyLabels = false;
        chart.SetChart(new PerformanceTimeline { Events = [Note("A", 1000)] });
        var chartSnapshot = Preview(1050);
        chart.RenderSnapshot(chartSnapshot, 800, 600);
        var initialChartFrame = Assert.Single(Images(MusicPlayfieldLifecycleTest.Layer(chart, 2))).ImageSource;

        var uncharted = new MusicPlayfieldControl(() => clock, _ => { }, _ => { });
        uncharted.EffectsConfig.ShowKeyLabels = false;
        uncharted.RenderSnapshot(Snapshot(1050, "A"), 800, 600);
        var initialUnchartedFrame = Assert.Single(Images(MusicPlayfieldLifecycleTest.Layer(uncharted, 2))).ImageSource;
        clock = 200;
        chart.RenderSnapshot(chartSnapshot, 800, 600);
        uncharted.RenderSnapshot(Snapshot(1050), 800, 600);
        Assert.Same(initialChartFrame, Assert.Single(Images(MusicPlayfieldLifecycleTest.Layer(chart, 2))).ImageSource);
        Assert.NotSame(initialUnchartedFrame, Assert.Single(Images(MusicPlayfieldLifecycleTest.Layer(uncharted, 2))).ImageSource);
        clock = 2000;
        chart.RenderSnapshot(chartSnapshot, 800, 600);
        uncharted.RenderSnapshot(Snapshot(1050), 800, 600);
        Assert.Same(initialChartFrame, Assert.Single(Images(MusicPlayfieldLifecycleTest.Layer(chart, 2))).ImageSource);
        Assert.Empty(Images(MusicPlayfieldLifecycleTest.Layer(uncharted, 2)));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Control_RevisionChangeClearsOldHitsWhilePreviewReconstructsRequestedTime(bool preview) => MusicRenderingTestHost.Run(() =>
    {
        var control = new MusicPlayfieldControl(() => 0, _ => { }, _ => { });
        control.EffectsConfig.ShowKeyLabels = false;
        control.SetChart(new PerformanceTimeline { Events = [Note("A", 1000)] });
        var snapshot = (preview ? Preview(1000) : Snapshot(1000, "A"));
        control.RenderSnapshot(snapshot, 800, 600);
        Assert.Single(Images(MusicPlayfieldLifecycleTest.Layer(control, 2)));
        var revised = snapshot with { Revision = snapshot.Revision + 1, PositionMs = 1100, ActiveKeys = [], ActiveAutomaticKeys = [] };
        control.RenderSnapshot(revised, 800, 600);
        if (preview) Assert.Single(Images(MusicPlayfieldLifecycleTest.Layer(control, 2)));
        else Assert.Empty(Images(MusicPlayfieldLifecycleTest.Layer(control, 2)));
        Assert.Equal(revised, control.CurrentSnapshot);
    });

    private static PerformanceEvent Note(string key, int time, int duration = 0) => new()
    {
        NoteId = $"{key}-{time}", Key = key, TimeMs = time, DurationMs = duration,
        Kind = duration > 0 ? MusicNoteKind.Hold : MusicNoteKind.Tap
    };

    private static MusicRenderSnapshot Snapshot(double time, params string[] keys) => MusicRenderSnapshot.Empty with
    {
        SessionId = "real", Revision = 1, PositionMs = time, ActiveKeys = keys, ActiveAutomaticKeys = keys
    };

    private static MusicRenderSnapshot Preview(double time) => Snapshot(time) with { SessionId = "preview-test" };

    private static IEnumerable<ImageDrawing> Images(Drawing drawing)
    {
        if (drawing is ImageDrawing image) yield return image;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var descendant in Images(child)) yield return descendant;
    }
}
