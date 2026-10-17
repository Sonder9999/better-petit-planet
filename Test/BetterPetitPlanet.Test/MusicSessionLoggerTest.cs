using System.Collections.Concurrent;
using BetterPetitPlanet.GameTask.Music.Service;
using Microsoft.Extensions.Logging;

namespace BetterPetitPlanet.Test;

public sealed class MusicSessionLoggerTest
{
    private sealed class Sink(bool throwFirst = false) : ILogger<MusicPlaybackService>, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private int _calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<(LogLevel Level, int Event, Dictionary<string, object?> Fields)> Written { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.TrySetResult();
                _release.Wait();
                if (throwFirst) throw new IOException("logging sink unavailable");
            }
            var fields = ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(pair => pair.Key, pair => pair.Value);
            Written.Enqueue((level, id.Id, fields));
        }
        public void Unblock() => _release.Set();
        public void Dispose() => _release.Set();
    }

    [Fact]
    public async Task Saturation_BoundsOrdinaryLogsAndDrainsEveryCriticalSlotWithCounts()
    {
        using var sink = new Sink();
        using var logger = new MusicSessionLogger(sink, 2);
        logger.LogInformation("ordinary {Index}", -1);
        await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            for (int i = 0; i < 100; i++)
            {
                logger.LogInformation("ordinary {Index}", i);
                for (int slot = 0; slot < 9; slot++) logger.LogError(new EventId(slot), "critical {Index}", i);
            }
            Assert.Equal(98, logger.DroppedCount);
            logger.Dispose();
        }
        finally { sink.Unblock(); }
        await logger.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal([-1, 98, 99], sink.Written.Where(entry => entry.Level == LogLevel.Information)
            .Select(entry => (int)entry.Fields["Index"]!));
        for (int slot = 0; slot < 9; slot++)
        {
            Assert.Contains(sink.Written, entry => entry.Level == LogLevel.Error && entry.Event == slot &&
                entry.Fields.TryGetValue("Index", out var index) && (int)index! == 99);
            Assert.Contains(sink.Written, entry => entry.Level == LogLevel.Error &&
                entry.Fields.TryGetValue("Occurrences", out var count) && (long)count! == 100 &&
                (int)entry.Fields["EventId"]! == slot);
        }
    }

    [Fact]
    public async Task SinkException_DoesNotTerminateConsumerOrLoseLaterRecoveryLogs()
    {
        using var sink = new Sink(throwFirst: true);
        using var logger = new MusicSessionLogger(sink, 1);
        logger.LogWarning("first write fails");
        await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        logger.LogError(new EventId(MusicSessionLogger.ReleaseEventBase), "release exhausted");
        logger.Dispose();
        sink.Unblock();
        await logger.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, logger.SinkFailureCount);
        Assert.Contains(sink.Written, entry => entry.Event == MusicSessionLogger.ReleaseEventBase);

        // A later explicit Stop may have diagnostics even though disposal already drained.
        logger.LogError(new EventId(MusicSessionLogger.SchedulingEvent), "late recovery failed");
        await logger.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(sink.Written, entry => entry.Event == MusicSessionLogger.SchedulingEvent);
    }
}
