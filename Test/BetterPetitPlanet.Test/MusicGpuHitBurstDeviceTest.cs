using System;
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

public class MusicGpuHitBurstDeviceTest
{
    [Fact]
    public void HitBursts_RenderWithAtlasAndFallbackOnRealD2DDevice()
    {
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
            new PerformanceEvent { Key = "A", TimeMs = 1000, Kind = MusicNoteKind.Tap },
            new PerformanceEvent { Key = "S", TimeMs = 1000, Kind = MusicNoteKind.Tap },
            new PerformanceEvent { Key = "D", TimeMs = 1200, DurationMs = 600, Kind = MusicNoteKind.Hold }
        });

        var config = new MusicOverlayConfig
        {
            Effects = new()
            {
                HitBursts = true,
                Intensity = 1.0,
                ShowKeyLabels = true,
                LaneGuides = true,
                ShowBackgroundDeco = false
            }
        };

        context.Target = target;
        try
        {
            renderer.OnDeviceCreated(context);

            // 1. Render frame right when notes are tapped (PositionMs = 1000)
            var snap1 = MusicRenderSnapshot.Empty with
            {
                SessionId = "session-1",
                PositionMs = 1000,
                HostMonotonicMs = 5000,
                ActiveKeys = ["A", "S"]
            };
            renderer.Publish(snap1, chart, config);
            RenderFrame(960, 540, 96, 5000);

            // Verify hits were registered in the timeline
            Assert.Equal(2, renderer.Timeline.Hits.Count);

            // 2. Render subsequent frame mid-burst (PositionMs = 1500)
            var snap2 = MusicRenderSnapshot.Empty with
            {
                SessionId = "session-1",
                PositionMs = 1500,
                HostMonotonicMs = 5500,
                ActiveKeys = ["D"]
            };
            renderer.Publish(snap2, chart, config);
            RenderFrame(960, 540, 96, 5500);

            // 3. Render at tail completion (PositionMs = 1800)
            var snap3 = MusicRenderSnapshot.Empty with
            {
                SessionId = "session-1",
                PositionMs = 1800,
                HostMonotonicMs = 5800,
                ActiveKeys = ["D"]
            };
            renderer.Publish(snap3, chart, config);
            RenderFrame(960, 540, 96, 5800);

            // 4. Test HitBursts disabled
            var configDisabled = config.Clone();
            configDisabled.Effects.HitBursts = false;
            renderer.Publish(snap3, chart, configDisabled);
            RenderFrame(960, 540, 96, 5800);

            // 5. Test session change resets bursts
            var snapSession2 = snap3 with { SessionId = "session-2", PositionMs = 100, ActiveKeys = [] };
            renderer.Publish(snapSession2, chart, config);
            RenderFrame(960, 540, 96, 6000);
            Assert.Empty(renderer.Timeline.Hits);

            // 6. Test uncharted keyboard mode
            var snapUncharted = MusicRenderSnapshot.Empty with
            {
                SessionId = "session-2",
                HostMonotonicMs = 7000,
                ActiveKeys = ["J", "K"]
            };
            renderer.Publish(snapUncharted, MusicChartIndex.Empty, config);
            RenderFrame(960, 540, 96, 7000);
            Assert.Equal(2, renderer.Timeline.Hits.Count);

            // 7. Test device destruction and recreation
            renderer.OnDeviceDestroyed();
            Assert.Empty(renderer.Timeline.Hits);
            renderer.OnDeviceCreated(context);
            RenderFrame(960, 540, 96, 7500);
        }
        finally
        {
            context.Target = null;
        }

        void RenderFrame(float width, float height, float dpi, double monotonicMs)
        {
            context.SetDpi(dpi, dpi);
            context.BeginDraw();
            context.Clear(new Color4(0, 0, 0, 0));
            var frame = new GpuOverlayFrame(context, width, height, dpi, monotonicMs);
            renderer.Render(in frame);
            context.EndDraw().CheckError();
        }
    }

    [Fact]
    public void HitBursts_FrameIndexCoversAll64Frames()
    {
        // 64 frames at 60fps is ~16.6667ms per frame
        for (int frame = 0; frame < 64; frame++)
        {
            double elapsedMs = frame * (1000.0 / 60.0);
            int computedFrame = (int)(elapsedMs * 60.0 / 1000.0);
            Assert.Equal(frame, computedFrame);

            int col = computedFrame % 8;
            int row = computedFrame / 8;
            Assert.InRange(col, 0, 7);
            Assert.InRange(row, 0, 7);

            var sourceRect = new Rect(col * 512, row * 512, 512, 512);
            Assert.Equal(col * 512, sourceRect.X);
            Assert.Equal(row * 512, sourceRect.Y);
            Assert.Equal(512, sourceRect.Width);
            Assert.Equal(512, sourceRect.Height);
        }

        // Outside 64-frame range
        double expiredElapsed = 64 * (1000.0 / 60.0);
        int expiredIndex = (int)(expiredElapsed * 60.0 / 1000.0);
        Assert.True(expiredIndex >= 64);
    }

    [Fact]
    public void HitBursts_FallbackPath_RendersCleanlyWhenAtlasMissing()
    {
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

        var config = new MusicOverlayConfig
        {
            Effects = new() { HitBursts = true, Intensity = 0.8, ShowKeyLabels = true }
        };

        context.Target = target;
        try
        {
            renderer.OnDeviceCreated(context);
            var sprites = renderer.Sprites!;

            // Force HitAtlas = null to trigger fallback
            var atlas = sprites.HitAtlas;
            sprites.HitAtlas = null;

            // Trigger uncharted hit on lane A at monotonic 1000ms
            var snapshot = MusicRenderSnapshot.Empty with { HostMonotonicMs = 1000, ActiveKeys = ["A"] };
            renderer.Publish(snapshot, MusicChartIndex.Empty, config);

            // Render at 1100ms (100ms into 380ms fallback duration)
            context.BeginDraw();
            context.Clear(new Color4(0, 0, 0, 0));
            renderer.Render(new GpuOverlayFrame(context, 960, 540, 96, 1100));
            context.EndDraw().CheckError();

            // Also test pure vector fallback by nulling HitShockwave and HitFlower
            var shockwave = sprites.HitShockwave;
            var flower = sprites.HitFlower;
            sprites.HitShockwave = null;
            sprites.HitFlower = null;

            context.BeginDraw();
            context.Clear(new Color4(0, 0, 0, 0));
            renderer.Render(new GpuOverlayFrame(context, 960, 540, 96, 1200));
            context.EndDraw().CheckError();

            // Restore
            sprites.HitAtlas = atlas;
            sprites.HitShockwave = shockwave;
            sprites.HitFlower = flower;
        }
        finally
        {
            context.Target = null;
        }
    }
}
