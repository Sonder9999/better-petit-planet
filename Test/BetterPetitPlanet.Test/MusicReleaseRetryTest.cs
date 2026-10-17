using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Service;
using Microsoft.Extensions.Logging;

namespace BetterPetitPlanet.Test;

public sealed class MusicReleaseRetryTest
{
    private sealed class Input : IMusicInputTransport
    {
        private int _ups;
        private int _downs;
        public bool FailReleases { get; set; } = true;
        public int Ups => Volatile.Read(ref _ups);
        public TaskCompletionSource FirstUp { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondUp { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ThirdUp { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondDown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(IntPtr Target, MusicInputMode Mode)> ReleaseRoutes { get; } = [];
        public IReadOnlyList<MusicInputResult> Submit(IReadOnlyList<MusicKeyCommand> commands, IntPtr target, MusicInputMode mode)
        {
            return commands.Select(command =>
            {
                if (!command.IsDown)
                {
                    ReleaseRoutes.Add((target, mode));
                    int count = Interlocked.Increment(ref _ups);
                    if (count == 1) FirstUp.TrySetResult();
                    if (count == 2) SecondUp.TrySetResult();
                    if (count == 3) ThirdUp.TrySetResult();
                    if (!FailReleases) Released.TrySetResult();
                }
                else if (Interlocked.Increment(ref _downs) == 2) SecondDown.TrySetResult();
                return new MusicInputResult(command, command.IsDown || !FailReleases, "native release failed");
            }).ToArray();
        }
    }

    private sealed class Logger : ILogger<MusicPlaybackService>
    {
        public TaskCompletionSource Error { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SchedulingFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Error && formatter(state, exception).Contains("scheduling failed"))
                SchedulingFailed.TrySetResult();
            if (level == LogLevel.Error && formatter(state, exception).Contains("release", StringComparison.OrdinalIgnoreCase))
                Error.TrySetResult();
        }
    }

    private sealed class TestConfig : IConfigService
    {
        private readonly AppConfig _config = new();
        public int FailNextRead;
        public AppConfig Config => Interlocked.Exchange(ref FailNextRead, 0) == 1
            ? throw new InvalidOperationException("configuration unavailable") : _config;
        public void Save() { }
        public void Reload() { }
    }

    [Fact]
    public void DueRelease_UsesWallTimeIntervalAndFiniteBudgetWhileRetainingOwnership()
    {
        double now = 0;
        var input = new Input();
        var scheduler = new MusicNoteScheduler(input,
            new() { ReleaseRetryIntervalMs = 100, ReleaseRetryAttempts = 3 }, () => now);
        scheduler.Load([new() { Key = "A", TimeMs = 0, DurationMs = 45 }]);
        scheduler.Advance(0, true, (IntPtr)1, MusicInputMode.Keyboard);
        scheduler.Advance(45, true, (IntPtr)1, MusicInputMode.Keyboard);
        Assert.Equal(1, input.Ups);
        for (now = 1; now < 100; now++) scheduler.Advance(10000, true, (IntPtr)1, MusicInputMode.Keyboard);
        Assert.Equal(1, input.Ups);
        now = 100;
        scheduler.Advance(10000, true, (IntPtr)1, MusicInputMode.Keyboard);
        Assert.Equal(2, input.Ups);
        now = 200;
        scheduler.Advance(10000, true, (IntPtr)1, MusicInputMode.Keyboard);
        Assert.Equal(3, input.Ups);
        for (now = 201; now < 1000; now++) scheduler.Advance(10000, true, (IntPtr)1, MusicInputMode.Keyboard);
        Assert.Equal(3, input.Ups);
        Assert.Equal(["A"], scheduler.ActiveKeys);
        Assert.Contains("exhausted", scheduler.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("stop")]
    [InlineData("automatic-off")]
    [InlineData("readiness")]
    [InlineData("dispose")]
    [InlineData("mode")]
    public async Task InterruptedRelease_DoesNotFloodAndNewStopStartsFreshRecovery(string interruption)
    {
        double now = 0;
        var config = new TestConfig();
        config.Config.Music.AutoFocusGameOnPlayback = false;
        var input = new Input();
        var logger = new Logger();
        using var service = new MusicPlaybackService(config, new GameProcessDetector(config), new KeyInputTransports(),
            logger: logger, clock: new MusicSessionClock(() => Volatile.Read(ref now)), inputTransport: input,
            options: new() { ReleaseRetryIntervalMs = 100, ReleaseRetryAttempts = 3 });
        service.ConfigurePreparation(0);
        service.LoadTimeline(new() { Events = [new() { Key = "A", DurationMs = 2500, Kind = MusicNoteKind.Hold }] });
        var exhausted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int exhaustionReports = 0;
        service.PlaybackError += message =>
        {
            if (!message.Contains("exhausted", StringComparison.OrdinalIgnoreCase)) return;
            Interlocked.Increment(ref exhaustionReports);
            exhausted.TrySetResult();
        };
        service.Play();
        switch (interruption)
        {
            case "pause": service.Pause(); break;
            case "stop": service.Stop(); break;
            case "automatic-off": service.SetAutomaticPlayback(false); break;
            case "readiness": service.SetReadiness(false, "focus lost"); break;
            case "dispose": service.Dispose(); break;
            case "mode":
                config.Config.Music.InputMode = MusicInputMode.Mouse;
                break;
        }
        await input.FirstUp.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, input.Ups);
        await Task.Delay(30); // The worker ticks repeatedly while monotonic wall time stays fixed.
        Assert.Equal(1, input.Ups);
        Volatile.Write(ref now, 100);
        await input.SecondUp.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Volatile.Write(ref now, 200);
        await input.ThirdUp.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await exhausted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await logger.Error.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Volatile.Write(ref now, 1000);
        await Task.Delay(30);
        Assert.Equal(3, input.Ups);
        Assert.Equal(1, Volatile.Read(ref exhaustionReports));
        Assert.Equal(["A"], service.CaptureSnapshot().ActiveKeys);
        Assert.All(input.ReleaseRoutes, route => Assert.Equal(MusicInputMode.Keyboard, route.Mode));
        if (interruption == "dispose")
        {
            for (int i = 0; i < 10; i++) service.Dispose();
            Assert.Equal(3, input.Ups);
        }
        input.FailReleases = false;
        service.Stop();
        Assert.Equal(4, input.Ups);
        Assert.Empty(service.CaptureSnapshot().ActiveKeys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_AfterFaultedWorker_RestartsRecoveryWhileDisposePreservesExhaustion(bool dispose)
    {
        double now = 0;
        var config = new TestConfig();
        var input = new Input();
        var logger = new Logger();
        using var service = new MusicPlaybackService(config, new GameProcessDetector(config), new KeyInputTransports(),
            logger: logger, clock: new MusicSessionClock(() => Volatile.Read(ref now)), inputTransport: input,
            options: new() { ReleaseRetryIntervalMs = 10, ReleaseRetryAttempts = 2 });
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StateChanged += state => { if (state == PlayerState.Paused) paused.TrySetResult(); };
        service.ConfigurePreparation(0);
        service.LoadTimeline(new() { Events = [new() { Key = "A", DurationMs = 2500, Kind = MusicNoteKind.Hold }] });
        service.Play();
        Volatile.Write(ref config.FailNextRead, 1);
        await logger.SchedulingFailed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(PlayerState.Paused, service.State);
        Volatile.Write(ref now, 10);
        await logger.Error.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50); // Let the failed worker complete its bounded cleanup.
        Assert.Equal(2, input.Ups);
        Assert.Equal(["A"], service.CaptureSnapshot().ActiveKeys);

        if (dispose)
            for (int i = 0; i < 10; i++) service.Dispose();
        Assert.Equal(2, input.Ups);
        service.Stop(); // Only explicit Stop can start a new budget, preserving the next allowed time.
        Assert.Equal(2, input.Ups);
        Volatile.Write(ref now, 20);
        await input.ThirdUp.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, input.Ups);
        input.FailReleases = false;
        Volatile.Write(ref now, 30);
        await input.Released.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(4, input.Ups);
        Assert.Empty(service.CaptureSnapshot().ActiveKeys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedRoute_RetriesOriginalRouteWithoutResettingBudget(bool changeMode)
    {
        double now = 0;
        var input = new Input();
        var scheduler = new MusicNoteScheduler(input,
            new() { ReleaseRetryIntervalMs = 100, ReleaseRetryAttempts = 2 }, () => now);
        scheduler.Load([new() { Key = "A", DurationMs = 2500, Kind = MusicNoteKind.Hold }]);
        scheduler.Advance(0, true, (IntPtr)1, MusicInputMode.Keyboard);
        var mode = changeMode ? MusicInputMode.Mouse : MusicInputMode.Keyboard;
        var target = changeMode ? (IntPtr)1 : (IntPtr)2;
        scheduler.Advance(0, true, target, mode);
        scheduler.Advance(0, true, target, mode);
        Assert.Equal(1, input.Ups);
        now = 100;
        scheduler.Advance(0, true, target, mode);
        now = 200;
        scheduler.Advance(0, true, target, mode);
        Assert.Equal(2, input.Ups);
        Assert.All(input.ReleaseRoutes, route => Assert.Equal(((IntPtr)1, MusicInputMode.Keyboard), route));
        Assert.Equal(["A"], scheduler.ActiveKeys);
    }

    [Fact]
    public async Task ResumeDuringFaultCleanup_KeepsSchedulingAfterCleanupFinishes()
    {
        double now = 0;
        var config = new TestConfig();
        var input = new Input();
        using var service = new MusicPlaybackService(config, new GameProcessDetector(config), new KeyInputTransports(),
            clock: new MusicSessionClock(() => Volatile.Read(ref now)), inputTransport: input,
            options: new() { ReleaseRetryIntervalMs = 10 });
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StateChanged += state => { if (state == PlayerState.Paused) paused.TrySetResult(); };
        service.ConfigurePreparation(0);
        service.LoadTimeline(new() { Events = [new() { Key = "A", DurationMs = 2500, Kind = MusicNoteKind.Hold },
            new() { Key = "F", TimeMs = 500 }] });
        service.Play();
        Volatile.Write(ref config.FailNextRead, 1);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(2));
        service.Play();
        input.FailReleases = false;
        Volatile.Write(ref now, 10);
        await input.Released.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Volatile.Write(ref now, 500);
        await input.SecondDown.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(PlayerState.Playing, service.State);
    }
}
