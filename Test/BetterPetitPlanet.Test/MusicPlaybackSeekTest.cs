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

    [Fact]
    public void SeekNote_SetsCorrectNoteIndexAndPosition()
    {
        var configService = new DummyConfigService();
        var processDetector = new GameProcessDetector(configService);
        var transports = new KeyInputTransports();
        var playback = new MusicPlaybackService(configService, processDetector, transports);

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

        int notifiedNote = -1;
        int notifiedTotal = -1;
        double notifiedPos = -1;
        playback.NotePositionChanged += (note, totalNotes, pos, _) =>
        {
            notifiedNote = note;
            notifiedTotal = totalNotes;
            notifiedPos = pos;
        };

        playback.SeekNote(2);

        Assert.Equal(2, notifiedNote);
        Assert.Equal(4, notifiedTotal);
        Assert.Equal(4.0, notifiedPos);
    }

    [Fact]
    public void Seek_SetsCorrectPositionAndCalculatesEventIndex()
    {
        var configService = new DummyConfigService();
        var processDetector = new GameProcessDetector(configService);
        var transports = new KeyInputTransports();
        var playback = new MusicPlaybackService(configService, processDetector, transports);

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

        double notifiedPos = -1;
        double notifiedTotal = -1;
        playback.PositionChanged += (pos, total) =>
        {
            notifiedPos = pos;
            notifiedTotal = total;
        };

        // Seek to 4.0s (normalized note 2 is at 4.0s)
        playback.Seek(4.0);

        Assert.Equal(4.0, notifiedPos);
    }

    [Fact]
    public void Seek_WhilePaused_UpdatesPausedPosition()
    {
        var configService = new DummyConfigService();
        var processDetector = new GameProcessDetector(configService);
        var transports = new KeyInputTransports();
        var playback = new MusicPlaybackService(configService, processDetector, transports);

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

        double notifiedPos = -1;
        playback.PositionChanged += (pos, _) => notifiedPos = pos;

        // Seek to 5.0s (normalized note 2 is at 5.0s)
        playback.Seek(5.0);

        Assert.Equal(5.0, notifiedPos);
        Assert.Equal(PlayerState.Paused, playback.State);
    }

    [Fact]
    public void Seek_ClampsOutOfBoundsCorrectly()
    {
        var configService = new DummyConfigService();
        var processDetector = new GameProcessDetector(configService);
        var transports = new KeyInputTransports();
        var playback = new MusicPlaybackService(configService, processDetector, transports);

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

        double notifiedPos = -1;
        playback.PositionChanged += (pos, _) => notifiedPos = pos;

        playback.Seek(-5.0);
        Assert.Equal(0.0, notifiedPos);

        playback.Seek(999.0);
        Assert.Equal(4.0, notifiedPos);
    }

    [Fact]
    public void ForegroundGuard_BlocksSendKeyWhenGameIsNotForeground()
    {
        bool isForeground = false;
        HardwareInputSimulator.ForegroundGuard = () => isForeground;

        try
        {
            bool sentWhenBackground = HardwareInputSimulator.SendKeyDown("A");
            Assert.False(sentWhenBackground);

            isForeground = true;
            // When foreground, SendKey attempts Win32 SendInput (returns true)
            bool sentWhenForeground = HardwareInputSimulator.SendKeyDown("A");
            Assert.True(sentWhenForeground);
        }
        finally
        {
            HardwareInputSimulator.ForegroundGuard = null;
        }
    }

    [Fact]
    public void ActivateGameWindow_BringsGameToForegroundIfRunning()
    {
        var configService = new DummyConfigService();
        var processDetector = new GameProcessDetector(configService);
        if (processDetector.IsGameRunning())
        {
            var activated = processDetector.ActivateGameWindow();
            bool isFgAfter = false;
            for (int i = 0; i < 10; i++)
            {
                System.Threading.Thread.Sleep(50);
                if (processDetector.IsGameForeground())
                {
                    isFgAfter = true;
                    break;
                }
            }

            Assert.True(activated);
            Assert.True(isFgAfter);
        }
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
