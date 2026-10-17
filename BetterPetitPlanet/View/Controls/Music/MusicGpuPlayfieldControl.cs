using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Rendering.Gpu;
using BetterPetitPlanet.GameTask.Music.Skins;
using BetterPetitPlanet.MusicOverlay.Gpu;

namespace BetterPetitPlanet.View.Controls.Music;

/// <summary>
/// GPU-backed settings preview playfield hosted in a Win32 child HWND.
/// Uses Direct2D 1.1 + DirectComposition with 0 CPU readbacks, running on a background thread.
/// Mouse hit-testing transparently passes through to WPF (WM_NCHITTEST -> HTTRANSPARENT),
/// allowing smooth scrolling of the parent settings ScrollViewer.
/// </summary>
public sealed class MusicGpuPlayfieldControl : HwndHost
{
    private readonly MusicGpuRenderer _renderer = new();
    private GpuOverlayHost? _host;
    private string? _registeredClassName;
    private MusicChartIndex? _chartIndex;
    private MusicOverlayConfig _config = new();
    private MusicSkin? _skin;
    private MusicRenderSnapshot _lastSnapshot = MusicRenderSnapshot.Empty;
    private NativeWindow.WindowProc? _wndProcDelegate;

    public MusicOverlayConfig Config
    {
        get => _config;
        set { _config = value ?? new(); PublishCurrent(); }
    }

    public MusicOverlayLayoutConfig LayoutConfig
    {
        get => _config.Layout;
        set { _config.Layout = value ?? new(); PublishCurrent(); }
    }

    public MusicOverlayEffectsConfig EffectsConfig
    {
        get => _config.Effects;
        set { _config.Effects = value ?? new(); PublishCurrent(); }
    }

    public int FallDurationMs
    {
        get => _config.FallDurationMs;
        set { _config.FallDurationMs = value; PublishCurrent(); }
    }

    public int VisualOffsetMs
    {
        get => _config.VisualOffsetMs;
        set { _config.VisualOffsetMs = value; PublishCurrent(); }
    }

    public MusicRenderSnapshot CurrentSnapshot => _lastSnapshot;
    public MusicChartIndex? ChartIndex => _chartIndex;
    public MusicSkin? Skin => _skin;

    private nint _childHwnd;
    private System.Windows.Controls.ScrollViewer? _parentScrollViewer;
    private bool _isClippedToEmpty;

    internal MusicGpuRenderer Renderer => _renderer;
    internal GpuOverlayHost? Host => _host;

    public MusicGpuPlayfieldControl()
    {
        _renderer.OpaqueBackdrop = true;
        IsVisibleChanged += OnIsVisibleChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        LayoutUpdated += OnLayoutUpdated;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HookScrollViewer();
        UpdateClippingRegion();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnhookScrollViewer();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        UpdateClippingRegion();
    }

