using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BetterPetitPlanet.GameTask.Music.Service;

/// <summary>
/// 乐器界面检测触发器。
/// 挂载于 TaskTriggerDispatcher，接入 GameTaskManager 实时截图流，驱动纯 OCR 实时识别。
/// </summary>
public sealed class InstrumentDetectorTrigger : ITaskTrigger
{
    private readonly InstrumentDetector _detector;
    private readonly ILogger<InstrumentDetectorTrigger>? _logger;

    private int _isProcessing;
    private long _lastProcessTimestamp;

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

        // 避免上一帧 OCR 仍在运算时产生堆积
        if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0)
        {
            return;
        }

        // 节流控制：约 400ms 触发一次 OCR 判定 (兼顾实时性与低资源开销)
        long now = Stopwatch.GetTimestamp();
        double elapsedMs = (double)(now - _lastProcessTimestamp) * 1000 / Stopwatch.Frequency;
        if (elapsedMs < 400)
        {
            Interlocked.Exchange(ref _isProcessing, 0);
            return;
        }

        _lastProcessTimestamp = now;

        // 克隆画面至后台异步线程执行 OCR，严禁阻塞截图器主捕获循环
        Mat frameCopy;
        try
        {
            frameCopy = content.Frame.Clone();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to clone frame for instrument OCR");
            Interlocked.Exchange(ref _isProcessing, 0);
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                _detector.DetectFull(frameCopy);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Exception in InstrumentDetectorTrigger OCR processing");
            }
            finally
            {
                frameCopy.Dispose();
                Interlocked.Exchange(ref _isProcessing, 0);
            }
        });
    }
}
