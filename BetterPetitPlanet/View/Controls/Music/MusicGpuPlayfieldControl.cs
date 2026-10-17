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

    internal MusicGpuRenderer Renderer => _renderer;
    internal GpuOverlayHost? Host => _host;

    public MusicGpuPlayfieldControl()
    {
        _renderer.OpaqueBackdrop = true;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) _host?.Show();
        else _host?.Hide();
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

        _host = new GpuOverlayHost(_renderer, childHwnd);
        if (IsVisible)
        {
            _host.Show();
        }

        PublishCurrent();

        return new HandleRef(this, childHwnd);
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
