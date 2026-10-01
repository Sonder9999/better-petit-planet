using System;
using System.Drawing;
using OpenCvSharp;

namespace BetterPetitPlanet.GameTask;

public sealed class CaptureContent : IDisposable
{
    public Mat Frame { get; }
    public IntPtr Hwnd { get; }
    public Rectangle ClientRect { get; }
    public DateTime Timestamp { get; }

    public CaptureContent(Mat frame, IntPtr hwnd, Rectangle clientRect)
    {
        Frame = frame;
        Hwnd = hwnd;
        ClientRect = clientRect;
        Timestamp = DateTime.UtcNow;
    }

    public void Dispose()
    {
        Frame.Dispose();
    }
}
