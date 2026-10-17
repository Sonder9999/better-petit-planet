using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Rendering.Gpu;
using BetterPetitPlanet.GameTask.Music.Skins;
using BetterPetitPlanet.MusicOverlay.Gpu;
using BetterPetitPlanet.View.Controls.Music;
using Xunit;

namespace BetterPetitPlanet.Test;

public class MusicGpuPreviewDeviceTest
{
    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, uint msg, nuint wparam, nint lparam);

    [Fact]
    public void GpuOverlayHost_WithChildHwnd_InitializesRendersAndDisposesCleanly()
    {
        nint instance = NativeWindow.GetModuleHandle(null);
        string parentClass = "BetterPetitPlanet.Test.Parent." + Guid.NewGuid().ToString("N");
        var parentCls = new NativeWindow.WindowClass
        {
            Size = (uint)Marshal.SizeOf<NativeWindow.WindowClass>(),
            Proc = NativeWindow.DefWindowProc,
            Instance = instance,
            ClassName = parentClass
        };
        ushort atom = NativeWindow.RegisterClassEx(ref parentCls);
        Assert.NotEqual(0, atom);

        nint parentHwnd = NativeWindow.CreateWindowEx(
            0, parentClass, "ParentTestWindow",
            0x80000000 | 0x10000000, // WS_POPUP | WS_VISIBLE
            100, 100, 640, 360, 0, 0, instance, 0);
        Assert.NotEqual(0, parentHwnd);

        string childClass = "BetterPetitPlanet.Test.Child." + Guid.NewGuid().ToString("N");
        var childCls = new NativeWindow.WindowClass
        {
            Size = (uint)Marshal.SizeOf<NativeWindow.WindowClass>(),
            Proc = NativeWindow.DefWindowProc,
            Instance = instance,
            ClassName = childClass
        };
        ushort childAtom = NativeWindow.RegisterClassEx(ref childCls);
        Assert.NotEqual(0, childAtom);

        nint childHwnd = NativeWindow.CreateWindowEx(
            0, childClass, "ChildTestWindow",
            NativeWindow.ChildWindowStyle,
            0, 0, 640, 360, parentHwnd, 0, instance, 0);
        Assert.NotEqual(0, childHwnd);

        try
        {
            using var renderer = new MusicGpuRenderer();
            var chart = new MusicChartIndex(new[]
            {
                new PerformanceEvent { Key = "A", TimeMs = 1000, Kind = MusicNoteKind.Tap },
                new PerformanceEvent { Key = "S", TimeMs = 1200, DurationMs = 800, Kind = MusicNoteKind.Hold }
            });
            var config = new MusicOverlayConfig
            {
                Effects = new() { ShowKeyLabels = true, HoldGlow = true, HitBursts = true }
            };
            var snapshot = MusicRenderSnapshot.Empty with
            {
                SessionId = "preview-session",
                PositionMs = 1100,
                IsPlaying = true,
                ActiveKeys = ["A", "S"]
            };
            renderer.Publish(snapshot, chart, config);

            using var host = new GpuOverlayHost(renderer, childHwnd);
            Assert.Equal(childHwnd, host.Handle);

            host.Show();

            // Wait briefly for at least one presentation frame or device initialization
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1500)
            {
                if (host.Failure != null) break;
                if (host.Statistics.DeviceRecreations >= 1) break;
                Thread.Sleep(20);
            }

            // Verify no unhandled failure occurred
            Assert.Null(host.Failure);
            Assert.True(host.Statistics.DeviceRecreations >= 1);

            // Test Wake and Hide
            host.Wake();
            host.Hide();
            host.Show();
        }
        finally
        {
            NativeWindow.DestroyWindow(childHwnd);
            NativeWindow.UnregisterClass(childClass, instance);
            NativeWindow.DestroyWindow(parentHwnd);
            NativeWindow.UnregisterClass(parentClass, instance);
        }
    }

    [Fact]
    public void MusicGpuPlayfieldControl_MountsInVisualTree_AndHandlesParameters() => MusicRenderingTestHost.Run(() =>
    {
        var control = new MusicGpuPlayfieldControl();
        Assert.Equal(MusicRenderSnapshot.Empty, control.CurrentSnapshot);
        Assert.Null(control.ChartIndex);
        Assert.Null(control.Skin);

        var chart = new MusicChartIndex(new[]
        {
            new PerformanceEvent { Key = "D", TimeMs = 500, Kind = MusicNoteKind.Tap }
        });
        var layout = new MusicOverlayLayoutConfig { Scale = 1.2 };
        var effects = new MusicOverlayEffectsConfig { ShowKeyLabels = true, HoldGlow = true };

        control.SetChartIndex(chart);
        control.SetParameters(layout, effects, 3500, 50, MusicSkin.Default);

        Assert.Same(chart, control.ChartIndex);
        Assert.Equal(1.2, control.LayoutConfig.Scale);
        Assert.True(control.EffectsConfig.ShowKeyLabels);
        Assert.Equal(3500, control.FallDurationMs);
        Assert.Equal(50, control.VisualOffsetMs);
        Assert.Same(MusicSkin.Default, control.Skin);

        var snap = MusicRenderSnapshot.Empty with { SessionId = "test-snap", PositionMs = 500, ActiveKeys = ["D"] };
        control.UpdateSnapshot(snap);
        Assert.Equal(snap, control.CurrentSnapshot);

        // Mount control in visual tree via HwndSource
        using var source = MusicRenderingTestHost.Mount(control);
        Assert.NotEqual(0, control.Handle);
        Assert.NotNull(control.Host);
        Assert.Equal(control.Handle, control.Host.Handle);

        // Verify WM_NCHITTEST returns HTTRANSPARENT (-1) for smooth ScrollViewer mouse-wheel passing
        nint hitResult = SendMessage(control.Handle, NativeWindow.WmNcHitTest, 0, 0);
        Assert.Equal(NativeWindow.HtTransparent, hitResult);

        // Verify WM_ERASEBKGND returns 1 (no GDI erase flicker)
        nint eraseResult = SendMessage(control.Handle, NativeWindow.WmEraseBkgnd, 0, 0);
        Assert.Equal(1, eraseResult);

        // Test Reset
        control.Reset();
        Assert.Equal(MusicRenderSnapshot.Empty, control.CurrentSnapshot);
        Assert.Null(control.ChartIndex);

        // Test ReleaseSkinResources
        control.SetSkin(MusicSkin.Default);
        Assert.NotNull(control.Skin);
        control.ReleaseSkinResources();
        Assert.Null(control.Skin);
    });

    [Fact]
    public void MusicGpuPlayfieldControl_VisibilityToggling_ShowsAndHidesHost() => MusicRenderingTestHost.Run(() =>
    {
        var control = new MusicGpuPlayfieldControl();
        using var source = MusicRenderingTestHost.Mount(control);
        Assert.NotNull(control.Host);

        control.Visibility = Visibility.Collapsed;
        MusicRenderingTestHost.Drain();

        control.Visibility = Visibility.Visible;
        MusicRenderingTestHost.Drain();
    });
}
