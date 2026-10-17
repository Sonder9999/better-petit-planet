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

public class MusicGpuHoldAndKeyFeedbackDeviceTest
{
    private static (ID3D11Device device, ID3D11DeviceContext immediate, ID2D1DeviceContext context, ID2D1Bitmap1 target) CreateDeviceContext(int width = 960, int height = 540)
    {
        D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport,
            new[] { Vortice.Direct3D.FeatureLevel.Level_11_0 }, out ID3D11Device device, out ID3D11DeviceContext immediate).CheckError();
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        using var factory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        var d2d = factory.CreateDevice(dxgi);
        var context = d2d.CreateDeviceContext(DeviceContextOptions.None);
        var target = context.CreateBitmap(new SizeI(width, height), IntPtr.Zero, 0,
            new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm,
                Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
        context.Target = target;
        return (device, immediate, context, target);
    }

    [Fact]
    public void HoldParticles_UploadAll10StylesWithDeduplication()
    {
        var (device, immediate, context, target) = CreateDeviceContext();
        using (device)
        using (immediate)
        using (context)
        using (target)
        using (var renderer = new MusicGpuRenderer())
        {
            renderer.OnDeviceCreated(context);
            Assert.NotNull(renderer.Sprites);
            Assert.Equal(10, renderer.Sprites.HoldLayers.Count);

            // All 10 styles from NahidaHoldParticles.Styles are loaded
            for (int i = 0; i < 10; i++)
            {
                Assert.NotNull(renderer.Sprites.HoldLayers[i]);
            }

            // Layer 5 and Layer 6 share identical texture ("ring") and color (0x78e3c5) -> shared bitmap
            Assert.Same(renderer.Sprites.HoldLayers[5], renderer.Sprites.HoldLayers[6]);

            // Other layers have different styles/colors and are distinct
            Assert.NotSame(renderer.Sprites.HoldLayers[0], renderer.Sprites.HoldLayers[1]);
            Assert.NotSame(renderer.Sprites.HoldLayers[1], renderer.Sprites.HoldLayers[2]);
            Assert.NotSame(renderer.Sprites.HoldLayers[3], renderer.Sprites.HoldLayers[4]);
            Assert.NotSame(renderer.Sprites.HoldLayers[4], renderer.Sprites.HoldLayers[5]);
        }
    }

    [Fact]
    public void HoldParticles_RenderDuringHoldingNoteOnRealDevice()
    {
        var (device, immediate, context, target) = CreateDeviceContext();
        using (device)
        using (immediate)
        using (context)
        using (target)
        using (var renderer = new MusicGpuRenderer())
        {
            renderer.OnDeviceCreated(context);

            var chart = new MusicChartIndex(new[]
            {
                new PerformanceEvent { Key = "A", TimeMs = 1000, DurationMs = 2000, Kind = MusicNoteKind.Hold },
                new PerformanceEvent { Key = "K", TimeMs = 1200, DurationMs = 1500, Kind = MusicNoteKind.Hold }
            });

            var config = new MusicOverlayConfig
            {
                Effects = new()
                {
                    HoldGlow = true,
                    HitBursts = false,
                    Intensity = 1.0,
                    ShowKeyLabels = true,
                    LaneGuides = true,
                    ShowBackgroundDeco = false
                }
            };

            // 1. Single lane holding (lane A)
            var snap1 = MusicRenderSnapshot.Empty with
            {
                SessionId = "session-hold",
                PositionMs = 1500, // ageMs = 500
                HostMonotonicMs = 5000,
                ActiveKeys = ["A"]
            };
            renderer.Publish(snap1, chart, config);
            RenderFrame(renderer, context, 960, 540, 96, 5000);

            // 2. Multi-lane simultaneous holding (lane A and lane K held simultaneously)
            var snapMulti = MusicRenderSnapshot.Empty with
            {
                SessionId = "session-hold",
                PositionMs = 1800, // A ageMs = 800, K ageMs = 600
                HostMonotonicMs = 5300,
                ActiveKeys = ["A", "K"]
            };
            renderer.Publish(snapMulti, chart, config);
            RenderFrame(renderer, context, 960, 540, 96, 5300);

            // 3. Completed hold expiration (PositionMs = 3100, beyond A's end of 3000)
            var snapExpired = MusicRenderSnapshot.Empty with
            {
                SessionId = "session-hold",
                PositionMs = 3100,
                HostMonotonicMs = 6600,
                ActiveKeys = ["A"]
            };
            renderer.Publish(snapExpired, chart, config);
            RenderFrame(renderer, context, 960, 540, 96, 6600);
        }
    }

