using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.AutoPick;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace BetterPetitPlanet.Test;

public sealed class ItemNameRecognitionServiceTest
{
    [Fact]
    public void Disabled_DoesNotCreateRecognizer()
    {
        var config = new TestConfigService();
        using var service = new ItemNameRecognitionService(config, () => throw new InvalidOperationException());
        using var frame = new Mat(100, 100, MatType.CV_8UC3);
        Assert.False(service.TryQueue(frame));
    }

    [Fact]
    public async Task Busy_ReturnsWithoutWaiting_AndOwnsSnapshot()
    {
        var config = new TestConfigService();
        config.Config.AutoPick.ItemNameRecognition.Enabled = true;
        using var recognizer = new BlockingRecognizer();
        using var service = new ItemNameRecognitionService(config, () => recognizer);
        var frame = new Mat(100, 100, MatType.CV_8UC3, Scalar.All(23));
        Assert.True(service.TryQueue(frame));
        frame.SetTo(Scalar.All(99));
        frame.Dispose();
        await recognizer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var nextFrame = new Mat(100, 100, MatType.CV_8UC3);
        Assert.False(service.TryQueue(nextFrame));
        service.Dispose(); // Must not wait for inference, or dispose resources being used.
        Assert.False(recognizer.WasDisposed);
        recognizer.Release.TrySetResult();
        await recognizer.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(23, recognizer.Pixel);
        await recognizer.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(service.TryQueue(nextFrame));
    }

    [Fact]
    public async Task BackgroundFailure_ReleasesBusySlot()
    {
        var config = new TestConfigService();
        config.Config.AutoPick.ItemNameRecognition.Enabled = true;
        var attempts = 0;
        var logger = new RecordingLogger();
        using var service = new ItemNameRecognitionService(config, () =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("Model unavailable");
        }, logger);
        using var frame = new Mat(100, 100, MatType.CV_8UC3);
        Assert.True(service.TryQueue(frame));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref attempts) < 2)
        {
            await Task.Delay(10, timeout.Token);
            service.TryQueue(frame);
        }
        service.Dispose();
        Assert.Empty(logger.Messages);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task InFlightResult_LogsOnlyWhileEnabled(bool disableDuringRecognition, int expectedLogs)
    {
        var config = new TestConfigService();
        config.Config.AutoPick.ItemNameRecognition.Enabled = true;
        using var recognizer = new BlockingRecognizer();
        var logger = new RecordingLogger();
        using var service = new ItemNameRecognitionService(config, () => recognizer, logger);
        using var frame = new Mat(100, 100, MatType.CV_8UC3);
        Assert.True(service.TryQueue(frame));
        await recognizer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        config.Config.AutoPick.ItemNameRecognition.Enabled = !disableDuringRecognition;
        recognizer.Release.TrySetResult();
        await recognizer.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!disableDuringRecognition)
            await logger.Logged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Dispose();
        await recognizer.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expectedLogs, logger.Messages.Count);
        if (expectedLogs > 0)
            Assert.Equal("【自动拾取】拾取到物品: 红向日葵", logger.Messages.Single());
    }

    private sealed class RecordingLogger : ILogger<AutoPickTrigger>
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Messages { get; } = [];
        public TaskCompletionSource Logged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Logged.TrySetResult();
        }
    }

    internal sealed class TestConfigService : IConfigService
    {
        public AppConfig Config { get; } = new();
        public int SaveCount { get; private set; }
        public void Save() => SaveCount++;
        public void Reload() { }
    }

    private sealed class BlockingRecognizer : IItemNameRecognizer
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasDisposed { get; private set; }
        public byte Pixel { get; private set; }

        public string Recognize(Mat searchImage, ItemNameSearchArea area)
        {
            Started.TrySetResult();
            Release.Task.Wait(TimeSpan.FromSeconds(5));
            Pixel = searchImage.At<Vec3b>(0, 0).Item0;
            Finished.TrySetResult();
            return "红向日葵";
        }

        public void Dispose()
        {
            WasDisposed = true;
            Release.TrySetResult();
            Disposed.TrySetResult();
        }
    }
}
