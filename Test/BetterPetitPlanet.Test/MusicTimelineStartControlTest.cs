using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Service;
using Xunit;

namespace BetterPetitPlanet.Test;

public sealed class MusicTimelineStartControlTest
{
    private sealed class ConfigService : IConfigService
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

    private sealed class Fixture : IDisposable
    {
        public readonly Input Input = new();
        public readonly MusicPlaybackService Playback;
        public readonly TaskCompletionSource<bool> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly MusicTimelineStartControl Start;
        public int ReadinessChecks;

        public Fixture()
        {
            var config = new ConfigService();
            config.Config.Music.AutoFocusGameOnPlayback = false;
            Playback = new(config, new GameProcessDetector(config), new KeyInputTransports(),
                clock: new MusicSessionClock(() => 0), inputTransport: Input);
            Playback.ConfigurePreparation(0);
            Playback.LoadTimeline(Chart());
            Start = new(Playback, _ =>
            {
                ReadinessChecks++;
                return Ready.Task;
            }, () => { });
        }

        public void Dispose() => Playback.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecondToggleCancelsPendingManualOrAutomaticStart(bool automatic)
    {
        using var f = new Fixture();
        Func<Task> toggle = automatic
            ? () => f.Start.ToggleAutomaticAsync(() => Task.CompletedTask)
            : () => f.Start.ToggleAsync(() => Task.CompletedTask);
        var first = toggle();
        await toggle();
        f.Ready.SetResult(true);
        await first;
        Assert.NotEqual(PlayerState.Playing, f.Playback.State);
        Assert.Empty(f.Input.Commands);
    }

    [Fact]
    public async Task StopWinsOverPendingReadiness()
    {
        using var f = new Fixture();
        var first = f.Start.StartAsync(() => Task.CompletedTask);
        f.Start.CancelPending();
        f.Playback.Stop();
        f.Ready.SetResult(true);
        await first;
        Assert.Equal(PlayerState.Stopped, f.Playback.State);
        Assert.Empty(f.Input.Commands);
    }

    [Fact]
    public async Task SupersededCompletionCannotClearOrPlayNewPendingRequest()
    {
        using var f = new Fixture();
        var first = f.Start.StartAsync(() => Task.CompletedTask);
        var second = f.Start.StartAsync(() => Task.CompletedTask);
        await first;
        Assert.True(f.Start.CancelPending());
        f.Ready.SetResult(true);
        await second;
        Assert.NotEqual(PlayerState.Playing, f.Playback.State);
        Assert.Empty(f.Input.Commands);
    }

    [Fact]
    public async Task AutomaticToggleDuringManualPlaybackDoesNotRestartOrCatchUpHold()
    {
        using var f = new Fixture();
        f.Playback.SetAutomaticPlayback(false);
        f.Playback.Play();
        await f.Start.ToggleAutomaticAsync(() =>
        {
            f.Playback.SetAutomaticPlayback(true);
            return Task.CompletedTask;
        });
        Assert.Equal(PlayerState.Playing, f.Playback.State);
        Assert.True(f.Playback.AutomaticPlaybackEnabled);
        Assert.Equal(0, f.ReadinessChecks);
        Assert.Empty(f.Input.Commands);
    }

    [Fact]
    public async Task LifetimeCancellationStopsReadinessRequest()
    {
        using var f = new Fixture();
        using var lifetime = new CancellationTokenSource();
        var start = new MusicTimelineStartControl(f.Playback, _ => f.Ready.Task, () => { }, lifetime.Token);
        var pending = start.StartAsync(() => Task.CompletedTask);
        lifetime.Cancel();
        await pending;
        f.Ready.SetResult(true);
        Assert.NotEqual(PlayerState.Playing, f.Playback.State);
        Assert.Empty(f.Input.Commands);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("seek")]
    [InlineData("replace")]
    public async Task SessionChangesInvalidateOldStartEvenWithoutExplicitCancellation(string change)
    {
        using var f = new Fixture();
        var first = f.Start.StartAsync(() => Task.CompletedTask);
        if (change == "stop") f.Playback.Stop();
        if (change == "seek") f.Playback.Seek(1);
        if (change == "replace") f.Playback.LoadTimeline(Chart());
        f.Ready.SetResult(true);
        await first;
        Assert.NotEqual(PlayerState.Playing, f.Playback.State);
        Assert.Empty(f.Input.Commands);
    }

    [Fact]
    public async Task CancellationDuringSettingsSavePreventsLaterActivation()
    {
        using var f = new Fixture();
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = f.Start.StartAsync(() => saving.Task);
        f.Start.CancelPending();
        f.Playback.Stop();
        saving.SetResult();
        f.Ready.SetResult(true);
        await first;
        Assert.Equal(0, f.ReadinessChecks);
        Assert.Equal(PlayerState.Stopped, f.Playback.State);
        Assert.Empty(f.Input.Commands);
    }

    private static PerformanceTimeline Chart() => new()
    {
        Events = [new() { Key = "A", TimeMs = 0, DurationMs = 2500, Kind = MusicNoteKind.Hold }],
        TotalDurationMs = 2500
    };
}
