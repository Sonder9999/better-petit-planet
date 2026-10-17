using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Rendering.Gpu;
using Xunit;

namespace BetterPetitPlanet.Test;

public class MusicGpuGeometryTest
{
    private static MusicRenderSnapshot Playing(double position = 1000, double host = 5000, double speed = 1)
        => MusicRenderSnapshot.Empty with { PositionMs = position, HostMonotonicMs = host, PlaybackSpeed = speed, IsPlaying = true };

    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(144)]
    public void CadenceDoesNotAccumulateMotion(int refreshRate)
    {
        var builder = new MusicGpuFrameBuilder();
        var config = new MusicOverlayConfig { VisualOffsetMs = 120 };
        var note = new PerformanceEvent { Key = "F", TimeMs = 3000 };
        var chart = new MusicChartIndex(new[] { note });
        for (int frame = 0; frame <= refreshRate; frame++)
            builder.Build(Playing(speed: 1.5), chart, config, 5000 + frame * 1000.0 / refreshRate, 1920, 1080, 0);

        Assert.Equal(2620, builder.DisplayTimeMs, 6);
        Assert.Single(builder.Notes);
        var expected = MusicPlayfieldGeometry.ComputeNoteBounds(note, 2620, config.FallDurationMs, 1920, 1080, config.Layout);
        Assert.Equal(expected, builder.Notes[0].Head);
    }

    [Fact]
    public void PauseAndResumeUsePublishedClockAnchor()
    {
        var builder = new MusicGpuFrameBuilder();
        var chart = new MusicChartIndex(new[] { new PerformanceEvent { Key = "A", TimeMs = 4000 } });
        var config = new MusicOverlayConfig();
        var paused = Playing() with { IsPlaying = false, PositionMs = 1600 };
        builder.Build(paused, chart, config, 9000, 1920, 1080, 0);
        var before = builder.Notes[0];
        builder.Build(paused, chart, config, 15000, 1920, 1080, 0);
        Assert.Equal(before, builder.Notes[0]);
        builder.Build(paused with { IsPlaying = true, HostMonotonicMs = 15000 }, chart, config, 15100, 1920, 1080, 0);
        Assert.Equal(1700, builder.DisplayTimeMs);
    }

    [Fact]
    public void PreparingCountdownUsesTheSameClockDomain()
    {
        var builder = new MusicGpuFrameBuilder();
        var snapshot = Playing(-1000) with { IsPlaying = false, IsPreparing = true };
        builder.Build(snapshot, MusicChartIndex.Empty, new(), 5500, 1920, 1080, 0);
        Assert.Equal(-500, builder.DisplayTimeMs);
    }

    [Fact]
    public void SevenLanesAndResizeReuseExistingDipGeometry()
    {
        var builder = new MusicGpuFrameBuilder();
        var config = new MusicOverlayConfig { Layout = new() { X = .1, Y = .1, Width = .8, Height = .7, Scale = 1.2 } };
        var notes = MusicPlayfieldGeometry.LaneKeys.Select(key => new PerformanceEvent { Key = key, TimeMs = 2000 }).ToArray();
        var chart = new MusicChartIndex(notes);
        foreach (double width in new[] { 960.0, 1920.0, 2560.0 })
        {
            builder.Build(Playing(), chart, config, 5000, width, 1080, 0);
            Assert.Equal(7, builder.Notes.Count);
            foreach (var note in builder.Notes)
                Assert.Equal(MusicPlayfieldGeometry.GetLaneX(note.Lane, width, config.Layout), note.Head.X + note.Head.Width / 2, 6);
        }
    }

    [Fact]
    public void LongHoldRemainsVisibleAndHeldHeadStopsAtJudge()
    {
        var builder = new MusicGpuFrameBuilder();
        var config = new MusicOverlayConfig();
        var chart = new MusicChartIndex(new[] { new PerformanceEvent { Key = "A", TimeMs = 1000, DurationMs = 20000, Kind = MusicNoteKind.Hold } });
        builder.Build(Playing(10000), chart, config, 5000, 1920, 1080, 1);
        var note = Assert.Single(builder.Notes);
        Assert.True(note.IsHolding);
        Assert.Equal(MusicPlayfieldGeometry.GetJudgementY(1080, config.Layout), note.Head.Y + note.Head.Height / 2, 6);
        builder.Build(Playing(10000), chart, config, 5000, 1920, 1080, 0);
        Assert.False(builder.Notes[0].IsHolding);
        Assert.Equal(MusicPlayfieldGeometry.GetJudgementY(1080, config.Layout), builder.Notes[0].Head.Y + note.Head.Height / 2, 6);
    }

    [Fact]
    public void ResolvedTapAndHoldAreRemovedAtTheirExactBoundaries()
    {
        var builder = new MusicGpuFrameBuilder();
        var chart = new MusicChartIndex(new[]
        {
            new PerformanceEvent { Key = "A", TimeMs = 1000 },
            new PerformanceEvent { Key = "S", TimeMs = 500, DurationMs = 500, Kind = MusicNoteKind.Hold }
        });
        builder.Build(Playing(999), chart, new(), 5000, 1920, 1080, 0);
        Assert.Equal(2, builder.Notes.Count);
        builder.Build(Playing(), chart, new(), 5000, 1920, 1080, 0);
        Assert.Empty(builder.Notes);
    }

    [Fact]
    public void ChartCopyAndFrameBufferAreStableAcrossFrames()
    {
        var builder = new MusicGpuFrameBuilder();
        var source = new PerformanceEvent { Key = "A", TimeMs = 2000 };
        var chart = new MusicChartIndex(new[] { source });
        builder.Build(Playing(), chart, new(), 5000, 1920, 1080, 0);
        var buffer = builder.Notes;
        source.Key = "L";
        builder.Build(Playing(), chart, new(), 5100, 1920, 1080, 0);
        Assert.Same(buffer, builder.Notes);
        Assert.Equal(0, builder.Notes[0].Lane);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    public void FeedbackScreenshot_HoldCapStopsButTailSurvivesUntilEnd(int activeMask)
    {
        var builder = new MusicGpuFrameBuilder();
        var config = new MusicOverlayConfig { Layout = new() { Height = .5 } };
        var chart = new MusicChartIndex(new[] { new PerformanceEvent { Key = "L", TimeMs = 5900, DurationMs = 1600, Kind = MusicNoteKind.Hold } });
        var frozen = Playing(7200) with { IsPlaying = false };
        builder.Build(frozen, chart, config, 5000, 1920, 1080, activeMask);
        var note = Assert.Single(builder.Notes);
        double judge = MusicPlayfieldGeometry.GetJudgementY(1080, config.Layout);
        Assert.Equal(judge, note.Head.Y + note.Head.Height / 2, 6);
        Assert.True(note.Tail.Y + note.Tail.Height / 2 < judge);
        Assert.True(note.Beam.Height > 0);
        builder.Build(frozen with { PositionMs = 7499 }, chart, config, 9000, 1920, 1080, activeMask);
        Assert.Single(builder.Notes);
        builder.Build(frozen with { PositionMs = 7500 }, chart, config, 9000, 1920, 1080, activeMask);
        Assert.Empty(builder.Notes);
    }
}
