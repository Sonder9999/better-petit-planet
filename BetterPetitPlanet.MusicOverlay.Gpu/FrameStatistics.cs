namespace BetterPetitPlanet.MusicOverlay.Gpu;

/// <summary>
/// Fixed 0.1 ms resolution histogram buckets (0 to 200 ms, plus overflow) and circular anomaly ring buffer.
/// Measures CPU calls and presentation completion intervals; these do not measure physical display scanout.
/// </summary>
internal sealed class FrameStatistics
{
    private sealed class Histogram
    {
        private const int BucketsPerMs = 10; // 0.1 ms resolution
        private const int MaxTrackedMs = 200;
        private const int BucketCount = MaxTrackedMs * BucketsPerMs + 1; // 2001
        private readonly long[] _buckets = new long[BucketCount];
        private long _samples;
        private double _max;
        private double _sum;

        internal void Record(double durationMs)
        {
            if (durationMs < 0) durationMs = 0;
            _samples++;
            _sum += durationMs;
            if (durationMs > _max) _max = durationMs;
            int bucket = Math.Clamp((int)(durationMs * BucketsPerMs), 0, BucketCount - 1);
            _buckets[bucket]++;
        }

        internal double Percentile(double quantile)
        {
            if (_samples == 0) return 0;
            long needed = (long)Math.Ceiling(_samples * quantile);
            long count = 0;
            for (int i = 0; i < BucketCount; i++)
            {
                count += _buckets[i];
                if (count >= needed)
                {
                    if (i == BucketCount - 1) return _max;
                    return (i + 0.5) / BucketsPerMs;
                }
            }
            return _max;
        }

        internal double Max => _samples > 0 ? _max : 0;
        internal double Avg => _samples > 0 ? _sum / _samples : 0;
        internal long Samples => _samples;
    }

    private readonly object _gate = new();
    private readonly Histogram _waitHist = new();
    private readonly Histogram _renderHist = new();
    private readonly Histogram _presentCallHist = new();
    private readonly Histogram _renderStartIntervalHist = new();
    private readonly Histogram _presentReturnIntervalHist = new();

    private readonly GpuAnomalyEvent[] _anomalyRing = new GpuAnomalyEvent[64];
    private int _anomalyHead;
    private long _totalAnomalies;

    private long _frames, _recreations;
    private double _lastRender, _lastPresent;

    // Threshold violations on PresentReturnInterval
    private long _intervalGt8_3msCount;
    private long _intervalGt16_6msCount;
    private long _intervalGt50msCount;

    // Wait reason counters
    private long _waitFrameLatencyCount;
    private long _waitWakeHandleCount;
    private long _waitWindowMessageCount;
    private long _waitTimeoutCount;
    private long _waitErrorCount;

    internal void RecordWaitOnly(GpuWaitReason reason, double waitDurationMs, double monotonicMs)
    {
        lock (_gate)
        {
            IncrementReasonCount(reason);
            _waitHist.Record(waitDurationMs);
            if (waitDurationMs > 8.333 || reason == GpuWaitReason.Timeout || reason == GpuWaitReason.Error)
            {
                RecordAnomalyLocked(new GpuAnomalyEvent(
                    monotonicMs, reason, waitDurationMs, 0, 0, 0, 0));
            }
        }
    }

    internal void RecordFrame(
        long recreations,
        double waitDurationMs,
        double renderDurationMs,
        double presentCallDurationMs,
        double renderStartIntervalMs,
        double presentReturnIntervalMs,
        double monotonicMs)
    {
        lock (_gate)
        {
            _frames++;
            _recreations = recreations;
            _lastRender = renderDurationMs;
            _lastPresent = presentCallDurationMs;

            _waitFrameLatencyCount++;
            _waitHist.Record(waitDurationMs);
            _renderHist.Record(renderDurationMs);
            _presentCallHist.Record(presentCallDurationMs);

            if (renderStartIntervalMs > 0)
                _renderStartIntervalHist.Record(renderStartIntervalMs);

            if (presentReturnIntervalMs > 0)
            {
                _presentReturnIntervalHist.Record(presentReturnIntervalMs);
                if (presentReturnIntervalMs > 8.333) _intervalGt8_3msCount++;
                if (presentReturnIntervalMs > 16.667) _intervalGt16_6msCount++;
                if (presentReturnIntervalMs > 50.0) _intervalGt50msCount++;
            }

            if (presentReturnIntervalMs > 8.333 || waitDurationMs > 8.333 ||
                renderDurationMs > 8.333 || presentCallDurationMs > 8.333)
            {
                RecordAnomalyLocked(new GpuAnomalyEvent(
                    monotonicMs,
                    GpuWaitReason.FrameLatency,
                    waitDurationMs,
                    renderStartIntervalMs,
                    presentReturnIntervalMs,
                    renderDurationMs,
                    presentCallDurationMs));
            }
        }
    }

