using System;
using System.Linq;
using System.Reflection;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Service;
using Xunit;

namespace BetterPetitPlanet.Test;

public class MusicPreviewIsolationTest
{
    [Fact]
    public void DisposedSource_CannotRestartOrResetItsClock()
    {
        double now = 0;
        using var preview = new MusicPreviewSource(() => now);
        preview.Start();
        now = 1500;
        preview.Dispose();
        var stopped = preview.CaptureSnapshot();
        now += 5000;
        preview.Start();
        preview.Reset();
        var after = preview.CaptureSnapshot();
        Assert.False(after.IsPlaying);
        Assert.Equal(stopped.PositionMs, after.PositionMs);
        Assert.Equal(stopped.Revision, after.Revision);
    }

    [Fact]
    public void SyntheticChart_CoversAllSevenTracks_WithTapsHoldsAndChords()
    {
        using var preview = new MusicPreviewSource();

        Assert.NotNull(preview.Timeline);
        Assert.NotNull(preview.ChartIndex);
        Assert.Equal(7, preview.Timeline.Events.Count);

        // 1. All 7 lanes must be covered
        var distinctLanes = preview.Timeline.Events.Select(e => e.Key).Distinct().OrderBy(k => MusicPlayfieldGeometry.GetLaneIndex(k)).ToArray();
        string[] expectedLanes = ["A", "S", "D", "F", "J", "K", "L"];
        Assert.Equal(expectedLanes, distinctLanes);

        // 2. Must contain long hold notes
        var holds = preview.Timeline.Events.Where(e => MusicChartIndex.GetNoteDuration(e) >= 1000).ToList();
        Assert.True(holds.Count >= 2, "Must contain at least 2 long hold notes");

        // 3. Must contain chords
        var chordGroups = MusicChartIndex.GroupChords(preview.Timeline.Events).ToList();
        Assert.NotEmpty(chordGroups);
    }

    [Fact]
    public void LoopClock_LoopsSeamlesslyAcrossDuration()
    {
        double fakeNow = 10000.0;
        using var preview = new MusicPreviewSource(monotonicClock: () => fakeNow, loopDurationMs: 8000.0);

        Assert.False(preview.IsPlaying);
        Assert.Equal(0.0, preview.GetCurrentPositionMs());

        // 1. Start playback
        preview.Start();
        Assert.True(preview.IsPlaying);

        // 2. Advance clock by 2500ms
        fakeNow += 2500.0;
        Assert.Equal(2500.0, preview.GetCurrentPositionMs(), precision: 2);

        // 3. Advance clock past the 8000ms loop boundary (8500ms total elapsed)
        fakeNow += 6000.0; // 8500ms total elapsed
        Assert.Equal(500.0, preview.GetCurrentPositionMs(), precision: 2); // 8500 % 8000 = 500

        // 4. Pause locks position
        preview.Pause();
        Assert.False(preview.IsPlaying);
        fakeNow += 1000.0;
        Assert.Equal(500.0, preview.GetCurrentPositionMs(), precision: 2);

        // 5. Reset zeroes position
        preview.Reset();
        Assert.Equal(0.0, preview.GetCurrentPositionMs(), precision: 2);
    }

    [Fact]
    public void StrictPhysicalIsolation_NoInputTransportOrWindowDependencies()
    {
        var previewType = typeof(MusicPreviewSource);

        // 1. Must NOT implement any transport interface
        Assert.False(typeof(IMusicInputTransport).IsAssignableFrom(previewType));

        // 2. Verify all constructors
        var constructors = previewType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (var ctor in constructors)
        {
            var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();
            Assert.DoesNotContain(typeof(IMusicInputTransport), paramTypes);
            Assert.DoesNotContain(typeof(KeyInputTransports), paramTypes);
            Assert.DoesNotContain(typeof(MusicSessionCoordinator), paramTypes);
            Assert.DoesNotContain(typeof(MusicPlaybackService), paramTypes);
            Assert.DoesNotContain(typeof(IntPtr), paramTypes);
        }

        // 3. Verify all fields
        var fields = previewType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (var field in fields)
        {
            var fieldType = field.FieldType;
            Assert.False(typeof(IMusicInputTransport).IsAssignableFrom(fieldType),
                $"Field {field.Name} must not reference IMusicInputTransport");
            Assert.False(fieldType == typeof(KeyInputTransports),
                $"Field {field.Name} must not reference KeyInputTransports");
            Assert.False(fieldType == typeof(MusicSessionCoordinator),
                $"Field {field.Name} must not reference MusicSessionCoordinator");
            Assert.False(fieldType == typeof(MusicPlaybackService),
                $"Field {field.Name} must not reference MusicPlaybackService");
            Assert.False(fieldType == typeof(IntPtr),
                $"Field {field.Name} must not reference IntPtr window handle");
        }
    }

    [Fact]
    public void SimulatedActiveFeedback_TriggersOnlyInSnapshot_ZeroSideEffects()
    {
        double fakeNow = 50000.0;
        using var preview = new MusicPreviewSource(monotonicClock: () => fakeNow, loopDurationMs: 8000.0);

        preview.Start();

        // demo-6 starts at 2500ms on lane K with duration 1500ms (active during [2500..4000])
        fakeNow += 3000.0; // position = 3000ms

        var snapshot = preview.CaptureSnapshot();

        Assert.StartsWith("preview-", snapshot.SessionId);
        Assert.True(snapshot.IsPlaying);
        Assert.False(snapshot.IsPreparing);
        Assert.Equal(3000.0, snapshot.PositionMs, precision: 2);

        // During 3000ms, lanes A (holding), S (tap at 3000ms), and D (holding) are active
        Assert.Contains("A", snapshot.ActiveKeys);
        Assert.Contains("S", snapshot.ActiveKeys);
        Assert.Contains("D", snapshot.ActiveKeys);

        // Manual keys are strictly empty in preview
        Assert.Empty(snapshot.ActiveManualKeys);
    }
}
