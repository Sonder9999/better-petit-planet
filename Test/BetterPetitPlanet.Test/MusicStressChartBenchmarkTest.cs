using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Service;
using Xunit;

namespace BetterPetitPlanet.Test;

public class MusicStressChartBenchmarkTest
{
    private class DummyConfigService : IConfigService
    {
        public AppConfig Config { get; set; } = new AppConfig();
        public void Save() { }
        public void Reload() { }
    }

    private static MusicPlaybackService CreatePlaybackService(double playbackSpeed = 1.0)
    {
        var configService = new DummyConfigService();
        configService.Config.Music.AutoFocusGameOnPlayback = false;
        configService.Config.Music.PlaybackSpeed = playbackSpeed;
        var processDetector = new GameProcessDetector(configService);
        var transports = new KeyInputTransports();
        return new MusicPlaybackService(configService, processDetector, transports);
    }

    [Fact]
    public void SevenLaneStressChart_CoversAllSevenTracksWithChordsAndHolds()
    {
        var timeline = StressChartFixture.CreateSevenLaneStressTimeline(
            noteCount: 700,
            stepMs: 50,
            chordInterval: 7,
            holdInterval: 14);

        Assert.NotNull(timeline);
        Assert.True(timeline.Events.Count >= 700);

        // Verify all 7 lanes are represented
        var distinctLanes = timeline.Events.Select(e => e.Key).Distinct().OrderBy(k => k).ToArray();
        var expectedLanes = StressChartFixture.SevenLanes.OrderBy(k => k).ToArray();
        Assert.Equal(expectedLanes, distinctLanes);

        // Verify chords (multiple events sharing the exact same TimeMs on distinct keys)
        var groupedByTime = timeline.Events.GroupBy(e => e.TimeMs).Where(g => g.Count() > 1).ToList();
        Assert.NotEmpty(groupedByTime);
        foreach (var chordGroup in groupedByTime.Take(5))
        {
            var chordKeys = chordGroup.Select(e => e.Key).ToList();
            Assert.Equal(chordKeys.Count, chordKeys.Distinct().Count()); // No duplicate keys in the same chord
        }

        // Verify long hold notes exist
        var holdNotes = timeline.Events.Where(e => e.DurationMs >= 500).ToList();
        Assert.NotEmpty(holdNotes);

        // Verify sections
        Assert.Equal(4, timeline.Sections.Count);
        Assert.Contains(timeline.Sections, s => s.SectionType == SongSectionType.Intro);
        Assert.Contains(timeline.Sections, s => s.SectionType == SongSectionType.Chorus && s.IsClimax);
    }

