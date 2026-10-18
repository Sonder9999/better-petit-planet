using OpenCvSharp;

namespace BetterPetitPlanet.GameCapture;

public interface IGameCapture : IDisposable
{
    public bool IsCapturing { get; }

    public void Start(nint hWnd, Dictionary<string, object>? settings = null);

    public GameCaptureFrame? Capture();

    public GameCaptureFrame? CaptureRoi(Rect roi)
    {
        var full = Capture();
        if (full == null) return null;
        try
        {
            if (full.Frame == null || full.Frame.Empty()) return full;
            int x = Math.Clamp(roi.X, 0, full.Frame.Width - 1);
            int y = Math.Clamp(roi.Y, 0, full.Frame.Height - 1);
            int w = Math.Clamp(roi.Width, 1, full.Frame.Width - x);
            int h = Math.Clamp(roi.Height, 1, full.Frame.Height - y);
            using var sub = new Mat(full.Frame, new Rect(x, y, w, h));
            return new GameCaptureFrame(
                sub.Clone(),
                full.CaptureRect,
                new Rect(x, y, w, h),
                full.OriginalWindowSize,
                full.CaptureTimestamp,
                full.MonotonicTimestampMs,
                full.IsHdr,
                full.DpiScale);
        }
        finally
        {
            full.Dispose();
        }
    }

    public void Stop();
}
