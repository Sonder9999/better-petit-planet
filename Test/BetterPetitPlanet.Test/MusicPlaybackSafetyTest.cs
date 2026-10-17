using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask.AutoPick;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Service;
using Xunit;

namespace BetterPetitPlanet.Test;

public sealed class MusicPlaybackSafetyTest
{
    private sealed class TestConfig : IConfigService
    {
        public AppConfig Config { get; } = new();
        public void Save() { }
        public void Reload() { }
    }

    private sealed class Input : IMusicInputTransport
    {
        public TaskCompletionSource<MusicInputMode> Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondDown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<MusicKeyCommand> Commands { get; } = [];
        private int _downs;

        public IReadOnlyList<MusicInputResult> Submit(IReadOnlyList<MusicKeyCommand> commands, IntPtr target, MusicInputMode mode)
        {
            lock (Commands)
            {
                Commands.AddRange(commands);
            }

            foreach (var command in commands)
            {
                if (!command.IsDown) Released.TrySetResult(mode);
                else if (Interlocked.Increment(ref _downs) == 2) SecondDown.TrySetResult();
            }
            return commands.Select(c => new MusicInputResult(c, true)).ToArray();
        }
    }

    [Fact]
    public async Task SwitchingToMouseDuringHold_PausesAndReleasesOriginalKeyboardRoute()
    {
        var config = new TestConfig();
        config.Config.Music.AutoFocusGameOnPlayback = false;
        var input = new Input();
        using var service = new MusicPlaybackService(config, new GameProcessDetector(config), new KeyInputTransports(),
            clock: new MusicSessionClock(() => 0), inputTransport: input);
        service.ConfigurePreparation(0);
        service.LoadTimeline(new() { Events = [new() { Key = "A", Kind = MusicNoteKind.Hold, DurationMs = 2500 }] });
        service.Play();
        config.Config.Music.InputMode = MusicInputMode.Mouse;
        var releaseMode = await input.Released.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MusicInputMode.Keyboard, releaseMode);
        Assert.Equal(PlayerState.Paused, service.State);
        Assert.Contains("键盘", service.LastInputError);
    }

    [Fact]
    public async Task SlowPositionObserver_SeekDoesNotBlockSchedulingOrReadinessRelease()
    {
        using var blocker = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = new TestConfig();
        config.Config.Music.AutoFocusGameOnPlayback = false;
        var input = new Input();
        using var service = new MusicPlaybackService(config, new GameProcessDetector(config), new KeyInputTransports(),
            clock: new MusicSessionClock(() => 0), inputTransport: input);
        service.ConfigurePreparation(0);
        service.LoadTimeline(new() { Events = [new() { Key = "A", Kind = MusicNoteKind.Hold, DurationMs = 2500 }] });
        service.Play();
        service.PositionChanged += (_, _) => { entered.TrySetResult(); blocker.Wait(); };
        var seek = Task.Run(() => service.Seek(0));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await seek.WaitAsync(TimeSpan.FromSeconds(1));
            await input.SecondDown.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Run(() => service.SetReadiness(false, "focus lost")).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(service.CaptureSnapshot().ActiveKeys);
            Assert.Equal(PlayerState.Paused, service.State);
        }
        finally { blocker.Set(); await seek; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadinessStopsAutomaticAndManualHoldsRegardlessOfRendering(bool hasOverlay)
    {
        var config = new TestConfig();
        config.Config.Music.AutoFocusGameOnPlayback = false;
        var input = new Input();
        using var playback = new MusicPlaybackService(config, new GameProcessDetector(config), new KeyInputTransports(),
            clock: new MusicSessionClock(() => 0), inputTransport: input);
        playback.ConfigurePreparation(0);
        playback.LoadTimeline(new()
        {
            Events = [new() { Key = "A", TimeMs = 0, DurationMs = 2500, Kind = MusicNoteKind.Hold }],
            TotalDurationMs = 2500
        });

        var keys = new MusicPhysicalKeys();
        bool unsafeCalled = false;
        var safety = new MusicReadinessCoordinator(playback.SetReadiness, () => keys.Clear(), hasOverlay ? () => unsafeCalled = true : null);
        var gate = new MusicReadinessGate(new MusicReadinessOptions());
        gate.ObserveCapture(0);
        gate.ObserveDetection(true, 0);
        gate.ObserveDetection(true, 100);

        safety.Apply(gate.Evaluate(100, exists: true, foreground: true, captureRunning: true));
        playback.Play();
        keys.Apply("L", true, false);

        Assert.Contains(input.Commands, c => c.IsDown && c.Note.Key == "A");

        // Lost focus / minimized
        safety.Apply(gate.Evaluate(200, exists: true, foreground: false, captureRunning: true));
        Assert.Equal(PlayerState.Paused, playback.State);
        Assert.Empty(playback.CaptureSnapshot().ActiveKeys);
        Assert.Empty(keys.ActiveKeys);
        if (hasOverlay) Assert.True(unsafeCalled);
        Assert.Contains(input.Commands, c => !c.IsDown && c.Note.Key == "A");

        // Recovers readiness
        gate.ObserveCapture(300);
        gate.ObserveDetection(true, 300);
        gate.ObserveDetection(true, 400);
        safety.Apply(gate.Evaluate(400, exists: true, foreground: true, captureRunning: true));
        Assert.True(safety.Ready);
        Assert.Equal(PlayerState.Paused, playback.State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SafetyProtectionReleasesHeldKeysWhenGameLosesFocus_EvenIfOverlayDisabledOrNoChart(bool hasChart)
    {
        var config = new TestConfig();
        config.Config.Music.AutoFocusGameOnPlayback = false;
        var input = new Input();
        using var playback = new MusicPlaybackService(config, new GameProcessDetector(config), new KeyInputTransports(),
            clock: new MusicSessionClock(() => 0), inputTransport: input);
        playback.ConfigurePreparation(0);
        if (hasChart)
        {
            playback.LoadTimeline(new()
            {
                Events = [new() { Key = "A", TimeMs = 0, DurationMs = 2500, Kind = MusicNoteKind.Hold }],
                TotalDurationMs = 2500
            });
        }

        var keys = new MusicPhysicalKeys();
        bool unsafeInvoked = false;
        var safety = new MusicReadinessCoordinator(playback.SetReadiness, () => keys.Clear(), () => unsafeInvoked = true);
        var gate = new MusicReadinessGate(new MusicReadinessOptions());
        gate.ObserveCapture(0);
        gate.ObserveDetection(true, 0);
        gate.ObserveDetection(true, 100);

        safety.Apply(gate.Evaluate(100, exists: true, foreground: true, captureRunning: true));
        if (hasChart)
        {
            playback.Play();
            Assert.Contains(input.Commands, c => c.IsDown && c.Note.Key == "A");
        }
        keys.Apply("L", true, false);
        Assert.Contains("L", keys.ActiveKeys);

        // Game loses focus
        var unready = gate.Evaluate(200, exists: true, foreground: false, captureRunning: true);
        Assert.False(unready.Ready);
        Assert.Equal("游戏失焦或最小化", unready.Reason);

        safety.Apply(unready);

        Assert.False(safety.Ready);
        if (hasChart)
        {
            Assert.Equal(PlayerState.Paused, playback.State);
            Assert.Empty(playback.CaptureSnapshot().ActiveKeys);
            Assert.Contains(input.Commands, c => !c.IsDown && c.Note.Key == "A");
        }
        Assert.Empty(keys.ActiveKeys);
        Assert.True(unsafeInvoked);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SafetyProtectionReleasesHeldKeysWhenScreenshotExpires_EvenIfOverlayDisabled(bool hasChart)
    {
        var config = new TestConfig();
        config.Config.Music.AutoFocusGameOnPlayback = false;
        var input = new Input();
        using var playback = new MusicPlaybackService(config, new GameProcessDetector(config), new KeyInputTransports(),
            clock: new MusicSessionClock(() => 0), inputTransport: input);
        playback.ConfigurePreparation(0);
        if (hasChart)
        {
            playback.LoadTimeline(new()
            {
                Events = [new() { Key = "D", TimeMs = 0, DurationMs = 2500, Kind = MusicNoteKind.Hold }],
                TotalDurationMs = 2500
            });
        }

        var keys = new MusicPhysicalKeys();
        var safety = new MusicReadinessCoordinator(playback.SetReadiness, () => keys.Clear());
        var options = new MusicReadinessOptions { ScreenshotMaxAge = TimeSpan.FromMilliseconds(2000) };
        var gate = new MusicReadinessGate(options);
        gate.ObserveCapture(0);
        gate.ObserveDetection(true, 0);
        gate.ObserveDetection(true, 100);

        safety.Apply(gate.Evaluate(100, exists: true, foreground: true, captureRunning: true));
        if (hasChart)
        {
            playback.Play();
            Assert.Contains(input.Commands, c => c.IsDown && c.Note.Key == "D");
        }
        keys.Apply("J", true, false);

        // Screenshot expired (now = 2500, capture = 0 > 2000ms)
        var unready = gate.Evaluate(2500, exists: true, foreground: true, captureRunning: true);
        Assert.False(unready.Ready);
        Assert.Equal("截图已过期", unready.Reason);

        safety.Apply(unready);

        Assert.False(safety.Ready);
        if (hasChart)
        {
            Assert.Equal(PlayerState.Paused, playback.State);
            Assert.Empty(playback.CaptureSnapshot().ActiveKeys);
            Assert.Contains(input.Commands, c => !c.IsDown && c.Note.Key == "D");
        }
        Assert.Empty(keys.ActiveKeys);
    }

    [Fact]
    public void RuntimePickupSuppressionPreservesPersistedEnabledSetting()
    {
        var config = new TestConfig();
        config.Config.AutoPick.Enabled = true;
        var pickup = new AutoPickTrigger(config, null!, null!);
        pickup.MusicSuppressed = true;
        pickup.OnCapture(null!);
        Assert.True(pickup.IsEnabled);
        Assert.True(config.Config.AutoPick.Enabled);
        pickup.MusicSuppressed = false;
        Assert.True(pickup.IsEnabled);
        Assert.True(config.Config.AutoPick.Enabled);
    }
}
