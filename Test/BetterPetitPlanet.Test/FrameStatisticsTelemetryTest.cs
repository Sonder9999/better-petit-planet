using System;
using System.Diagnostics;
using BetterPetitPlanet.MusicOverlay.Gpu;
using Xunit;

namespace BetterPetitPlanet.Test;

public class FrameStatisticsTelemetryTest
{
    [Fact]
    public void FrameStatistics_RecordsFrameTimingAndPercentiles()
    {
        var stats = new FrameStatistics();

        // Feed known values
        for (int i = 0; i < 100; i++)
        {
            stats.RecordFrame(
                recreations: 0,
                waitDurationMs: 3.8,
                renderDurationMs: 0.2,
                presentCallDurationMs: 0.1,
                renderStartIntervalMs: 4.16,
                presentReturnIntervalMs: 4.16,
                monotonicMs: 1000.0 + i * 4.16);
        }

        var captured = stats.Capture();
        Assert.Equal(100, captured.Frames);
        Assert.Equal(100, captured.WaitFrameLatencyCount);
        Assert.Equal(0, captured.IntervalGt8_3msCount);
        Assert.True(captured.WaitP50Ms >= 3.7 && captured.WaitP50Ms <= 3.9);
        Assert.True(captured.RenderP50Ms >= 0.15 && captured.RenderP50Ms <= 0.25);
        Assert.True(captured.PresentP50Ms >= 0.05 && captured.PresentP50Ms <= 0.15);
        Assert.True(captured.PresentReturnIntervalP50Ms >= 4.1 && captured.PresentReturnIntervalP50Ms <= 4.25);
    }

    [Fact]
    public void FrameStatistics_TracksWaitReasonsCorrectly()
    {
        var stats = new FrameStatistics();

        stats.RecordWaitOnly(GpuWaitReason.WakeHandle, 1.5, 10.0);
        stats.RecordWaitOnly(GpuWaitReason.WindowMessage, 0.5, 11.0);
        stats.RecordWaitOnly(GpuWaitReason.Timeout, 100.0, 111.0);
        stats.RecordWaitOnly(GpuWaitReason.Error, 2.0, 113.0);

        stats.RecordFrame(0, 3.8, 0.2, 0.1, 4.16, 4.16, 120.0);

        var captured = stats.Capture();
        Assert.Equal(1, captured.Frames);
        Assert.Equal(1, captured.WaitFrameLatencyCount);
        Assert.Equal(1, captured.WaitWakeHandleCount);
        Assert.Equal(1, captured.WaitWindowMessageCount);
        Assert.Equal(1, captured.WaitTimeoutCount);
        Assert.Equal(1, captured.WaitErrorCount);

        // Timeout (100ms) and Error should be in the anomaly list
        Assert.NotNull(captured.RecentAnomalies);
        Assert.True(captured.TotalAnomalies >= 2);
    }

    [Fact]
    public void FrameStatistics_TracksCadenceThresholdViolationsAndRingBuffer()
    {
        var stats = new FrameStatistics();

        // 10 normal frames
        for (int i = 0; i < 10; i++)
        {
            stats.RecordFrame(0, 3.8, 0.2, 0.1, 4.16, 4.16, 100.0 + i * 4.16);
        }

        // 1 spike of 9.0ms (>8.33ms)
        stats.RecordFrame(0, 8.5, 0.2, 0.1, 9.0, 9.0, 200.0);

        // 1 spike of 17.5ms (>16.67ms)
        stats.RecordFrame(0, 16.5, 0.3, 0.1, 17.5, 17.5, 300.0);

        // 1 major stall of 55.0ms (>50ms)
        stats.RecordFrame(0, 52.0, 0.5, 0.2, 55.0, 55.0, 400.0);

        var captured = stats.Capture();
        Assert.Equal(13, captured.Frames);
        Assert.Equal(3, captured.IntervalGt8_3msCount);
        Assert.Equal(2, captured.IntervalGt16_6msCount);
        Assert.Equal(1, captured.IntervalGt50msCount);

        Assert.Equal(3, captured.TotalAnomalies);
        Assert.Equal(3, captured.RecentAnomalies?.Count);

        // Verify ring buffer content
        var spike1 = captured.RecentAnomalies![0];
        Assert.Equal(GpuWaitReason.FrameLatency, spike1.WaitReason);
        Assert.Equal(9.0, spike1.PresentReturnIntervalMs);

        var spike2 = captured.RecentAnomalies![1];
        Assert.Equal(17.5, spike2.PresentReturnIntervalMs);

        var spike3 = captured.RecentAnomalies![2];
        Assert.Equal(55.0, spike3.PresentReturnIntervalMs);
    }

    [Fact]
    public void FrameStatistics_AnomalyRingBuffer_CapsAt64Entries()
    {
        var stats = new FrameStatistics();

        // Insert 100 anomalies
        for (int i = 0; i < 100; i++)
        {
            stats.RecordFrame(0, 10.0 + i, 0.2, 0.1, 10.0 + i, 10.0 + i, 1000.0 + i * 10);
        }

        var captured = stats.Capture();
        Assert.Equal(100, captured.TotalAnomalies);
        Assert.NotNull(captured.RecentAnomalies);
        Assert.Equal(64, captured.RecentAnomalies.Count);

        // First element in recent 64 should be anomaly index 36 (100 - 64)
        Assert.Equal(10.0 + 36, captured.RecentAnomalies[0].PresentReturnIntervalMs);
        // Last element should be anomaly index 99
        Assert.Equal(10.0 + 99, captured.RecentAnomalies[63].PresentReturnIntervalMs);
    }

    [Fact]
    public void FrameStatistics_OverheadMicrobenchmark_UnderTwoMicrosecondsAndZeroAlloc()
    {
        var stats = new FrameStatistics();

        // Warmup
        for (int i = 0; i < 1000; i++)
        {
            stats.RecordFrame(0, 3.8, 0.18, 0.12, 4.16, 4.16, i * 4.16);
        }

        // Measure allocations using static QPC (no objects created during measurement)
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long t0 = Stopwatch.GetTimestamp();
        const int iterations = 10_000;

        for (int i = 0; i < iterations; i++)
        {
            stats.RecordFrame(0, 3.8, 0.18, 0.12, 4.16, 4.16, i * 4.16);
        }

        long t1 = Stopwatch.GetTimestamp();
        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        long bytesAllocated = allocatedAfter - allocatedBefore;
        double totalMicroseconds = (t1 - t0) * 1_000_000.0 / Stopwatch.Frequency;
        double usPerIteration = totalMicroseconds / iterations;

        // Zero allocations on the per-frame hot path
        Assert.Equal(0, bytesAllocated);

        // Must be well under 2 microseconds (typically ~0.05 - 0.2 us)
        Assert.True(usPerIteration < 2.0, $"Overhead was {usPerIteration:F3} us per frame (expected < 2.0 us)");
    }
}
