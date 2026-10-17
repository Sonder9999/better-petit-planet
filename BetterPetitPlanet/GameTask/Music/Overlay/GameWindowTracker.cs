using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BetterPetitPlanet.Core.Process;

namespace BetterPetitPlanet.GameTask.Music.Overlay;

public sealed record GameWindowState(IntPtr Handle, bool Foreground, Rect Bounds, double Dpi)
{
    public bool Exists => Handle != IntPtr.Zero && Bounds.Width > 0 && Bounds.Height > 0;
    public Rect PhysicalBounds => Bounds;
    public Rect DipBounds => GameWindowTracker.PhysicalToDip(Bounds, Dpi);
}

public sealed class GameWindowTracker
{
    private readonly GameProcessDetector? _detector;
    private readonly Func<GameWindowState>? _sampleFunc;

    public GameWindowTracker(GameProcessDetector detector)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
    }

    public GameWindowTracker(Func<GameWindowState> sampleFunc)
    {
        _sampleFunc = sampleFunc ?? throw new ArgumentNullException(nameof(sampleFunc));
    }

    public GameWindowState Sample()
    {
        if (_sampleFunc != null) return _sampleFunc();
        if (_detector == null) return new(IntPtr.Zero, false, Rect.Empty, 96);

        var snapshot = _detector.GetCachedSnapshot();
        var handle = snapshot.MainWindowHandle;
        if (handle == IntPtr.Zero || !GetClientRect(handle, out var rect))
            return new(IntPtr.Zero, false, Rect.Empty, 96);

        var point = new NativePoint();
        ClientToScreen(handle, ref point);
        uint dpi = GetDpiForWindow(handle);
        if (dpi == 0) dpi = 96;

        bool iconic = IsIconic(handle);
        bool foreground = !iconic && _detector.IsGameForegroundCached(snapshot);
        var bounds = new Rect(point.X, point.Y, Math.Max(0, rect.Right), Math.Max(0, rect.Bottom));
        return new(handle, foreground, bounds, dpi);
    }

    public static Rect PhysicalToDip(Rect physicalRect, double dpi)
    {
        double scale = dpi > 0 ? dpi / 96.0 : 1.0;
        return new Rect(
            physicalRect.X / scale,
            physicalRect.Y / scale,
            physicalRect.Width / scale,
            physicalRect.Height / scale);
    }

    public static Rect DipToPhysical(Rect dipRect, double dpi)
    {
        double scale = dpi > 0 ? dpi / 96.0 : 1.0;
        return new Rect(
            dipRect.X * scale,
            dipRect.Y * scale,
            dipRect.Width * scale,
            dipRect.Height * scale);
    }

    public static bool ShouldUpdatePosition(Rect current, Rect target, double threshold = 1.0)
    {
        return Math.Abs(current.X - target.X) >= threshold ||
               Math.Abs(current.Y - target.Y) >= threshold ||
               Math.Abs(current.Width - target.Width) >= threshold ||
               Math.Abs(current.Height - target.Height) >= threshold;
    }

    public static void Position(Window window, GameWindowState state)
    {
        if (!state.Exists) return;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (hwnd == IntPtr.Zero) return;

        if (IsWindowVisible(hwnd) && GetWindowRect(hwnd, out var currentRect))
        {
            var currentBounds = new Rect(currentRect.Left, currentRect.Top, currentRect.Right - currentRect.Left, currentRect.Bottom - currentRect.Top);
            if (!ShouldUpdatePosition(currentBounds, state.Bounds, 1.0))
            {
                return;
            }
        }

        const uint flags = 0x0010 | 0x0040; // SWP_NOACTIVATE | SWP_SHOWWINDOW
        if (!SetWindowPos(hwnd, new IntPtr(-1), (int)state.Bounds.X, (int)state.Bounds.Y,
            (int)state.Bounds.Width, (int)state.Bounds.Height, flags))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 0)
            {
                throw new System.ComponentModel.Win32Exception(error);
            }
        }
    }

    public static void SetInteractive(Window window, bool interactive)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var style = GetWindowLongPtr(hwnd, -20).ToInt64(); // GWL_EXSTYLE = -20
        style |= 0x00000080L | 0x08000000L; // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
        style = interactive ? (style & ~0x00000020L) : (style | 0x00000020L); // WS_EX_TRANSPARENT
        SetWindowLongPtr(hwnd, -20, new IntPtr(style));
        // Strictly allow screen capture and recording (WDA_NONE = 0)
        SetWindowDisplayAffinity(hwnd, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
}