    private void OnParentScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        UpdateClippingRegion();
    }

    private void HookScrollViewer()
    {
        if (_parentScrollViewer != null) return;
        _parentScrollViewer = FindVisualParent<System.Windows.Controls.ScrollViewer>(this);
        if (_parentScrollViewer != null)
        {
            _parentScrollViewer.ScrollChanged += OnParentScrollChanged;
        }
    }

    private void UnhookScrollViewer()
    {
        if (_parentScrollViewer != null)
        {
            _parentScrollViewer.ScrollChanged -= OnParentScrollChanged;
            _parentScrollViewer = null;
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent) return parent;
            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            _host?.Show();
            UpdateClippingRegion();
        }
        else
        {
            _host?.Hide();
        }
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _registeredClassName = "BetterPetitPlanet.GpuPreview." + Guid.NewGuid().ToString("N");
        nint instance = NativeWindow.GetModuleHandle(null);
        _wndProcDelegate = ChildWindowProc;

        var cls = new NativeWindow.WindowClass
        {
            Size = (uint)Marshal.SizeOf<NativeWindow.WindowClass>(),
            Proc = _wndProcDelegate,
            Instance = instance,
            ClassName = _registeredClassName
        };

        if (NativeWindow.RegisterClassEx(ref cls) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        int initialWidth = Math.Max(1, (int)ActualWidth);
        int initialHeight = Math.Max(1, (int)ActualHeight);

        nint childHwnd = NativeWindow.CreateWindowEx(
            0,
            _registeredClassName,
            "BetterPetitPlanet GPU Playfield Preview",
            NativeWindow.ChildWindowStyle,
            0, 0, initialWidth, initialHeight,
            hwndParent.Handle,
            0,
            instance,
            0);

        if (childHwnd == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        _childHwnd = childHwnd;
        _host = new GpuOverlayHost(_renderer, childHwnd);
        if (IsVisible)
        {
            _host.Show();
        }

        HookScrollViewer();
        UpdateClippingRegion();
        PublishCurrent();

        return new HandleRef(this, childHwnd);
    }

    private void UpdateClippingRegion()
    {
        if (_childHwnd == 0 || !IsVisible) return;

        HookScrollViewer();
        if (_parentScrollViewer == null)
        {
            NativeWindow.SetWindowRgn(_childHwnd, 0, true);
            _isClippedToEmpty = false;
            return;
        }

        try
        {
            double actualWidth = ActualWidth;
            double actualHeight = ActualHeight;
            if (actualWidth <= 0 || actualHeight <= 0) return;

            var transform = TransformToVisual(_parentScrollViewer);
            Rect controlRectInScrollViewer = transform.TransformBounds(new Rect(0, 0, actualWidth, actualHeight));
            Rect viewportRect = new Rect(0, 0, _parentScrollViewer.ViewportWidth, _parentScrollViewer.ViewportHeight);

            // Compute the intersection with the ScrollViewer's visible viewport
            controlRectInScrollViewer.Intersect(viewportRect);

            if (controlRectInScrollViewer.IsEmpty || controlRectInScrollViewer.Width <= 0 || controlRectInScrollViewer.Height <= 0)
            {
                // Completely scrolled outside the visible viewport
                if (!_isClippedToEmpty)
                {
                    nint emptyRgn = NativeWindow.CreateRectRgn(0, 0, 0, 0);
                    NativeWindow.SetWindowRgn(_childHwnd, emptyRgn, true);
                    NativeWindow.ShowWindow(_childHwnd, 0); // SW_HIDE
                    _isClippedToEmpty = true;
                }
                return;
            }

            if (_isClippedToEmpty)
            {
                NativeWindow.ShowWindow(_childHwnd, 4); // SW_SHOWNOACTIVATE
                _isClippedToEmpty = false;
            }

            Point localTopLeft = _parentScrollViewer.TranslatePoint(controlRectInScrollViewer.TopLeft, this);
            Point localBottomRight = _parentScrollViewer.TranslatePoint(controlRectInScrollViewer.BottomRight, this);

            uint dpi = NativeWindow.GetDpiForWindow(_childHwnd);
            if (dpi == 0) dpi = 96;
            double dpiScale = dpi / 96.0;

            int left = (int)Math.Max(0, Math.Floor(localTopLeft.X * dpiScale));
            int top = (int)Math.Max(0, Math.Floor(localTopLeft.Y * dpiScale));
            int right = (int)Math.Min(actualWidth * dpiScale, Math.Ceiling(localBottomRight.X * dpiScale));
            int bottom = (int)Math.Min(actualHeight * dpiScale, Math.Ceiling(localBottomRight.Y * dpiScale));

            if (right > left && bottom > top)
            {
                nint rgn = NativeWindow.CreateRectRgn(left, top, right, bottom);
                NativeWindow.SetWindowRgn(_childHwnd, rgn, true);
            }
            else
            {
                nint emptyRgn = NativeWindow.CreateRectRgn(0, 0, 0, 0);
                NativeWindow.SetWindowRgn(_childHwnd, emptyRgn, true);
            }
        }
        catch
        {
            // Ignore visual tree disconnection during layout transitions
        }
    }

    private nint ChildWindowProc(nint hwnd, uint message, nuint wparam, nint lparam)
    {
        switch (message)
        {
            case NativeWindow.WmNcHitTest:
                // HTTRANSPARENT: Mouse clicks and wheel scroll transparently pass to WPF ScrollViewer.
                return NativeWindow.HtTransparent;
            case NativeWindow.WmEraseBkgnd:
                // Prevent GDI background erase flicker.
                return 1;
        }
        return NativeWindow.DefWindowProc(hwnd, message, wparam, lparam);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        UnhookScrollViewer();
        _childHwnd = 0;
        _host?.Dispose();
        _host = null;
        if (hwnd.Handle != 0)
        {
            NativeWindow.DestroyWindow(hwnd.Handle);
        }
        if (_registeredClassName != null)
        {
            NativeWindow.UnregisterClass(_registeredClassName, NativeWindow.GetModuleHandle(null));
            _registeredClassName = null;
        }
    }

    public void SetChartIndex(MusicChartIndex? chartIndex)
    {
        _chartIndex = chartIndex;
        PublishCurrent();
    }

    public void SetSkin(MusicSkin? skin)
    {
        _skin = skin;
        PublishCurrent();
    }

    public void SetParameters(MusicOverlayLayoutConfig layout, MusicOverlayEffectsConfig effects, int fallDurationMs, int visualOffsetMs, MusicSkin? skin)
    {
        _config.Layout = layout ?? new();
        _config.Effects = effects ?? new();
        _config.FallDurationMs = fallDurationMs;
        _config.VisualOffsetMs = visualOffsetMs;
        _skin = skin;
        PublishCurrent();
    }

    public void UpdateSnapshot(MusicRenderSnapshot snapshot)
    {
        _lastSnapshot = snapshot ?? MusicRenderSnapshot.Empty;
        PublishCurrent();
    }

    public void ReleaseSkinResources()
    {
        _skin = null;
        PublishCurrent();
    }

    public void Reset()
    {
        _lastSnapshot = MusicRenderSnapshot.Empty;
        _chartIndex = null;
        PublishCurrent();
    }

    private void PublishCurrent()
    {
        _renderer.Publish(_lastSnapshot, _chartIndex ?? MusicChartIndex.Empty, _config, _skin);
        _host?.Wake();
    }
}