    internal void Record(long recreations, double render, double present)
    {
        lock (_gate)
        {
            _frames++;
            _recreations = recreations;
            _lastRender = render;
            _lastPresent = present;
            _renderHist.Record(render);
            _presentCallHist.Record(present);
        }
    }

    private void IncrementReasonCount(GpuWaitReason reason)
    {
        switch (reason)
        {
            case GpuWaitReason.FrameLatency: _waitFrameLatencyCount++; break;
            case GpuWaitReason.WakeHandle: _waitWakeHandleCount++; break;
            case GpuWaitReason.WindowMessage: _waitWindowMessageCount++; break;
            case GpuWaitReason.Timeout: _waitTimeoutCount++; break;
            default: _waitErrorCount++; break;
        }
    }

    private void RecordAnomalyLocked(in GpuAnomalyEvent evt)
    {
        _anomalyRing[_anomalyHead] = evt;
        _anomalyHead = (_anomalyHead + 1) % _anomalyRing.Length;
        _totalAnomalies++;
    }

    internal GpuOverlayStatistics Capture()
    {
        lock (_gate)
        {
            int anomalyCount = (int)Math.Min(_totalAnomalies, _anomalyRing.Length);
            var anomalies = new GpuAnomalyEvent[anomalyCount];
            int start = (_totalAnomalies <= _anomalyRing.Length) ? 0 : _anomalyHead;
            for (int i = 0; i < anomalyCount; i++)
            {
                anomalies[i] = _anomalyRing[(start + i) % _anomalyRing.Length];
            }

            return new GpuOverlayStatistics(
                _frames,
                _recreations,
                _lastRender,
                _lastPresent,
                _renderHist.Percentile(0.50),
                _renderHist.Percentile(0.95),
                _renderHist.Max,
                _presentCallHist.Percentile(0.50),
                _presentCallHist.Percentile(0.95),
                _presentCallHist.Max,
                // Detailed timing percentiles
                RenderP99Ms: _renderHist.Percentile(0.99),
                RenderAvgMs: _renderHist.Avg,
                PresentP99Ms: _presentCallHist.Percentile(0.99),
                PresentAvgMs: _presentCallHist.Avg,
                WaitP50Ms: _waitHist.Percentile(0.50),
                WaitP95Ms: _waitHist.Percentile(0.95),
                WaitP99Ms: _waitHist.Percentile(0.99),
                WaitMaxMs: _waitHist.Max,
                WaitAvgMs: _waitHist.Avg,
                RenderStartIntervalP50Ms: _renderStartIntervalHist.Percentile(0.50),
                RenderStartIntervalP95Ms: _renderStartIntervalHist.Percentile(0.95),
                RenderStartIntervalP99Ms: _renderStartIntervalHist.Percentile(0.99),
                RenderStartIntervalMaxMs: _renderStartIntervalHist.Max,
                RenderStartIntervalAvgMs: _renderStartIntervalHist.Avg,
                PresentReturnIntervalP50Ms: _presentReturnIntervalHist.Percentile(0.50),
                PresentReturnIntervalP95Ms: _presentReturnIntervalHist.Percentile(0.95),
                PresentReturnIntervalP99Ms: _presentReturnIntervalHist.Percentile(0.99),
                PresentReturnIntervalMaxMs: _presentReturnIntervalHist.Max,
                PresentReturnIntervalAvgMs: _presentReturnIntervalHist.Avg,
                // Threshold violation counters
                IntervalGt8_3msCount: _intervalGt8_3msCount,
                IntervalGt16_6msCount: _intervalGt16_6msCount,
                IntervalGt50msCount: _intervalGt50msCount,
                // Wait return reason counters
                WaitFrameLatencyCount: _waitFrameLatencyCount,
                WaitWakeHandleCount: _waitWakeHandleCount,
                WaitWindowMessageCount: _waitWindowMessageCount,
                WaitTimeoutCount: _waitTimeoutCount,
                WaitErrorCount: _waitErrorCount,
                // Long-tail anomalies
                TotalAnomalies: _totalAnomalies,
                RecentAnomalies: anomalies
            );
        }
    }
}
