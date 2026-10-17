using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("BetterPetitPlanet")]
[assembly: InternalsVisibleTo("BetterPetitPlanet.Test")]

namespace BetterPetitPlanet.MusicOverlay.Gpu;

internal static class NativeWindow
{
    internal const uint WakeMessage = 0x8001;
    internal const uint ExStyle = 0x00080000 | 0x00000020 | 0x08000000 | 0x00000080 | 0x00000008; // layered, transparent, no activate, tool, topmost
    internal const uint Popup = 0x80000000;
    internal const uint PositionFlags = 0x0010; // no activation
    internal const uint ChildWindowStyle = 0x54000000; // WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS
    internal const uint WmNcHitTest = 0x0084;
    internal const uint WmEraseBkgnd = 0x0014;
    internal const nint HtTransparent = -1;
    internal static readonly nint Topmost = -1;
    internal delegate nint WindowProc(nint hwnd, uint message, nuint wparam, nint lparam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        public uint Size, Style;
        public WindowProc Proc;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        public nint Hwnd;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }

    internal static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint DefWindowProc(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] internal static extern bool PeekMessage(out Message message, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] internal static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] internal static extern bool PostMessage(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint MsgWaitForMultipleObjectsEx(uint count, nint[] handles, uint milliseconds, uint wakeMask, uint flags);
    internal static uint WaitForMessages(nint[] handles, uint milliseconds)
    {
        uint result = MsgWaitForMultipleObjectsEx((uint)handles.Length, handles, milliseconds, 0x04FF, 0x0004);
        if (result == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        return result;
    }
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("gdi32.dll")] internal static extern nint CreateRectRgn(int x1, int y1, int x2, int y2);
    [DllImport("user32.dll")] internal static extern int SetWindowRgn(nint hwnd, nint hrgn, bool redraw);
}
