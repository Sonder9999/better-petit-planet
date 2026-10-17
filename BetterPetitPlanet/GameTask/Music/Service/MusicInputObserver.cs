using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BetterPetitPlanet.GameTask.Music.Service;

public sealed class MusicInputObserver : IDisposable
{
    private readonly HookProc _callback;
    private IntPtr _hook;
    private readonly MusicPhysicalKeys _keys = new();
    public Func<bool>? CanObserve { get; set; }
    public event Action<string, string, double>? InputObserved;
    public event Action<Exception>? ObservationFailed;
    public string[] ActiveKeys => _keys.ActiveKeys;

    public MusicInputObserver() => _callback = OnKeyboard;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
    {
        try
        {
            if (code >= 0)
            {
                var info = Marshal.PtrToStructure<KeyboardData>(data);
                bool down = message.ToInt64() is 0x100 or 0x104;
                bool up = message.ToInt64() is 0x101 or 0x105;
                bool injected = IsInjected(info.Flags, info.ExtraInfo);
                if ((down || up) && !injected && (CanObserve?.Invoke() ?? false))
                {
                    string key = ((char)info.VirtualKey).ToString();
                    if (_keys.Apply(key, down, false))
                        InputObserved?.Invoke(key, down ? "down" : "up", MusicCaptureObserver.MonotonicMs);
                }
            }
        }
        catch (Exception ex)
        {
            ObservationFailed?.Invoke(ex);
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    public static bool IsInjected(uint flags, nuint marker) => (flags & 0x12) != 0 || marker == HardwareInputSimulator.MusicInputMarker;

    public void Clear()
    {
        foreach (var key in _keys.Clear())
            InputObserved?.Invoke(key, "up", MusicCaptureObserver.MonotonicMs);
    }

    public void Dispose()
    {
        Clear();
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData
    {
        public uint VirtualKey, ScanCode, Flags, Time;
        public nuint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
