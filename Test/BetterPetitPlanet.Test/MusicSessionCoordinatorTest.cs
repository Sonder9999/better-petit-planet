using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask;
using BetterPetitPlanet.GameTask.AutoPick;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Service;
using Xunit;

namespace BetterPetitPlanet.Test;

public sealed class MusicSessionCoordinatorTest : IDisposable
{
    private sealed class TestConfig : IConfigService
    {
        public AppConfig Config { get; } = new();
        public void Save() { }
        public void Reload() { }
        public Task SaveAsync() => Task.CompletedTask;
    }

    private sealed class InputTransport : IMusicInputTransport
    {
        public List<MusicKeyCommand> Commands { get; } = [];

        public IReadOnlyList<MusicInputResult> Submit(IReadOnlyList<MusicKeyCommand> commands, IntPtr target, MusicInputMode mode)
        {
            lock (Commands)
            {
                Commands.AddRange(commands);
            }
            return commands.Select(c => new MusicInputResult(c, true)).ToArray();
        }
    }

    private readonly TestConfig _config = new();
    private readonly InputTransport _input = new();
    private readonly MusicPlaybackService _playback;
    private readonly InstrumentDetector _detector;
    private readonly GameProcessDetector _process;
    private readonly GameTaskManager _capture;
    private readonly AutoPickTrigger _pickup;
    private readonly MusicCaptureObserver _frames;
    private readonly TaskTriggerDispatcher _dispatcher;

    private double _now = 1000;
    private bool _captureRunning = true;
    private bool _gameRunning = true;
    private bool _gameForeground = true;

    public MusicSessionCoordinatorTest()
    {
        _config.Config.Music.AutoFocusGameOnPlayback = false;
        _process = new GameProcessDetector(_config);
        _dispatcher = new TaskTriggerDispatcher();
        _capture = new GameTaskManager(_config, _process, _dispatcher);
        _pickup = new AutoPickTrigger(_config, _process, null!);
        _frames = new MusicCaptureObserver();
        _detector = new InstrumentDetector(_config, null, null);
        _playback = new MusicPlaybackService(_config, _process, new KeyInputTransports(),
            clock: new MusicSessionClock(() => 0), inputTransport: _input);
        _playback.ConfigurePreparation(0);
    }

    public void Dispose()
    {
        _playback.Dispose();
        _capture.Dispose();
        _detector.Dispose();
    }

    private MusicSessionCoordinator CreateCoordinator(MusicReadinessOptions? options = null)
    {
        return new MusicSessionCoordinator(
            _config,
            _playback,
            _detector,
            _process,
            _capture,
            _pickup,
            _frames,
            logger: null,
            options: options ?? new MusicReadinessOptions(),
            inputObserver: null,
            isGameRunning: () => _gameRunning,
            isGameForeground: () => _gameForeground,
            isCaptureRunning: () => _captureRunning,
            clockNow: () => _now);
    }

    [Fact]
    public async Task ToggleAutomaticDuringManualTimeline_EnablesAutomaticWithoutRestartingSong()
    {
        using var coordinator = CreateCoordinator();
        _playback.LoadTimeline(new()
        {
            Events = [new() { Key = "A", TimeMs = 0, DurationMs = 2500, Kind = MusicNoteKind.Hold }],
            TotalDurationMs = 2500
        });

        // Set up readiness
        coordinator.ReadinessGate.ObserveCapture(_now);
        coordinator.ReadinessGate.ObserveDetection(true, _now);
        coordinator.ReadinessGate.ObserveDetection(true, _now);
        coordinator.Tick();
        Assert.True(coordinator.Ready);

        // Start manual playback
        _playback.SetAutomaticPlayback(false);
        _playback.Play();
        Assert.Equal(PlayerState.Playing, _playback.State);
        Assert.False(_playback.AutomaticPlaybackEnabled);

        // Enable automatic playback during playback
        await coordinator.ToggleAutomaticAsync();

        Assert.Equal(PlayerState.Playing, _playback.State);
        Assert.True(_playback.AutomaticPlaybackEnabled);
        Assert.True(_config.Config.Music.AutomaticPlaybackEnabled);

        // Next toggle pauses
        await coordinator.ToggleAutomaticAsync();
        Assert.Equal(PlayerState.Paused, _playback.State);
    }

    [Fact]
    public async Task RapidDoubleToggle_CancelsPendingStartRequest()
    {
        _captureRunning = false;
        using var coordinator = CreateCoordinator();
        _playback.LoadTimeline(new()
        {
            Events = [new() { Key = "A", TimeMs = 0, DurationMs = 2500, Kind = MusicNoteKind.Hold }],
            TotalDurationMs = 2500
        });

        // Readiness is not satisfied initially (captureRunning is false)
        var first = coordinator.ToggleTimelineAsync();
        // Second toggle immediately cancels pending
        var second = coordinator.ToggleTimelineAsync();

        await first;
        await second;

        Assert.NotEqual(PlayerState.Playing, _playback.State);
        Assert.Empty(_input.Commands);
    }

