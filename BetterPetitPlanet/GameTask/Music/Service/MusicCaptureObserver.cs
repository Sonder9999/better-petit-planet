using System;
using System.Diagnostics;

namespace BetterPetitPlanet.GameTask.Music.Service;

public sealed class MusicCaptureObserver : ITaskTrigger
{
    public string Name => "音乐截图时效";
    public bool IsEnabled { get; set; } = true;
    public int Priority => 100;
    public event Action<double>? FrameObserved;

    public void OnCapture(CaptureContent content)
    {
        if (content.Frame != null && !content.Frame.Empty())
        {
            FrameObserved?.Invoke(MonotonicMs);
        }
    }

    public static double MonotonicMs => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
}
