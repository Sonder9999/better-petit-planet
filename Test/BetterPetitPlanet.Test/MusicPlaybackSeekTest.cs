using System.Collections.Generic;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Service;
using BetterPetitPlanet.View.Converters;
using Xunit;

namespace BetterPetitPlanet.Test;

public class MusicPlaybackSeekTest
{
    private sealed class DummyConfigService : IConfigService
    {
        public AppConfig Config { get; } = new();
        public void Load() { }
        public void Save() { }
        public void Reload() { }
    }

    private sealed class Input : IMusicInputTransport
    {
        public IReadOnlyList<MusicInputResult> Submit(IReadOnlyList<MusicKeyCommand> commands, IntPtr target, MusicInputMode mode)
            => commands.Select(command => new MusicInputResult(command, true)).ToArray();
    }

    private static MusicPlaybackService Create()
    {
        var config = new DummyConfigService();
        config.Config.Music.AutoFocusGameOnPlayback = false;
        return new(config, new GameProcessDetector(config), new KeyInputTransports(),
            clock: new MusicSessionClock(() => 0), inputTransport: new Input());
    }

    private static async Task<(int Note, int Total, double Position)> SeekAndObserve(
        MusicPlaybackService playback, Action seek, double expectedPosition)
    {
        var changed = new TaskCompletionSource<(int, int, double)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(int note, int total, double position, double duration)
        {
            if (position == expectedPosition) changed.TrySetResult((note, total, position));
        }
        playback.NotePositionChanged += Observe;
        try { seek(); return await changed.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
        finally { playback.NotePositionChanged -= Observe; }
    }

    [Fact]
    public async Task SeekNote_SetsCorrectNoteIndexAndPosition()
    {
        using var playback = Create();

        var timeline = new PerformanceTimeline
        {
            Metadata = new PetitSongMetadata { Title = "Test Song" },
            TotalDurationMs = 10000,
            Events = new List<PerformanceEvent>
            {
                new() { TimeMs = 1000, Key = "A" },
                new() { TimeMs = 3000, Key = "S" },
                new() { TimeMs = 5000, Key = "D" },
                new() { TimeMs = 8000, Key = "F" },
            }
        };

        playback.LoadTimeline(timeline);
        Thread.Sleep(20);

        var notified = await SeekAndObserve(playback, () => playback.SeekNote(2), 4);
        Assert.Equal(2, notified.Note);
        Assert.Equal(4, notified.Total);
        Assert.Equal(4.0, notified.Position);
    }

    [Fact]
    public async Task Seek_SetsCorrectPositionAndCalculatesEventIndex()
    {
        using var playback = Create();

        var timeline = new PerformanceTimeline
        {
            Metadata = new PetitSongMetadata { Title = "Test Song" },
            TotalDurationMs = 10000,
            Events = new List<PerformanceEvent>
            {
                new() { TimeMs = 1000, Key = "A" },
                new() { TimeMs = 3000, Key = "S" },
                new() { TimeMs = 5000, Key = "D" },
                new() { TimeMs = 8000, Key = "F" },
            }
        };

        playback.LoadTimeline(timeline);

        var notified = await SeekAndObserve(playback, () => playback.Seek(4), 4);
        Assert.Equal(4.0, notified.Position);
        Assert.Equal(2, notified.Note);
    }

    [Fact]
    public async Task Seek_WhilePaused_UpdatesPausedPosition()
    {
        using var playback = Create();

        var timeline = new PerformanceTimeline
        {
            Metadata = new PetitSongMetadata { Title = "Test Song" },
            TotalDurationMs = 10000,
            Events = new List<PerformanceEvent>
            {
                new() { TimeMs = 1000, Key = "A" },
                new() { TimeMs = 3000, Key = "S" },
                new() { TimeMs = 6000, Key = "D" },
            }
        };

        playback.LoadTimeline(timeline);
        playback.Play();
        playback.Pause();

        var notified = await SeekAndObserve(playback, () => playback.Seek(5), 5);
        Assert.Equal(5.0, notified.Position);
        Assert.Equal(PlayerState.Paused, playback.State);
    }

    [Fact]
    public async Task Seek_ClampsOutOfBoundsCorrectly()
    {
        using var playback = Create();

        var timeline = new PerformanceTimeline
        {
            Metadata = new PetitSongMetadata { Title = "Test Song" },
            TotalDurationMs = 10000,
            Events = new List<PerformanceEvent>
            {
                new() { TimeMs = 1000, Key = "A" },
                new() { TimeMs = 5000, Key = "D" },
            }
        };

        playback.LoadTimeline(timeline);

        var before = await SeekAndObserve(playback, () => playback.Seek(-5), 0);
        Assert.Equal(0.0, before.Position);
        var after = await SeekAndObserve(playback, () => playback.Seek(999), 4);
        Assert.Equal(4.0, after.Position);
    }

    [Fact]
    public void ExtractSections_WithExplicitTags_ExtractsCorrectMarkers()
    {
        var events = new List<PerformanceEvent>
        {
            new() { TimeMs = 0, Key = "A", Lyric = "Intro" },
            new() { TimeMs = 2000, Key = "S" },
            new() { TimeMs = 4000, Key = "D", Lyric = "Verse 1" },
            new() { TimeMs = 6000, Key = "F" },
            new() { TimeMs = 8000, Key = "J", Lyric = "Chorus 1" },
            new() { TimeMs = 10000, Key = "K" },
            new() { TimeMs = 12000, Key = "L", Lyric = "Outro" },
        };

        var sections = PetitScoreParser.ExtractSections(events, 13000);

        Assert.Equal(4, sections.Count);
        Assert.Equal(SongSectionType.Intro, sections[0].SectionType);
        Assert.Equal("Intro", sections[0].Name);
        Assert.Equal("Intro", sections[0].ToolTipText);
        Assert.Equal(0, sections[0].NoteIndex);

        Assert.Equal(SongSectionType.Verse, sections[1].SectionType);
        Assert.Equal("Verse 1", sections[1].Name);
        Assert.Equal("Verse 1", sections[1].ToolTipText);
        Assert.Equal(2, sections[1].NoteIndex);

        Assert.Equal(SongSectionType.Chorus, sections[2].SectionType);
        Assert.Equal("Chorus 1", sections[2].Name);
        Assert.Equal("Chorus 1", sections[2].ToolTipText);
        Assert.True(sections[2].IsClimax);
        Assert.Equal(4, sections[2].NoteIndex);

        Assert.Equal(SongSectionType.Outro, sections[3].SectionType);
        Assert.Equal("Outro", sections[3].Name);
        Assert.Equal("Outro", sections[3].ToolTipText);
        Assert.Equal(6, sections[3].NoteIndex);
    }

    [Fact]
    public void ExtractSections_WithoutTags_DetectsClimaxAndIntro()
    {
        // 构造一个没有显式段落标注的曲谱（30 个音符），中间 12~20 个音符密集（高潮段）
        var events = new List<PerformanceEvent>();
        int t = 0;
        for (int i = 0; i < 30; i++)
        {
            events.Add(new PerformanceEvent { TimeMs = t, Key = "A" });
            // 中间高潮部分速度翻倍
            if (i >= 12 && i <= 22)
            {
                t += 100;
            }
            else
            {
                t += 500;
            }
        }

        var sections = PetitScoreParser.ExtractSections(events, t);

        Assert.True(sections.Count >= 2);
        Assert.Equal(SongSectionType.Intro, sections[0].SectionType);
        Assert.Equal(0, sections[0].NoteIndex);

        var climax = sections.Find(s => s.SectionType == SongSectionType.Chorus);
        Assert.NotNull(climax);
        Assert.True(climax.IsClimax);
        Assert.InRange(climax.NoteIndex, 10, 22);
    }

    [Fact]
    public void ProgressToMarginConverter_CalculatesCorrectOffsets()
    {
        var converter = new ProgressToMarginConverter();

        // NormalizedProgress = 0.5, TotalWidth = 1000, MarkerWidth = 8 (default)
        // Expected left = 0.5 * 1000 - 4 = 496
        var result = (System.Windows.Thickness)converter.Convert([0.5, 1000.0], typeof(System.Windows.Thickness), "8", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(496.0, result.Left);

        // NormalizedProgress = 0.0 -> left = 0
        var resultZero = (System.Windows.Thickness)converter.Convert([0.0, 1000.0], typeof(System.Windows.Thickness), "8", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(0.0, resultZero.Left);

        // NormalizedProgress = 1.0 -> left = 1000 - 8 = 992
        var resultEnd = (System.Windows.Thickness)converter.Convert([1.0, 1000.0], typeof(System.Windows.Thickness), "8", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(992.0, resultEnd.Left);
    }
}
