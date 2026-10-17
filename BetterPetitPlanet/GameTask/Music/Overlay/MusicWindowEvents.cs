using System;
using System.Runtime.InteropServices;

namespace BetterPetitPlanet.GameTask.Music.Overlay;

public sealed class MusicWindowEvents : IDisposable
{
    private readonly WinEventProc _callback;
    private IntPtr _hook;
    public event Action? Changed;

    public MusicWindowEvents()
    {
        _callback = (_, _, _, _, _, _, _) => Changed?.Invoke();
        try
        {
            // EVENT_SYSTEM_FOREGROUND (0x3) through EVENT_SYSTEM_MINIMIZEEND (0x17)
            // Covers foreground change, moves/resizes, and minimize/restore events
            _hook = SetWinEventHook(0x0003, 0x0017, IntPtr.Zero, _callback, 0, 0, 0);
        }
        catch
        {
            _hook = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private delegate void WinEventProc(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);
}
