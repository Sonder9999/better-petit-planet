using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Service;
using Xunit;

namespace BetterPetitPlanet.Test;

public sealed class MusicTimelineSeekControlTest
{
    private sealed class TestConfig : IConfigService
    {
        public AppConfig Config { get; } = new();
        public void Save() { }
        public void Reload() { }
    }

    private sealed class Input : IMusicInputTransport
    {
        public List<MusicKeyCommand> Commands { get; } = [];

        public IReadOnlyList<MusicInputResult> Submit(IReadOnlyList<MusicKeyCommand> commands, IntPtr target, MusicInputMode mode)
        {
            Commands.AddRange(commands);
            return commands.Select(c => new MusicInputResult(c, true)).ToArray();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlayingSeekReleasesAndWaitsForActivationAndReadiness(bool automatic)
    {
        var config = new TestConfig();
        config.Config.Music.AutoFocusGameOnPlayback = false;
        var input = new Input();
        double now = 0;
        using var playback = new MusicPlaybackService(config, new GameProcessDetector(config), new KeyInputTransports(),
            clock: new MusicSessionClock(() => now), inputTransport: input);
        playback.ConfigurePreparation(0);
        playback.LoadTimeline(Chart());
        playback.SetAutomaticPlayback(automatic);
        playback.Play();

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int activations = 0;
        var seek = MusicTimelineSeekControl.SeekAsync(playback, 1, async () =>
        {
            activations++;
            await ready.Task;
            playback.SetReadiness(true, "竖笛已就绪");
            playback.Play();
        });

        Assert.Equal(1, activations);
        Assert.Equal(PlayerState.Paused, playback.State);
        Assert.Empty(playback.CaptureSnapshot().ActiveKeys);
        if (automatic) Assert.Contains(input.Commands, c => !c.IsDown && c.Note.Key == "A");

        now = 1000;
        Assert.Equal(3000, playback.CurrentPositionMs);
        Assert.False(seek.IsCompleted);
        Assert.DoesNotContain(input.Commands, c => c.IsDown && c.Note.Key == "S");

        ready.SetResult();
        await seek;

        Assert.Equal(PlayerState.Playing, playback.State);
        Assert.Equal(3000, playback.CurrentPositionMs);
    }

    [Fact]
    public async Task FailedReadinessLeavesPlayingSeekPausedAtRequestedNote()
    {
        var config = new TestConfig();
        config.Config.Music.AutoFocusGameOnPlayback = false;
        var input = new Input();
        using var playback = new MusicPlaybackService(config, new GameProcessDetector(config), new KeyInputTransports(),
            clock: new MusicSessionClock(() => 0), inputTransport: input);
        playback.ConfigurePreparation(0);
        playback.LoadTimeline(Chart());
        playback.Play();

        int checks = 0;
        await MusicTimelineSeekControl.SeekAsync(playback, 1, () =>
        {
            checks++;
            playback.SetReadiness(false, "游戏失焦");
            return Task.CompletedTask;
        });

        Assert.Equal(1, checks);
        Assert.Equal(PlayerState.Paused, playback.State);
        Assert.Equal(3000, playback.CurrentPositionMs);
        Assert.Empty(playback.CaptureSnapshot().ActiveKeys);
        Assert.DoesNotContain(input.Commands, c => c.IsDown && c.Note.Key == "S");
    }

    private static PerformanceTimeline Chart() => new()
    {
        Events = [
            new() { Key = "A", TimeMs = 0, DurationMs = 2500, Kind = MusicNoteKind.Hold },
            new() { Key = "S", TimeMs = 3000, DurationMs = 1000, Kind = MusicNoteKind.Hold }
        ],
        TotalDurationMs = 4000
    };
}