    [Fact]
    public void HoldParticles_PrematureReleaseClearsImmediately()
    {
        var (device, immediate, context, target) = CreateDeviceContext();
        using (device)
        using (immediate)
        using (context)
        using (target)
        using (var renderer = new MusicGpuRenderer())
        {
            renderer.OnDeviceCreated(context);

            var chart = new MusicChartIndex(new[]
            {
                new PerformanceEvent { Key = "D", TimeMs = 1000, DurationMs = 2000, Kind = MusicNoteKind.Hold }
            });

            var config = new MusicOverlayConfig
            {
                Effects = new()
                {
                    HoldGlow = true,
                    Intensity = 1.0,
                    ShowKeyLabels = true
                }
            };

            // Frame 1: Key is held
            var snapHolding = MusicRenderSnapshot.Empty with
            {
                SessionId = "session-premature",
                PositionMs = 1400,
                HostMonotonicMs = 5000,
                ActiveKeys = ["D"]
            };
            renderer.Publish(snapHolding, chart, config);
            RenderFrame(renderer, context, 960, 540, 96, 5000);

            // Frame 2: Key is prematurely released at PositionMs = 1450
            var snapReleased = MusicRenderSnapshot.Empty with
            {
                SessionId = "session-premature",
                PositionMs = 1450,
                HostMonotonicMs = 5050,
                ActiveKeys = [] // released!
            };
            renderer.Publish(snapReleased, chart, config);
            RenderFrame(renderer, context, 960, 540, 96, 5050);
        }
    }

    [Fact]
    public void HoldParticles_ConfigAndIntensityGuards()
    {
        var (device, immediate, context, target) = CreateDeviceContext();
        using (device)
        using (immediate)
        using (context)
        using (target)
        using (var renderer = new MusicGpuRenderer())
        {
            renderer.OnDeviceCreated(context);

            var chart = new MusicChartIndex(new[]
            {
                new PerformanceEvent { Key = "F", TimeMs = 1000, DurationMs = 1000, Kind = MusicNoteKind.Hold }
            });

            var snap = MusicRenderSnapshot.Empty with
            {
                SessionId = "session-guards",
                PositionMs = 1200,
                HostMonotonicMs = 5000,
                ActiveKeys = ["F"]
            };

            // 1. HoldGlow disabled
            var configDisabled = new MusicOverlayConfig
            {
                Effects = new() { HoldGlow = false, Intensity = 1.0 }
            };
            renderer.Publish(snap, chart, configDisabled);
            RenderFrame(renderer, context, 960, 540, 96, 5000);

            // 2. Intensity zero
            var configZeroIntensity = new MusicOverlayConfig
            {
                Effects = new() { HoldGlow = true, Intensity = 0.0 }
            };
            renderer.Publish(snap, chart, configZeroIntensity);
            RenderFrame(renderer, context, 960, 540, 96, 5000);
        }
    }

