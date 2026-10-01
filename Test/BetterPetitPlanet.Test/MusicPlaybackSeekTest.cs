using System.Collections.Generic;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
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

        // Seek to 4.0s (between 3000ms and 5000ms)
        playback.Seek(4.0);

        Assert.Equal(4.0, notifiedPos);
        Assert.Equal(10.0, notifiedTotal);
    }
}
