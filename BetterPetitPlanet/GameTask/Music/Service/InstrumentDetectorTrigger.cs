using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BetterPetitPlanet.GameTask.Music.Rendering;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BetterPetitPlanet.GameTask.Music.Service;

/// <summary>
/// 乐器界面检测触发器。
/// 挂载于 TaskTriggerDispatcher，接入 GameTaskManager 实时截图流，驱动多乐器分阶段高效识别。
/// 采用 ROI 局部克隆（显存/内存缩减 90% 以上）与乱序丢弃机制，兼顾极低开销与高并发时序安全。
/// </summary>
public sealed class InstrumentDetectorTrigger : ITaskTrigger
{
    private readonly InstrumentDetector _detector;
    private readonly ILogger<InstrumentDetectorTrigger>? _logger;

    private int _isProcessing;
    private long _lastProcessTimestamp;

    private long _sequenceCounter;
    private long _lastCompletedSequence;
    private readonly object _sequenceLock = new();

    public string Name => "乐器检测";
    public bool IsEnabled { get; set; } = true;
    public int Priority => 25;

    public InstrumentDetectorTrigger(
        InstrumentDetector detector,
        ILogger<InstrumentDetectorTrigger>? logger = null)
    {
        _detector = detector;
        _logger = logger;
    }

    public void OnCapture(CaptureContent content)
    {
        if (!IsEnabled || content.Frame == null || content.Frame.Empty())
        {
            return;
        }

        // 避免上一帧识别仍在运算时产生堆积
        if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0)
        {
            return;
        }

        // 节流控制：约 400ms 触发一次识别判定 (兼顾实时性与低资源开销)
        long now = Stopwatch.GetTimestamp();
        double elapsedMs = (double)(now - _lastProcessTimestamp) * 1000 / Stopwatch.Frequency;
        if (elapsedMs < 400)
        {
            Interlocked.Exchange(ref _isProcessing, 0);
            return;
        }

        _lastProcessTimestamp = now;

        int frameW = content.Frame.Width;
        int frameH = content.Frame.Height;
        if (frameW <= 0 || frameH <= 0)
        {
            Interlocked.Exchange(ref _isProcessing, 0);
            return;
        }

        // 内存优化：严禁全画幅克隆 2560x1440 BGRA 图像 (14.7MB)！
        // 乐器演奏区域在 2560x1440 基准下约 X: 500, Y: 1100, W: 1550, H: 250。
        // 仅对该局部 ROI 建立子图并克隆 (约 1.5MB，内存开销直接缩减 90% 以上)。
        double scaleX = frameW / 2560.0;
        double scaleY = frameH / 1440.0;

        int roiX = (int)Math.Round(500.0 * scaleX);
        int roiY = (int)Math.Round(1100.0 * scaleY);
        int roiW = (int)Math.Round(1550.0 * scaleX);
        int roiH = (int)Math.Round(250.0 * scaleY);

        roiX = Math.Clamp(roiX, 0, frameW - 1);
        roiY = Math.Clamp(roiY, 0, frameH - 1);
        roiW = Math.Clamp(roiW, 1, frameW - roiX);
        roiH = Math.Clamp(roiH, 1, frameH - roiY);

        Mat roiCopy;
        try
        {
            using var diagnosticClone = MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.OcrCloneMs);
            using var roiMat = new Mat(content.Frame, new Rect(roiX, roiY, roiW, roiH));
            roiCopy = roiMat.Clone();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to clone instrument ROI for detection");
            Interlocked.Exchange(ref _isProcessing, 0);
            return;
        }

        long currentSequence = Interlocked.Increment(ref _sequenceCounter);
        long diagnosticPosted = MusicOverlayDiagnostics.Enabled ? Stopwatch.GetTimestamp() : 0;

        _ = Task.Run(() =>
        {
            try
            {
                if (diagnosticPosted != 0)
                {
                    MusicOverlayDiagnostics.Record(
                        MusicOverlayDiagnostics.Metric.OcrQueueMs,
                        (Stopwatch.GetTimestamp() - diagnosticPosted) * 1000.0 / Stopwatch.Frequency);
                }

                using var diagnosticWork = MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.OcrWorkMs);

                // 乱序丢弃检查：若在排队期间已被更高序号的帧完成，直接丢弃
                lock (_sequenceLock)
                {
                    if (currentSequence < _lastCompletedSequence)
                    {
                        _logger?.LogDebug("Skipping superseded detection frame: seq {Seq} < latest {Latest}", currentSequence, _lastCompletedSequence);
                        return;
                    }
                }

                // 送入分阶段多乐器识别引擎
                _detector.DetectFull(roiCopy, roiX, roiY, frameW, frameH, currentSequence);

                // 完成后更新最新生效序号
                lock (_sequenceLock)
                {
                    if (currentSequence >= _lastCompletedSequence)
                    {
                        _lastCompletedSequence = currentSequence;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Exception in InstrumentDetectorTrigger processing");
            }
            finally
            {
                roiCopy.Dispose();
                Interlocked.Exchange(ref _isProcessing, 0);
            }
        });
    }
}
