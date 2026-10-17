using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Rendering.Gpu;
using BetterPetitPlanet.MusicOverlay.Gpu;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Xunit;

namespace BetterPetitPlanet.Test;

public class MusicGpuSyncRibbonDeviceTest
{
    [Fact]
    public void ChordRendersWithContextFactoryAfterResizeDpiAndResourceRecreation()
    {
        // WARP exercises real D3D/D2D resource ownership without a visible HWND or
        // depending on a particular display adapter. EndDraw detects WrongFactory.
        D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport,
            new[] { Vortice.Direct3D.FeatureLevel.Level_11_0 }, out ID3D11Device device, out ID3D11DeviceContext immediate).CheckError();
        using var ownedDevice = device;
        using var ownedImmediate = immediate;
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        using var factory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        using var d2d = factory.CreateDevice(dxgi);
        using var context = d2d.CreateDeviceContext(DeviceContextOptions.None);
        using var target = context.CreateBitmap(new SizeI(960, 540), IntPtr.Zero, 0,
            new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm,
                Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
        using var renderer = new MusicGpuRenderer();
        var chart = new MusicChartIndex(new[]
        {
            new PerformanceEvent { Key = "A", TimeMs = 7000, Kind = MusicNoteKind.Tap },
            new PerformanceEvent { Key = "L", TimeMs = 7000, Kind = MusicNoteKind.Tap }
        });
        var snapshot = MusicRenderSnapshot.Empty with { PositionMs = 6200, HostMonotonicMs = 10000 };
        var config = new MusicOverlayConfig
        {
            Effects = new() { SyncLines = true, ShowBackgroundDeco = false, ShowKeyLabels = false, HitBursts = false, HoldGlow = false }
        };
        context.Target = target;
        try
        {
            renderer.OnDeviceCreated(context);
            Draw(960, 540, 96);
            Draw(800, 450, 144);
            renderer.OnDeviceDestroyed();
            renderer.OnDeviceCreated(context);
            Draw(960, 540, 96);
        }
        finally { context.Target = null; }

        void Draw(float width, float height, float dpi)
        {
            renderer.Publish(snapshot, chart, config);
            context.SetDpi(dpi, dpi);
            context.BeginDraw();
            context.Clear(new Color4(0, 0, 0, 0));
            var frame = new GpuOverlayFrame(context, width, height, dpi, 10000);
            renderer.Render(in frame);
            context.EndDraw().CheckError();
        }
    }
}
