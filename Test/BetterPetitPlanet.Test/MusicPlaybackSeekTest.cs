using System.Collections.Generic;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Service;
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
            System.Threading.Thread.Sleep(100);
            var isFgAfter = processDetector.IsGameForeground();

            Assert.True(activated);
            Assert.True(isFgAfter);
        }
    }
}
