using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BetterPetitPlanet.MusicOverlay.Gpu;

/// <summary>A separate Win32/GPU thread. Public updates never wait on rendering or the UI dispatcher.</summary>
public sealed class GpuOverlayHost : IDisposable
{
    private sealed record Bounds(int X, int Y, int Width, int Height);
    private readonly IGpuOverlayContent _content;
    private readonly nint _externalHwnd;
    private readonly AutoResetEvent _wakeEvent = new(false);
    private readonly Thread _thread;
    private Bounds _bounds = new(100, 100, 960, 540);
    private Bounds? _appliedBounds;
    private volatile bool _visible, _stopping;
    private bool _shown, _contentCreated;
    private int _recreate;
    private nint _hwnd;
    private Exception? _failure;
    private CompositionDevice? _device;
    private readonly FrameStatistics _statistics = new();
    private readonly NativeWindow.WindowProc _windowProc;
    public nint Handle => Interlocked.CompareExchange(ref _hwnd, 0, 0);
    public Exception? Failure => Volatile.Read(ref _failure);
    public GpuOverlayStatistics Statistics => _statistics.Capture();

    public GpuOverlayHost(IGpuOverlayContent content, nint externalHwnd = 0)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _externalHwnd = externalHwnd;
        if (externalHwnd != 0) Interlocked.Exchange(ref _hwnd, externalHwnd);
        _windowProc = WindowProc;
        _thread = new Thread(Run) { Name = externalHwnd != 0 ? "Music GPU preview" : "Music GPU composition", IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void SetBounds(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        var previous = Volatile.Read(ref _bounds);
        if (previous.X == x && previous.Y == y && previous.Width == width && previous.Height == height) return;
        Volatile.Write(ref _bounds, new Bounds(x, y, width, height));
        Wake();
    }
    public void Show() { _visible = true; Wake(); }
    public void Hide() { _visible = false; Wake(); }
    /// <summary>Schedules a full resource rebuild. Also used by the lightweight prototype recovery check.</summary>
    public void RequestDeviceRecreation() { Interlocked.Exchange(ref _recreate, 1); Wake(); }
    public void Wake()
    {
        _wakeEvent.Set();
        if (_externalHwnd == 0)
        {
            nint hwnd = Handle;
            if (hwnd != 0) NativeWindow.PostMessage(hwnd, NativeWindow.WakeMessage, 0, 0);
        }
    }

    private void Run()
    {
        string className = "BetterPetitPlanet.GpuOverlay." + Guid.NewGuid().ToString("N");
        nint instance = NativeWindow.GetModuleHandle(null);
        bool registered = false;
        nint previousDpi = NativeWindow.SetThreadDpiAwarenessContext(-4); // thread-local PerMonitorV2
        try
        {
            if (_externalHwnd == 0)
            {
                var cls = new NativeWindow.WindowClass
                {
                    Size = (uint)Marshal.SizeOf<NativeWindow.WindowClass>(),
                    Proc = _windowProc, Instance = instance, ClassName = className
                };
                if (NativeWindow.RegisterClassEx(ref cls) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                registered = true;
                var bounds = Volatile.Read(ref _bounds);
                nint hwnd = NativeWindow.CreateWindowEx(NativeWindow.ExStyle, className, "BetterPetitPlanet GPU music overlay",
                    NativeWindow.Popup, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0, 0, instance, 0);
                if (hwnd == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                Interlocked.Exchange(ref _hwnd, hwnd);
            }
            else
            {
                Interlocked.Exchange(ref _hwnd, _externalHwnd);
            }

            int failures = 0, stableFrames = 0;
            long recreations = 0;
            long lastRenderStartTimestamp = 0;
            long lastPresentReturnTimestamp = 0;
            nint wakeHandle = _wakeEvent.SafeWaitHandle.DangerousGetHandle();
            nint[] frameWaitHandles = new nint[2];
            frameWaitHandles[1] = wakeHandle;

            while (!_stopping)
            {
                while (NativeWindow.PeekMessage(out var message, 0, 0, 0, 1))
                {
                    if (message.Id == 0x0012) { _stopping = true; break; }
                    NativeWindow.TranslateMessage(ref message);
                    NativeWindow.DispatchMessage(ref message);
                }
                if (_stopping) break;
                if (_externalHwnd == 0) ApplyWindowState();

                if (!_visible)
                {
                    lastRenderStartTimestamp = 0;
                    lastPresentReturnTimestamp = 0;
                    NativeWindow.WaitForMessages([wakeHandle], uint.MaxValue);
                    continue;
                }

                nint hwnd = Handle;
                if (hwnd == 0) break;

                NativeWindow.GetClientRect(hwnd, out var client);
                int width = Math.Max(1, client.Right), height = Math.Max(1, client.Bottom);
                if (client.Right <= 1 || client.Bottom <= 1)
                {
                    lastRenderStartTimestamp = 0;
                    lastPresentReturnTimestamp = 0;
                    NativeWindow.WaitForMessages([wakeHandle], 100);
                    continue;
                }

                try
                {
                    if (Interlocked.Exchange(ref _recreate, 0) != 0)
                    {
                        DestroyDevice();
                        lastRenderStartTimestamp = 0;
                        lastPresentReturnTimestamp = 0;
                    }
                    float dpi = NativeWindow.GetDpiForWindow(hwnd);
                    if (dpi == 0) dpi = 96;
                    if (_device == null)
                    {
                        _device = new CompositionDevice(hwnd, width, height, dpi);
                        _contentCreated = true;
                        _content.OnDeviceCreated(_device.Context);
                        recreations++;
                    }
                    _device.Resize(width, height, dpi);

                    frameWaitHandles[0] = _device.FrameLatencyHandle;
                    frameWaitHandles[1] = wakeHandle;

                    long waitStart = Stopwatch.GetTimestamp();
                    uint wait = NativeWindow.WaitForMessages(frameWaitHandles, 100);
                    long waitEnd = Stopwatch.GetTimestamp();
                    double waitDurationMs = (waitEnd - waitStart) * 1000.0 / Stopwatch.Frequency;

                    if (wait != 0)
                    {
                        GpuWaitReason nonFrameReason = wait switch
                        {
                            1 => GpuWaitReason.WakeHandle,
                            2 => GpuWaitReason.WindowMessage,
                            258 => GpuWaitReason.Timeout,
                            _ => GpuWaitReason.Error
                        };
                        _statistics.RecordWaitOnly(nonFrameReason, waitDurationMs, waitEnd * 1000.0 / Stopwatch.Frequency);
                        continue;
                    }

                    long renderStart = waitEnd;
                    double renderStartIntervalMs = lastRenderStartTimestamp != 0
                        ? (renderStart - lastRenderStartTimestamp) * 1000.0 / Stopwatch.Frequency
                        : 0.0;
                    lastRenderStartTimestamp = renderStart;

                    _device.Begin();
                    var frame = new GpuOverlayFrame(_device.Context, width * 96f / dpi, height * 96f / dpi, dpi,
                        renderStart * 1000.0 / Stopwatch.Frequency);
                    _content.Render(in frame);
                    _device.End();

                    long renderEnd = Stopwatch.GetTimestamp();
                    double renderDurationMs = (renderEnd - renderStart) * 1000.0 / Stopwatch.Frequency;

                    long presentStart = renderEnd;
                    _device.Present();
                    long presentEnd = Stopwatch.GetTimestamp();
                    double presentCallDurationMs = (presentEnd - presentStart) * 1000.0 / Stopwatch.Frequency;

                    double presentReturnIntervalMs = lastPresentReturnTimestamp != 0
                        ? (presentEnd - lastPresentReturnTimestamp) * 1000.0 / Stopwatch.Frequency
                        : 0.0;
                    lastPresentReturnTimestamp = presentEnd;

                    _statistics.RecordFrame(
                        recreations,
                        waitDurationMs,
                        renderDurationMs,
                        presentCallDurationMs,
                        renderStartIntervalMs,
                        presentReturnIntervalMs,
                        presentEnd * 1000.0 / Stopwatch.Frequency);

                    if (++stableFrames >= 120) { failures = 0; stableFrames = 0; }
                }
                catch (Exception ex) when (IsDeviceLost(ex) && ++failures <= 3)
                {
                    stableFrames = 0;
                    lastRenderStartTimestamp = 0;
                    lastPresentReturnTimestamp = 0;
                    DestroyDevice();
                    // Bound recovery attempts and yield while the driver recovers.
                    NativeWindow.WaitForMessages([wakeHandle], 100);
                }
            }
        }
        catch (Exception ex) { Interlocked.CompareExchange(ref _failure, ex, null); }
        finally
        {
            try { DestroyDevice(); } catch (Exception ex) { Interlocked.CompareExchange(ref _failure, ex, null); }
            try { _content.Dispose(); } catch (Exception ex) { Interlocked.CompareExchange(ref _failure, ex, null); }
            nint hwnd = Interlocked.Exchange(ref _hwnd, 0);
            if (_externalHwnd == 0)
            {
                if (hwnd != 0) NativeWindow.DestroyWindow(hwnd);
                if (registered) NativeWindow.UnregisterClass(className, instance);
            }
            if (previousDpi != 0) NativeWindow.SetThreadDpiAwarenessContext(previousDpi);
        }
    }

    private void ApplyWindowState()
    {
        var bounds = Volatile.Read(ref _bounds);
        bool desiredVisible = _visible;
        if (_appliedBounds != bounds)
        {
            NativeWindow.Check(NativeWindow.SetWindowPos(Handle, NativeWindow.Topmost,
                bounds.X, bounds.Y, bounds.Width, bounds.Height, NativeWindow.PositionFlags));
            _appliedBounds = bounds;
        }
        if (_shown != desiredVisible)
        {
            if (desiredVisible)
                NativeWindow.Check(NativeWindow.SetWindowPos(Handle, NativeWindow.Topmost,
                    bounds.X, bounds.Y, bounds.Width, bounds.Height, NativeWindow.PositionFlags | 0x0040)); // SWP_SHOWWINDOW
            else
                NativeWindow.ShowWindow(Handle, 0); // SW_HIDE
            _shown = desiredVisible;
        }
    }

    private void DestroyDevice()
    {
        try
        {
            if (_contentCreated) { _contentCreated = false; _content.OnDeviceDestroyed(); }
        }
        finally { _device?.Dispose(); _device = null; }
    }

    private static bool IsDeviceLost(Exception ex) => unchecked((uint)ex.HResult) is
        0x887A0005 or 0x887A0006 or 0x887A0007 or 0x887A0020 or 0x8899000C;

    private nint WindowProc(nint hwnd, uint message, nuint wparam, nint lparam)
    {
        // Never let managed exceptions cross the unmanaged window-procedure boundary.
        try
        {
            switch (message)
            {
                case 0x0084: return -1; // HTTRANSPARENT; cross-process pass-through is checked by the separate-process prototype.
                case 0x0021: return 3; // MA_NOACTIVATE
                case 0x0014: return 1; // no GDI background erase
                case 0x0010: _stopping = true; return 0;
                case 0x007E: Interlocked.Exchange(ref _recreate, 1); return 0; // display change
                case 0x02E0: // DPI changed; use actual current DPI on next frame. Bounds stay tracker-owned.
                case NativeWindow.WakeMessage: return 0;
            }
            return NativeWindow.DefWindowProc(hwnd, message, wparam, lparam);
        }
        catch (Exception ex) { Interlocked.CompareExchange(ref _failure, ex, null); _stopping = true; return 0; }
    }

    public void Dispose()
    {
        _stopping = true;
        Wake();
        if (Thread.CurrentThread != _thread && !_thread.Join(TimeSpan.FromSeconds(5)))
            Interlocked.CompareExchange(ref _failure, new TimeoutException("GPU overlay shutdown exceeded five seconds; background thread retains ownership until it exits."), null);
        _wakeEvent.Dispose();
    }
}
