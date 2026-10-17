using System.Collections.Generic;
using Vortice.Direct2D1;

namespace BetterPetitPlanet.MusicOverlay.Gpu;

/// <summary>Identifies the wakeup source of MsgWaitForMultipleObjectsEx on the render thread.</summary>
public enum GpuWaitReason : byte
{
    FrameLatency = 0,
    WakeHandle = 1,
    WindowMessage = 2,
    Timeout = 3,
    Error = 4
}

/// <summary>Captures a telemetry anomaly event exceeding cadence thresholds.</summary>
public readonly record struct GpuAnomalyEvent(
    double MonotonicMs,
    GpuWaitReason WaitReason,
    double WaitDurationMs,
    double RenderStartIntervalMs,
    double PresentReturnIntervalMs,
    double RenderDurationMs,
    double PresentCallDurationMs);

/// <summary>All callbacks run serially on the host's GPU thread. Do not retain frame targets.</summary>
public interface IGpuOverlayContent : IDisposable
{
    void OnDeviceCreated(ID2D1DeviceContext context);
    void OnDeviceDestroyed();
    void Render(in GpuOverlayFrame frame);
}

/// <summary>Coordinates are DIPs; MonotonicMs uses the process-wide Stopwatch clock.</summary>
public readonly record struct GpuOverlayFrame(
    ID2D1DeviceContext Context, float WidthDip, float HeightDip, float Dpi, double MonotonicMs);

/// <summary>
/// High-precision CPU timing and presentation completion statistics.
/// NOTE: These measure CPU execution and presentation completion intervals, NOT physical display scanout.
/// </summary>
public sealed record GpuOverlayStatistics(
    long Frames,
    long DeviceRecreations,
    double LastRenderMs,
    double LastPresentMs,
    double RenderP50Ms,
    double RenderP95Ms,
    double RenderMaxMs,
    double PresentP50Ms,
    double PresentP95Ms,
    double PresentMaxMs,
    // Detailed timing percentiles
    double RenderP99Ms = 0,
    double RenderAvgMs = 0,
    double PresentP99Ms = 0,
    double PresentAvgMs = 0,
    double WaitP50Ms = 0,
    double WaitP95Ms = 0,
    double WaitP99Ms = 0,
    double WaitMaxMs = 0,
    double WaitAvgMs = 0,
    double RenderStartIntervalP50Ms = 0,
    double RenderStartIntervalP95Ms = 0,
    double RenderStartIntervalP99Ms = 0,
    double RenderStartIntervalMaxMs = 0,
    double RenderStartIntervalAvgMs = 0,
    double PresentReturnIntervalP50Ms = 0,
    double PresentReturnIntervalP95Ms = 0,
    double PresentReturnIntervalP99Ms = 0,
    double PresentReturnIntervalMaxMs = 0,
    double PresentReturnIntervalAvgMs = 0,
    // Threshold violation counters
    long IntervalGt8_3msCount = 0,
    long IntervalGt16_6msCount = 0,
    long IntervalGt50msCount = 0,
    // Wait return reason counters
    long WaitFrameLatencyCount = 0,
    long WaitWakeHandleCount = 0,
    long WaitWindowMessageCount = 0,
    long WaitTimeoutCount = 0,
    long WaitErrorCount = 0,
    // Long-tail anomalies
    long TotalAnomalies = 0,
    IReadOnlyList<GpuAnomalyEvent>? RecentAnomalies = null);