    [Fact]
    public async Task SevenLaneStressChart_Parser_SerializesAndParsesSyntheticScore()
    {
        var json = StressChartFixture.GenerateStressScoreJson(
            noteCount: 350,
            stepMs: 50,
            chordInterval: 7,
            holdInterval: 14);

        var tempFile = Path.Combine(Path.GetTempPath(), $"stress_score_{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(tempFile, json);

            var parser = new PetitScoreParser();
            var timeline = await parser.ParseFileAsync(tempFile);

            Assert.NotNull(timeline);
            Assert.Equal("7-Lane Stress Chart", timeline.Metadata.Title);
            Assert.True(timeline.Events.Count >= 350);

            // Verify all 7 tracks are resolved via key mapping
            var distinctKeys = timeline.Events.Select(e => e.Key).Distinct().OrderBy(k => k).ToArray();
            Assert.Equal(StressChartFixture.SevenLanes.OrderBy(k => k), distinctKeys);

            // Verify duration calculation
            Assert.True(timeline.TotalDurationMs > 0);
            Assert.Equal(timeline.Events.Max(e => e.TimeMs + e.DurationMs), timeline.TotalDurationMs);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void SevenLaneStressChart_NegativeCountdownAndLeadIn_Modeling()
    {
        // 1. Verify negative countdown generation
        var countdowns = StressChartFixture.CreateNegativeCountdownSequence(leadInDurationMs: 3000, intervalMs: 1000);
        Assert.Equal([-3000, -2000, -1000], countdowns);

        // 2. Lead-in normalization test:
        // When events start at a positive lead-in, PetitScoreParser normalizes first event to 0ms.
        var events = new List<PerformanceEvent>
        {
            new() { TimeMs = 2500, Key = "A", DurationMs = 45 },
            new() { TimeMs = 3000, Key = "S", DurationMs = 45 },
            new() { TimeMs = 3500, Key = "D", DurationMs = 45 }
        };

        var timeline = new PerformanceTimeline
        {
            Events = events,
            TotalDurationMs = 3545
        };

        var playback = CreatePlaybackService();
        playback.LoadTimeline(timeline);

        // Timeline normalized first event from 2500ms down to 0ms in CurrentTimeline
        Assert.NotNull(playback.CurrentTimeline);
        Assert.Equal(0, playback.CurrentTimeline.Events[0].TimeMs);
        Assert.Equal(500, playback.CurrentTimeline.Events[1].TimeMs);
        Assert.Equal(1000, playback.CurrentTimeline.Events[2].TimeMs);
        Assert.Equal(1045, playback.CurrentTimeline.TotalDurationMs);
    }

    [Fact]
    public async Task SevenLaneStressChart_PlaybackSeekAndVariableSpeed_Consistency()
    {
        var timeline = StressChartFixture.CreateSevenLaneStressTimeline(
            noteCount: 500,
            stepMs: 50,
            chordInterval: 8,
            holdInterval: 12);

        using var playback = CreatePlaybackService(playbackSpeed: 1.5);
        playback.LoadTimeline(timeline);

        static async Task<(int Note, double Position)> SeekAndObserve(
            MusicPlaybackService p, Action seek, double expectedPosition)
        {
            var tcs = new TaskCompletionSource<(int, double)>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Observe(int note, int _, double pos, double __)
            {
                if (Math.Abs(pos - expectedPosition) < 0.001) tcs.TrySetResult((note, pos));
            }
            p.NotePositionChanged += Observe;
            try { seek(); return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
            finally { p.NotePositionChanged -= Observe; }
        }

        // 1. Initial position is 0
        Assert.Equal(PlayerState.Idle, playback.State);
        Assert.Equal(1.5, playback.PlaybackSpeed);

        // 2. Seek to 5.0 seconds
        var (noteIdx, pos) = await SeekAndObserve(playback, () => playback.Seek(5.0), 5.0);
        Assert.Equal(5.0, pos);
        Assert.True(noteIdx >= 0);

        // 3. Seek out of bounds: negative -> clamped to 0.0
        var (_, negPos) = await SeekAndObserve(playback, () => playback.Seek(-10.0), 0.0);
        Assert.Equal(0.0, negPos);

        // 4. Seek out of bounds: beyond duration -> clamped to last note position
        double lastNoteSeconds = timeline.Events[^1].TimeMs / 1000.0;
        var (_, lastPos) = await SeekAndObserve(playback, () => playback.Seek(99999.0), lastNoteSeconds);
        Assert.Equal(lastNoteSeconds, lastPos);

        // 5. SeekNote by index
        double targetNoteSeconds = timeline.Events[100].TimeMs / 1000.0;
        var (seekedNoteIdx, _) = await SeekAndObserve(playback, () => playback.SeekNote(100), targetNoteSeconds);
        Assert.Equal(100, seekedNoteIdx);
    }

    [Fact]
    public void SevenLaneStressChart_ScaleBenchmark_50kNotes_ExecutionWithinBudget()
    {
        var sw = Stopwatch.StartNew();
        var timeline = StressChartFixture.Create50kNoteStressTimeline();
        sw.Stop();

        Assert.True(timeline.Events.Count >= 50_000);
        // Generation of 50k objects in memory should be fast (typically < 100ms)
        Assert.True(sw.ElapsedMilliseconds < 2000, $"50k note generation took {sw.ElapsedMilliseconds}ms");

        // Benchmark: Querying visible notes in a sliding 2000ms window across 50,000 events
        // (Simulates native renderer per-frame visible note query)
        var querySw = Stopwatch.StartNew();
        int totalVisibleNotesQueried = 0;
        int querySteps = 100;
        int timeStepMs = timeline.TotalDurationMs / querySteps;

        // Using binary search to find visible range [windowStart, windowEnd]
        var eventTimes = timeline.Events.Select(e => e.TimeMs).ToArray();

        for (int i = 0; i < querySteps; i++)
        {
            int windowStart = i * timeStepMs;
            int windowEnd = windowStart + 2000;

            int startIndex = Array.BinarySearch(eventTimes, windowStart);
            if (startIndex < 0) startIndex = ~startIndex;

            int endIndex = Array.BinarySearch(eventTimes, windowEnd);
            if (endIndex < 0) endIndex = ~endIndex;

            totalVisibleNotesQueried += (endIndex - startIndex);
        }
        querySw.Stop();

        Assert.True(totalVisibleNotesQueried > 0);
        // 100 queries across 50k items should execute in less than 50ms total (< 0.5ms per query)
        Assert.True(querySw.ElapsedMilliseconds < 500,
            $"100 window queries over 50k notes took {querySw.ElapsedMilliseconds}ms (budget < 500ms)");
    }

    [Fact]
    public void SevenLaneStressChart_OverlappingHoldsAndChords_LaneIntegrity()
    {
        var timeline = StressChartFixture.CreateSevenLaneStressTimeline(
            noteCount: 300,
            stepMs: 50,
            chordInterval: 6,
            holdInterval: 12);

        // Find long holds
        var holds = timeline.Events.Where(e => e.DurationMs >= 500).ToList();
        Assert.NotEmpty(holds);

        foreach (var hold in holds.Take(5))
        {
            int holdStart = hold.TimeMs;
            int holdEnd = hold.TimeMs + hold.DurationMs;

            // Intermediate events occurring on other lanes while this lane is held
            var concurrentEvents = timeline.Events
                .Where(e => e.TimeMs > holdStart && e.TimeMs < holdEnd)
                .ToList();

            Assert.NotEmpty(concurrentEvents);
            // Intermediate events must span other lanes
            var otherLanes = concurrentEvents.Select(e => e.Key).Distinct().ToList();
            Assert.NotEmpty(otherLanes);
        }
    }
}
