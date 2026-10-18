using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using BetterPetitPlanet.GameCapture;
using OpenCvSharp;

namespace BetterPetitPlanet.GameTask;

public sealed class CaptureContent : IDisposable
{
    private readonly Lazy<GameCaptureFrame?>? _lazyCaptureFrame;
    private readonly Func<OpenCvSharp.Rect, GameCaptureFrame?>? _roiCaptureFunc;
    private readonly Dictionary<OpenCvSharp.Rect, GameCaptureFrame> _cachedRois = new();
    private Mat? _frame;
    private bool _disposed;

    public Mat? Frame
    {
        get
        {
            if (_frame != null) return _frame;
            if (_lazyCaptureFrame != null)
            {
                var full = _lazyCaptureFrame.Value;
                if (full != null && !full.Frame.Empty())
                {
                    _frame = full.Frame;
                }
            }
            return _frame;
        }
    }

    public IntPtr Hwnd { get; }
    public Rectangle ClientRect { get; }
    public DateTime Timestamp { get; }
    public double MonotonicTimestampMs { get; }
    public OpenCvSharp.Size OriginalWindowSize { get; }
    public double DpiScale { get; }
    public bool IsHdr { get; }
    public bool IsValid { get; }

    public int Width => OriginalWindowSize.Width;
    public int Height => OriginalWindowSize.Height;

    /// <summary>
    /// 常规全帧构造器（保持旧接口兼容与测试调用）
    /// </summary>
    public CaptureContent(Mat frame, IntPtr hwnd, Rectangle clientRect)
    {
        _frame = frame;
        Hwnd = hwnd;
        ClientRect = clientRect;
        Timestamp = DateTime.UtcNow;
        MonotonicTimestampMs = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        OriginalWindowSize = frame != null && !frame.Empty()
            ? new OpenCvSharp.Size(frame.Width, frame.Height)
            : new OpenCvSharp.Size(clientRect.Width, clientRect.Height);
        DpiScale = 1.0;
        IsHdr = false;
        IsValid = frame != null && !frame.Empty();
    }

    /// <summary>
    /// 高性能按需 GPU ROI 构造器：不提前拉取和映射全画幅，仅当触发器请求对应区域时执行 GPU 局部回读
    /// </summary>
    public CaptureContent(
        Func<GameCaptureFrame?>? frameFactory,
        Func<OpenCvSharp.Rect, GameCaptureFrame?>? roiCaptureFunc,
        IntPtr hwnd,
        Rectangle clientRect,
        OpenCvSharp.Size windowSize,
        double dpiScale = 1.0,
        bool isHdr = false)
    {
        _lazyCaptureFrame = frameFactory != null ? new Lazy<GameCaptureFrame?>(frameFactory) : null;
        _roiCaptureFunc = roiCaptureFunc;
        Hwnd = hwnd;
        ClientRect = clientRect;
        Timestamp = DateTime.UtcNow;
        MonotonicTimestampMs = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        OriginalWindowSize = windowSize;
        DpiScale = dpiScale;
        IsHdr = isHdr;
        IsValid = windowSize.Width > 0 && windowSize.Height > 0;
    }

    /// <summary>
    /// 获取指定 ROI 的帧数据。优先通过 GPU Direct3D11 CopySubresourceRegion 局部回读，
    /// 避免在 CPU/PCI-e 间映射传输全画幅 2560x1440 图像。
    /// </summary>
    public GameCaptureFrame? GetRoi(OpenCvSharp.Rect roi)
    {
        if (_disposed) return null;

        if (_cachedRois.TryGetValue(roi, out var cached))
        {
            return cached;
        }

        if (_roiCaptureFunc != null)
        {
            var roiFrame = _roiCaptureFunc(roi);
            if (roiFrame != null)
            {
                _cachedRois[roi] = roiFrame;
                return roiFrame;
            }
        }

        // Fallback：若无法直接通过 GPU 截取 ROI，但已有或可获取完整 Frame，则从 Frame 裁剪
        var full = Frame;
        if (full != null && !full.Empty())
        {
            int x = Math.Clamp(roi.X, 0, full.Width - 1);
            int y = Math.Clamp(roi.Y, 0, full.Height - 1);
            int w = Math.Clamp(roi.Width, 1, full.Width - x);
            int h = Math.Clamp(roi.Height, 1, full.Height - y);
            using var sub = new Mat(full, new OpenCvSharp.Rect(x, y, w, h));
            var fallbackFrame = new GameCaptureFrame(
                sub.Clone(),
                null,
                new OpenCvSharp.Rect(x, y, w, h),
                OriginalWindowSize,
                Timestamp,
                MonotonicTimestampMs,
                IsHdr,
                DpiScale);
            _cachedRois[roi] = fallbackFrame;
            return fallbackFrame;
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _frame?.Dispose();
        _frame = null;

        if (_lazyCaptureFrame != null && _lazyCaptureFrame.IsValueCreated)
        {
            _lazyCaptureFrame.Value?.Dispose();
        }

        foreach (var roi in _cachedRois.Values)
        {
            roi.Dispose();
        }
        _cachedRois.Clear();
    }
}
