using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask;
using BetterPetitPlanet.GameTask.AutoPick;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Overlay;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Service;
using BetterPetitPlanet.View.Windows;
using Xunit;

namespace BetterPetitPlanet.Test;

public sealed class MusicOverlayPresenterTest
{
    private static void RunInSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception != null)
        {
            throw new AggregateException("STA thread execution failed", exception);
        }
    }

    private static void RunInSta(Func<Task> asyncAction)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                asyncAction().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception != null)
        {
            throw new AggregateException("STA thread execution failed", exception);
        }
    }

    private sealed class TestConfig : IConfigService
    {
        public AppConfig Config { get; } = new();
        public void Save() { }
        public Task SaveAsync() => Task.CompletedTask;
        public void Reload() { }
    }

    private sealed class TestInput : IMusicInputTransport
    {
        public List<MusicKeyCommand> Commands { get; } = [];
        public IReadOnlyList<MusicInputResult> Submit(IReadOnlyList<MusicKeyCommand> commands, IntPtr target, MusicInputMode mode)
        {
            Commands.AddRange(commands);
            return commands.Select(c => new MusicInputResult(c, true)).ToArray();
        }
    }

    [Fact]
    public void DpiConversion_PhysicalToDipAndDipToPhysical_CalculatesCorrectly()
    {
        // 96 DPI = 100% scale
        var phys96 = new Rect(0, 0, 1920, 1080);
        var dip96 = GameWindowTracker.PhysicalToDip(phys96, 96);
        Assert.Equal(0, dip96.X);
        Assert.Equal(0, dip96.Y);
        Assert.Equal(1920, dip96.Width);
        Assert.Equal(1080, dip96.Height);
        Assert.Equal(phys96, GameWindowTracker.DipToPhysical(dip96, 96));

        // 144 DPI = 150% scale
        var phys144 = new Rect(150, 300, 1920, 1080);
        var dip144 = GameWindowTracker.PhysicalToDip(phys144, 144);
        Assert.Equal(100, dip144.X);
        Assert.Equal(200, dip144.Y);
        Assert.Equal(1280, dip144.Width);
        Assert.Equal(720, dip144.Height);
        Assert.Equal(phys144, GameWindowTracker.DipToPhysical(dip144, 144));

        // 192 DPI = 200% scale
        var phys192 = new Rect(200, 400, 3840, 2160);
        var dip192 = GameWindowTracker.PhysicalToDip(phys192, 192);
        Assert.Equal(100, dip192.X);
        Assert.Equal(200, dip192.Y);
        Assert.Equal(1920, dip192.Width);
        Assert.Equal(1080, dip192.Height);
        Assert.Equal(phys192, GameWindowTracker.DipToPhysical(dip192, 192));

        // State property mapping
        var state = new GameWindowState(new IntPtr(123), true, phys144, 144);
        Assert.True(state.Exists);
        Assert.Equal(phys144, state.PhysicalBounds);
        Assert.Equal(dip144, state.DipBounds);
    }

    [Fact]
    public void RenderFaultIsolation_WindowCreationFailureDoesNotDisruptPlaybackSession()
    {
        var config = new TestConfig();
        config.Config.MusicOverlay.Enabled = true;
        config.Config.Music.AutoFocusGameOnPlayback = false;

        var input = new TestInput();
        using var playback = new MusicPlaybackService(
            config,
            new GameProcessDetector(config),
            new KeyInputTransports(),
            clock: new MusicSessionClock(() => 0),
            inputTransport: input);

        var detector = new InstrumentDetector(config, null, null);
        var process = new GameProcessDetector(config);
        var capture = new GameTaskManager(config, process, new TaskTriggerDispatcher());
        var pickup = new AutoPickTrigger(config, null!, null!);
        var frames = new MusicCaptureObserver();

        using var coordinator = new MusicSessionCoordinator(
            config,
            playback,
            detector,
            process,
            capture,
            pickup,
            frames,
            isGameRunning: () => true,
            isGameForeground: () => true,
            isCaptureRunning: () => true,
            clockNow: () => 1000);

        var gameRect = new Rect(0, 0, 1920, 1080);
        var tracker = new GameWindowTracker(() => new GameWindowState(new IntPtr(1), true, gameRect, 96));

        // Presenter with a faulty window factory
        using var presenter = new MusicOverlayPresenter(
            config,
            coordinator,
            process,
            windowFactory: () => throw new InvalidOperationException("DirectX Device Lost / GPU Driver Reset"),
            tracker: tracker);

        // Session is ready
        coordinator.ReadinessGate.ObserveCapture(1000);
        coordinator.ReadinessGate.ObserveDetection(true, 1000);
        coordinator.ReadinessGate.ObserveDetection(true, 1000);
        coordinator.Tick();
        Assert.True(coordinator.Ready);

        // Load timeline and play
        playback.ConfigurePreparation(0);
        playback.LoadTimeline(new PerformanceTimeline
        {
            Events = [new PerformanceEvent { Key = "A", TimeMs = 0, DurationMs = 500, Kind = MusicNoteKind.Tap }],
            TotalDurationMs = 500
        });
        playback.Play();
        Assert.Equal(PlayerState.Playing, playback.State);

        // Trigger presenter tick - creation fails
        presenter.Tick();

        // Fault is isolated to overlay presenter
        Assert.True(presenter.IsFailed);
        Assert.Contains("DirectX Device Lost", presenter.RenderError);

        // Underlying playback is NOT crashed or suspended!
        Assert.Equal(PlayerState.Playing, playback.State);
        Assert.True(coordinator.Ready);
    }

    [Fact]
    public void VisibilityLifecycle_HidesOverlayWhenGameIsNotForegroundOrDisabled()
    {
        RunInSta(() =>
        {
            var config = new TestConfig();
            config.Config.MusicOverlay.Enabled = true;
            config.Config.Music.AutoFocusGameOnPlayback = false;

            var input = new TestInput();
            using var playback = new MusicPlaybackService(
                config,
                new GameProcessDetector(config),
                new KeyInputTransports(),
                clock: new MusicSessionClock(() => 0),
                inputTransport: input);

            var detector = new InstrumentDetector(config, null, null);
            var process = new GameProcessDetector(config);
            var capture = new GameTaskManager(config, process, new TaskTriggerDispatcher());
            var pickup = new AutoPickTrigger(config, null!, null!);
            var frames = new MusicCaptureObserver();

            bool foreground = true;
            bool exists = true;

            using var coordinator = new MusicSessionCoordinator(
                config,
                playback,
                detector,
                process,
                capture,
                pickup,
                frames,
                isGameRunning: () => exists,
                isGameForeground: () => foreground,
                isCaptureRunning: () => true,
                clockNow: () => 1000);

            var gameRect = new Rect(0, 0, 1920, 1080);
            var tracker = new GameWindowTracker(() => new GameWindowState(exists ? new IntPtr(1) : IntPtr.Zero, foreground, exists ? gameRect : Rect.Empty, 96));

            int windowCreations = 0;
            using var presenter = new MusicOverlayPresenter(
                config,
                coordinator,
                process,
                windowFactory: () =>
                {
                    windowCreations++;
                    return new MusicOverlayWindow();
                },
                tracker: tracker);

            // Readiness not satisfied yet -> should not create or show window
            presenter.Tick();
            Assert.Null(presenter.Window);

            // Satisfy readiness
            coordinator.ReadinessGate.ObserveCapture(1000);
            coordinator.ReadinessGate.ObserveDetection(true, 1000);
            coordinator.ReadinessGate.ObserveDetection(true, 1000);
            coordinator.Tick();
            Assert.True(coordinator.Ready);

            // Now tick should create window
            presenter.Tick();
            Assert.NotNull(presenter.Window);
            Assert.Equal(1, windowCreations);

            // Game loses foreground (minimized or switched out)
            foreground = false;
            presenter.Tick();
            // Window should be hidden
            Assert.False(presenter.Window.IsVisible);

            // Restore foreground
            foreground = true;
            presenter.Tick();
            Assert.True(presenter.Window.IsVisible);

            // Disable overlay in config
            config.Config.MusicOverlay.Enabled = false;
            presenter.Tick();
            Assert.False(presenter.Window.IsVisible);
        });
    }

    [Fact]
    public void Dispose_UnsubscribesEventsCleanly()
    {
        var config = new TestConfig();
        var input = new TestInput();
        using var playback = new MusicPlaybackService(
            config,
            new GameProcessDetector(config),
            new KeyInputTransports(),
            clock: new MusicSessionClock(() => 0),
            inputTransport: input);

        var detector = new InstrumentDetector(config, null, null);
        var process = new GameProcessDetector(config);
        var capture = new GameTaskManager(config, process, new TaskTriggerDispatcher());
        var pickup = new AutoPickTrigger(config, null!, null!);
        var frames = new MusicCaptureObserver();

        using var coordinator = new MusicSessionCoordinator(
            config,
            playback,
            detector,
            process,
            capture,
            pickup,
            frames);

        var presenter = new MusicOverlayPresenter(config, coordinator, process);
        presenter.Start();

        // Disposing presenter
        presenter.Dispose();

        // Emitting session changes after dispose must not throw or affect anything
        coordinator.LoadTimeline(new PerformanceTimeline());
        coordinator.Tick();
    }

    [Fact]
    public async Task LayoutAdjustment_EntersPausedAndRestoresPassThroughOnExit()
    {
        var config = new TestConfig();
        config.Config.MusicOverlay.Enabled = true;
        config.Config.Music.AutoFocusGameOnPlayback = false;

        var input = new TestInput();
        using var playback = new MusicPlaybackService(
            config,
            new GameProcessDetector(config),
            new KeyInputTransports(),
            clock: new MusicSessionClock(() => 0),
            inputTransport: input);

        var detector = new InstrumentDetector(config, null, null);
        var process = new GameProcessDetector(config);
        var capture = new GameTaskManager(config, process, new TaskTriggerDispatcher());
        var pickup = new AutoPickTrigger(config, null!, null!);
        var frames = new MusicCaptureObserver();

        using var coordinator = new MusicSessionCoordinator(
            config,
            playback,
            detector,
            process,
            capture,
            pickup,
            frames,
            isGameRunning: () => true,
            isGameForeground: () => true,
            isCaptureRunning: () => true,
            clockNow: () => 1000);

        var gameRect = new Rect(0, 0, 1920, 1080);
        var tracker = new GameWindowTracker(() => new GameWindowState(new IntPtr(1), true, gameRect, 96));

        using var presenter = new MusicOverlayPresenter(
            config,
            coordinator,
            process,
            tracker: tracker);

        // Start playback with hold note
        playback.ConfigurePreparation(0);
        playback.LoadTimeline(new PerformanceTimeline
        {
            Events = [new PerformanceEvent { Key = "A", TimeMs = 0, DurationMs = 2500, Kind = MusicNoteKind.Hold }],
            TotalDurationMs = 2500
        });
        playback.Play();
        Assert.Equal(PlayerState.Playing, playback.State);
        Assert.Contains(input.Commands, c => c.IsDown);

        // Enter adjust mode
        await presenter.SetAdjustingAsync(true);
        Assert.True(presenter.Adjusting);
        Assert.Equal(PlayerState.Paused, playback.State);
        Assert.Empty(playback.CaptureSnapshot().ActiveKeys);
        Assert.Contains(input.Commands, c => !c.IsDown);

        // Exit adjust mode
        await presenter.SetAdjustingAsync(false);
        Assert.False(presenter.Adjusting);
        // Playback remains paused, does not automatically resume
        Assert.Equal(PlayerState.Paused, playback.State);
    }

    [Fact]
    public void CreateRenderSnapshot_MapsSessionStateCaseInsensitively()
    {
        var config = new TestConfig();
        var playback = new MusicPlaybackService(
            config,
            new GameProcessDetector(config),
            new KeyInputTransports(),
            clock: new MusicSessionClock(() => 0));

        var detector = new InstrumentDetector(config, null, null);
        var process = new GameProcessDetector(config);
        var capture = new GameTaskManager(config, process, new TaskTriggerDispatcher());
        var pickup = new AutoPickTrigger(config, null!, null!);
        var frames = new MusicCaptureObserver();

        using var coordinator = new MusicSessionCoordinator(
            config,
            playback,
            detector,
            process,
            capture,
            pickup,
            frames,
            isGameRunning: () => true,
            isGameForeground: () => true,
            isCaptureRunning: () => true,
            clockNow: () => 1000);

        using var presenter = new MusicOverlayPresenter(
            config,
            coordinator,
            process);

        // Lowercase strings as generated by MusicPlaybackService.CaptureSnapshot()
        var playingSnapshot = new MusicSessionSnapshot("test", "playing", 100, 1000, 1.0, 500, true, 1, ["A"]);
        var renderPlaying = presenter.CreateRenderSnapshot(playingSnapshot);
        Assert.True(renderPlaying.IsPlaying);
        Assert.False(renderPlaying.IsPreparing);

        var preparingSnapshot = new MusicSessionSnapshot("test", "preparing", -500, 1000, 1.0, 500, true, 1, []);
        var renderPreparing = presenter.CreateRenderSnapshot(preparingSnapshot);
        Assert.False(renderPreparing.IsPlaying);
        Assert.True(renderPreparing.IsPreparing);

        // Mixed/Upper case strings
        var upperPlaying = new MusicSessionSnapshot("test", "Playing", 100, 1000, 1.0, 500, true, 1, []);
        Assert.True(presenter.CreateRenderSnapshot(upperPlaying).IsPlaying);

        var upperPreparing = new MusicSessionSnapshot("test", "Preparing", -500, 1000, 1.0, 500, true, 1, []);
        Assert.True(presenter.CreateRenderSnapshot(upperPreparing).IsPreparing);

        // Paused/stopped states
        var pausedSnapshot = new MusicSessionSnapshot("test", "paused", 100, 1000, 1.0, 500, true, 1, []);
        var renderPaused = presenter.CreateRenderSnapshot(pausedSnapshot);
        Assert.False(renderPaused.IsPlaying);
        Assert.False(renderPaused.IsPreparing);
    }
}
