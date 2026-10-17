using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Service;
using Microsoft.Extensions.Logging;

namespace BetterPetitPlanet.Test;

public sealed class MusicLoggingSafetyTest
{
    private sealed class TestConfig : IConfigService
    {
        public AppConfig Config { get; } = new();
        public void Save() { }
        public void Reload() { }
    }

    private sealed class Input : IMusicInputTransport
    {
        public bool FailRelease { get; init; }
        public IReadOnlyList<MusicInputResult> Submit(IReadOnlyList<MusicKeyCommand> commands, IntPtr target, MusicInputMode mode)
            => commands.Select(command => new MusicInputResult(command, command.IsDown || !FailRelease, "release failed")).ToArray();
    }

    private sealed class BlockingLogger(string blockedMessage) : ILogger<MusicPlaybackService>, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ExhaustionWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);
            if (message.Contains(blockedMessage, StringComparison.OrdinalIgnoreCase))
            {
                Entered.TrySetResult();
                _release.Wait();
            }
            if (level == LogLevel.Error && message.Contains("release retries exhausted", StringComparison.OrdinalIgnoreCase))
                ExhaustionWritten.TrySetResult();
        }
        public void Unblock() => _release.Set();
        public void Dispose() { _release.Set(); }
    }

    private static MusicPlaybackService Create(BlockingLogger logger, bool failRelease = false)
    {
        var config = new TestConfig();
        var service = new MusicPlaybackService(config, new GameProcessDetector(config), new KeyInputTransports(),
            logger: logger, clock: new MusicSessionClock(() => 0), inputTransport: new Input { FailRelease = failRelease },
            options: new() { NotificationCapacity = 1, ReleaseRetryAttempts = 1 });
        service.ConfigurePreparation(0);
        service.LoadTimeline(new() { Events = [new() { Key = "A", Kind = MusicNoteKind.Hold, DurationMs = 2500 }] });
        return service;
    }

    [Fact]
    public async Task BlockedOverflowLogger_DoesNotBlockReadinessRelease()
    {
        using var logger = new BlockingLogger("observer overflow");
        using var observerGate = new ManualResetEventSlim();
        var observerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = Create(logger);
        service.InputChanged += _ => { observerEntered.TrySetResult(); observerGate.Wait(); };
        service.Play();
        await observerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        service.Pause(); // The up fills the one-entry notification queue.
        service.Seek(0);
        var restart = Task.Run(service.Play); // The next down overflows while its key is owned.
        Task readiness = Task.CompletedTask;
        try
        {
            await logger.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            readiness = Task.Run(() => service.SetReadiness(false, "focus lost"));
            await readiness.WaitAsync(TimeSpan.FromMilliseconds(250));
            Assert.Empty(service.CaptureSnapshot().ActiveKeys);
            Assert.Equal(PlayerState.Paused, service.State);
        }
        finally
        {
            logger.Unblock();
            observerGate.Set();
            await Task.WhenAll(restart, readiness).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData("Music input failed")]
    [InlineData("release retries exhausted")]
    public async Task BlockedReleaseLogger_DisposeReturnsAndCriticalErrorDrains(string blockedMessage)
    {
        using var logger = new BlockingLogger(blockedMessage);
        using var service = Create(logger, failRelease: true);
        service.Play();
        var dispose = Task.Run(service.Dispose);
        try
        {
            await logger.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await dispose.WaitAsync(TimeSpan.FromMilliseconds(250));
            Assert.Equal(["A"], service.CaptureSnapshot().ActiveKeys);
        }
        finally
        {
            logger.Unblock();
            await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        }
        await logger.ExhaustionWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
