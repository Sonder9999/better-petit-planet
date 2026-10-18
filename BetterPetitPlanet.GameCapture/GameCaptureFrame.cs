using System;
using System.Diagnostics;
using OpenCvSharp;
using Vanara.PInvoke;

namespace BetterPetitPlanet.GameCapture;

public sealed class GameCaptureFrame : IDisposable
{
    public GameCaptureFrame(
        Mat frame,
        RECT? captureRect = null,
        Rect? roiRect = null,
        OpenCvSharp.Size? originalWindowSize = null,
        DateTime? captureTimestamp = null,
        double? monotonicTimestampMs = null,
        bool isHdr = false,
        double dpiScale = 1.0)
    {
        Frame = frame;
        CaptureRect = captureRect;
        RoiRect = roiRect ?? (frame != null && !frame.Empty() ? new Rect(0, 0, frame.Width, frame.Height) : new Rect(0, 0, 0, 0));
        OriginalWindowSize = originalWindowSize ?? (frame != null && !frame.Empty() ? new OpenCvSharp.Size(frame.Width, frame.Height) : new OpenCvSharp.Size(0, 0));
        CaptureTimestamp = captureTimestamp ?? DateTime.UtcNow;
        MonotonicTimestampMs = monotonicTimestampMs ?? (Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        IsHdr = isHdr;
        DpiScale = dpiScale;
    }

    public Mat Frame { get; }

    public RECT? CaptureRect { get; }

    public Rect RoiRect { get; }

    public OpenCvSharp.Size OriginalWindowSize { get; }


    public DateTime CaptureTimestamp { get; }

    public double MonotonicTimestampMs { get; }

    public bool IsHdr { get; }

    public double DpiScale { get; }

    public void Dispose()
    {
        Frame?.Dispose();
        GC.SuppressFinalize(this);
    }
}