    [Fact]
    public async Task ChangeSongOrStop_CancelsPendingStartRequest()
    {
        _captureRunning = false;
        using var coordinator = CreateCoordinator();
        _playback.LoadTimeline(new()
        {
            Events = [new() { Key = "A", TimeMs = 0, DurationMs = 2500, Kind = MusicNoteKind.Hold }],
            TotalDurationMs = 2500
        });

        var pending = coordinator.StartTimelineAsync();

        // Change song while start is pending
        coordinator.LoadTimeline(new()
        {
            Events = [new() { Key = "S", TimeMs = 0, DurationMs = 1000, Kind = MusicNoteKind.Hold }],
            TotalDurationMs = 1000
        });

        await pending;

        Assert.NotEqual(PlayerState.Playing, _playback.State);
        Assert.Empty(_input.Commands);
    }

    [Fact]
    public async Task SeekNoteAsync_PausesReleasesKeysAndStartsGuardedPlayback()
    {
        using var coordinator = CreateCoordinator();
        _playback.LoadTimeline(new()
        {
            Events = [
                new() { Key = "A", TimeMs = 0, DurationMs = 2500, Kind = MusicNoteKind.Hold },
                new() { Key = "S", TimeMs = 3000, DurationMs = 1000, Kind = MusicNoteKind.Hold }
            ],
            TotalDurationMs = 4000
        });

        // Simulate game readiness
        coordinator.ReadinessGate.ObserveCapture(_now);
        coordinator.ReadinessGate.ObserveDetection(true, _now);
        coordinator.ReadinessGate.ObserveDetection(true, _now);
        coordinator.Tick();
        Assert.True(coordinator.Ready);

        _playback.Play();
        Assert.Equal(PlayerState.Playing, _playback.State);
        Assert.Contains(_input.Commands, c => c.IsDown && c.Note.Key == "A");

        // Seek to note 1
        await coordinator.SeekNoteAsync(1);

        Assert.Equal(3000, _playback.CurrentPositionMs);
        Assert.Equal(PlayerState.Playing, _playback.State);
        // Previous note A was released
        Assert.Contains(_input.Commands, c => !c.IsDown && c.Note.Key == "A");
    }

    [Fact]
    public void GameLossOfFocusOrDetectionExpiry_PausesAndSuppressesKeys()
    {
        using var coordinator = CreateCoordinator();
        _playback.LoadTimeline(new()
        {
            Events = [new() { Key = "A", TimeMs = 0, DurationMs = 2500, Kind = MusicNoteKind.Hold }],
            TotalDurationMs = 2500
        });

        coordinator.ReadinessGate.ObserveCapture(_now);
        coordinator.ReadinessGate.ObserveDetection(true, _now);
        coordinator.ReadinessGate.ObserveDetection(true, _now);
        coordinator.Tick();
        Assert.True(coordinator.Ready);

        _playback.Play();
        Assert.Equal(PlayerState.Playing, _playback.State);

        // Game loses focus
        _gameForeground = false;
        coordinator.Tick();

        Assert.False(coordinator.Ready);
        Assert.Equal(PlayerState.Paused, _playback.State);
        Assert.Contains(_input.Commands, c => !c.IsDown && c.Note.Key == "A");
    }

    [Fact]
    public void PickupSuppression_TracksInstrumentStabilityAndPlaybackState()
    {
        using var coordinator = CreateCoordinator();
        Assert.False(_pickup.MusicSuppressed);

        // Stabilize instrument
        coordinator.ReadinessGate.ObserveCapture(_now);
        coordinator.ReadinessGate.ObserveDetection(true, _now);
        coordinator.ReadinessGate.ObserveDetection(true, _now);
        coordinator.Tick();

        Assert.True(_pickup.MusicSuppressed);

        // Invalidate instrument stability
        coordinator.ReadinessGate.Invalidate();
        coordinator.Tick();

        Assert.False(_pickup.MusicSuppressed);

        // Re-establish readiness so playback can play
        coordinator.ReadinessGate.ObserveCapture(_now);
        coordinator.ReadinessGate.ObserveDetection(true, _now);
        coordinator.ReadinessGate.ObserveDetection(true, _now);
        coordinator.Tick();
        Assert.True(coordinator.Ready);

        // Playing and paused states also suppress pickup
        _playback.LoadTimeline(new() { Events = [new() { Key = "A", TimeMs = 0 }], TotalDurationMs = 100 });
        _playback.Play();
        Assert.Equal(PlayerState.Playing, _playback.State);
        coordinator.Tick();
        Assert.True(_pickup.MusicSuppressed);

        _playback.Pause();
        Assert.Equal(PlayerState.Paused, _playback.State);
        coordinator.Tick();
        Assert.True(_pickup.MusicSuppressed);
    }
}