    [Fact]
    public void KeyFeedbackHalo_ManualAndAutoKeyDistinctionOnRealDevice()
    {
        var (device, immediate, context, target) = CreateDeviceContext();
        using (device)
        using (immediate)
        using (context)
        using (target)
        using (var renderer = new MusicGpuRenderer())
        {
            renderer.OnDeviceCreated(context);

            var config = new MusicOverlayConfig
            {
                Effects = new()
                {
                    Intensity = 1.0,
                    ShowKeyLabels = true,
                    HoldGlow = true
                }
            };

            // Manual key A and Auto key S active at the same time
            var snap = MusicRenderSnapshot.Empty with
            {
                SessionId = "session-feedback",
                PositionMs = 1000,
                HostMonotonicMs = 5000,
                ActiveKeys = ["A", "S"],
                ActiveManualKeys = ["A"],
                ActiveAutomaticKeys = ["S"]
            };
            renderer.Publish(snap, MusicChartIndex.Empty, config);
            RenderFrame(renderer, context, 960, 540, 96, 5000);

            // Intensity zero disables halo
            var configZero = config.Clone();
            configZero.Effects.Intensity = 0.0;
            renderer.Publish(snap, MusicChartIndex.Empty, configZero);
            RenderFrame(renderer, context, 960, 540, 96, 5000);
        }
    }

    [Fact]
    public void ActiveKeycaps_RenderHaloAndActivePillOnRealDevice()
    {
        var (device, immediate, context, target) = CreateDeviceContext();
        using (device)
        using (immediate)
        using (context)
        using (target)
        using (var renderer = new MusicGpuRenderer())
        {
            renderer.OnDeviceCreated(context);

            var config = new MusicOverlayConfig
            {
                Effects = new()
                {
                    ShowKeyLabels = true,
                    Intensity = 1.0
                }
            };

            // Lane J (manual) and K (auto) pressed; other lanes inactive
            var snapActive = MusicRenderSnapshot.Empty with
            {
                SessionId = "session-keycaps",
                PositionMs = 2000,
                HostMonotonicMs = 5000,
                ActiveKeys = ["J", "K"],
                ActiveManualKeys = ["J"],
                ActiveAutomaticKeys = ["K"]
            };
            renderer.Publish(snapActive, MusicChartIndex.Empty, config);
            RenderFrame(renderer, context, 960, 540, 96, 5000);

            // Releasing all keys reverts to inactive pill style
            var snapAllReleased = snapActive with { ActiveKeys = [], ActiveManualKeys = [], ActiveAutomaticKeys = [] };
            renderer.Publish(snapAllReleased, MusicChartIndex.Empty, config);
            RenderFrame(renderer, context, 960, 540, 96, 5100);
        }
    }

    [Fact]
    public void Lifecycle_DeviceLossAndRecreationPreservesHoldBitmaps()
    {
        var (device, immediate, context, target) = CreateDeviceContext();
        using (device)
        using (immediate)
        using (context)
        using (target)
        using (var renderer = new MusicGpuRenderer())
        {
            renderer.OnDeviceCreated(context);
            Assert.NotNull(renderer.Sprites);
            Assert.Equal(10, renderer.Sprites.HoldLayers.Count);

            // Destroy device
            renderer.OnDeviceDestroyed();
            Assert.Null(renderer.Sprites);

            // Recreate device
            renderer.OnDeviceCreated(context);
            Assert.NotNull(renderer.Sprites);
            Assert.Equal(10, renderer.Sprites.HoldLayers.Count);
            Assert.NotNull(renderer.Sprites.HoldLayers[0]);
            Assert.Same(renderer.Sprites.HoldLayers[5], renderer.Sprites.HoldLayers[6]);

            // Render on recreated device
            var config = new MusicOverlayConfig { Effects = new() { HoldGlow = true, Intensity = 1.0 } };
            var snap = MusicRenderSnapshot.Empty with { SessionId = "recreate", ActiveKeys = ["A"] };
            renderer.Publish(snap, MusicChartIndex.Empty, config);
            RenderFrame(renderer, context, 960, 540, 96, 6000);
        }
    }

    private static void RenderFrame(MusicGpuRenderer renderer, ID2D1DeviceContext context, float width, float height, float dpi, double monotonicMs)
    {
        context.SetDpi(dpi, dpi);
        context.BeginDraw();
        context.Clear(new Color4(0, 0, 0, 0));
        var frame = new GpuOverlayFrame(context, width, height, dpi, monotonicMs);
        renderer.Render(in frame);
        context.EndDraw().CheckError();
    }
}
