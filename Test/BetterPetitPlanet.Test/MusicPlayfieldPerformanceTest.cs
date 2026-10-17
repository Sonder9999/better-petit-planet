using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace BetterPetitPlanet.Test;

public class MusicPlayfieldPerformanceTest
{
    private readonly ITestOutputHelper _output;

    public MusicPlayfieldPerformanceTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void StressChart50k_PerFrameQueryAndLayout_MeetsP95LatencyBudget()
    {
        // 1. Generate 50,000 note stress timeline with dense chords and holds
        var timeline = StressChartFixture.Create50kNoteStressTimeline();
        Assert.True(timeline.Events.Count >= 50_000);

        // 2. Build index
        var buildSw = Stopwatch.StartNew();
        var chartIndex = new MusicChartIndex(timeline);
        buildSw.Stop();
        _output.WriteLine($"Index construction for 50,000 notes took: {buildSw.ElapsedMilliseconds}ms");
        Assert.True(buildSw.ElapsedMilliseconds < 3000, "Index build should be fast");

        // 3. Simulate 200 consecutive frames (simulating ~3.3 seconds of 60 FPS playback)
        const int frameCount = 200;
        const double frameStepMs = 16.666;
        const int fallDurationMs = 3000;
        const double width = 1920.0;
        const double height = 1080.0;
        var layout = new MusicOverlayLayoutConfig();
        var effects = new MusicOverlayEffectsConfig();

        double displayTime = 10000.0; // In the dense middle of the song
        var frameLatenciesMs = new double[frameCount];

        // Warmup (5 frames)
        for (int w = 0; w < 5; w++)
        {
            var visible = chartIndex.GetVisibleNotes(displayTime - 150, displayTime + fallDurationMs);
            foreach (var note in visible)
            {
                if (MusicChartIndex.GetNoteDuration(note) > 0)
                {
                    MusicPlayfieldGeometry.ComputeHoldBounds(note, displayTime, fallDurationMs, width, height, layout, false);
                }
                else
                {
                    MusicPlayfieldGeometry.ComputeNoteBounds(note, displayTime, fallDurationMs, width, height, layout);
                }
            }
            displayTime += frameStepMs;
        }

        // Benchmark measurement run
        int totalNotesProcessed = 0;
        var sw = new Stopwatch();

        for (int f = 0; f < frameCount; f++)
        {
            sw.Restart();

            // A. Interval query for visible notes
            var visible = chartIndex.GetVisibleNotes(displayTime - 150, displayTime + fallDurationMs);
            totalNotesProcessed += visible.Count;

            // B. Geometry transformation for every visible note in the frame
            for (int i = 0; i < visible.Count; i++)
            {
                var note = visible[i];
                if (MusicChartIndex.GetNoteDuration(note) > 0)
                {
                    MusicPlayfieldGeometry.ComputeHoldBounds(note, displayTime, fallDurationMs, width, height, layout, false);
                }
                else
                {
                    MusicPlayfieldGeometry.ComputeNoteBounds(note, displayTime, fallDurationMs, width, height, layout);
                }
            }

            // C. Chord sync lines computation
            var chordGroups = MusicChartIndex.GroupChords(visible);
            foreach (var group in chordGroups)
            {
                MusicPlayfieldGeometry.ComputeChordLine(group, displayTime, fallDurationMs, width, height, layout);
            }

            sw.Stop();
            frameLatenciesMs[f] = sw.Elapsed.TotalMilliseconds;
            displayTime += frameStepMs;
        }

        // 4. Calculate statistical metrics
        Array.Sort(frameLatenciesMs);
        double median = frameLatenciesMs[frameCount / 2];
        int p95Index = (int)(frameCount * 0.95);
        double p95 = frameLatenciesMs[p95Index];
        double max = frameLatenciesMs[^1];
        double avg = frameLatenciesMs.Average();

        _output.WriteLine($"[50k Performance Probe Results over {frameCount} frames]");
        _output.WriteLine($"Total visible notes processed: {totalNotesProcessed} (avg {totalNotesProcessed / frameCount} notes/frame)");
        _output.WriteLine($"Average: {avg:F4} ms");
        _output.WriteLine($"Median:  {median:F4} ms");
        _output.WriteLine($"P95:     {p95:F4} ms");
        _output.WriteLine($"Max:     {max:F4} ms");

        // 5. Assert budget: P95 must be <= 4.0 ms (Specification requirement 10)
        Assert.True(p95 <= 4.0, $"P95 latency {p95:F3}ms exceeded budget of 4.0ms!");
    }
}
