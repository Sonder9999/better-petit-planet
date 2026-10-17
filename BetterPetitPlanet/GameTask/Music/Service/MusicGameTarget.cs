using System;
using System.Diagnostics;
using BetterPetitPlanet.Core.Process;
using Vanara.PInvoke;

namespace BetterPetitPlanet.GameTask.Music.Service;

internal sealed class MusicGameTarget(GameProcessDetector detector)
{
    private IntPtr _handle;
    private double _nextRefresh;
    public IntPtr CurrentHandle
    {
        get
        {
            double now = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
            if (now >= _nextRefresh)
            {
                _handle = detector.FindMainWindowHandleCached();
                _nextRefresh = now + 1000;
            }
            return _handle;
        }
    }
    public bool IsForeground
    {
        get
        {
            var target = CurrentHandle;
            if (target == IntPtr.Zero || !User32.IsWindow(target) || User32.IsIconic(target)) return false;
            var foreground = User32.GetForegroundWindow();
            return (IntPtr)foreground == target || (IntPtr)User32.GetAncestor(foreground, User32.GetAncestorFlag.GA_ROOT) == target;
        }
    }
}
