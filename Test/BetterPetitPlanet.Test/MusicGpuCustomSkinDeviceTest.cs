using System;
using System.Windows.Media;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Overlay;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Rendering.Gpu;
using BetterPetitPlanet.GameTask.Music.Skins;
using BetterPetitPlanet.MusicOverlay.Gpu;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Xunit;
using MediaColor = System.Windows.Media.Color;

namespace BetterPetitPlanet.Test;

public class MusicGpuCustomSkinDeviceTest
{
    private static (ID3D11Device device, ID3D11DeviceContext immediate, ID2D1DeviceContext context, ID2D1Bitmap1 target) CreateWarpContext()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport,
            new[] { Vortice.Direct3D.FeatureLevel.Level_11_0 }, out ID3D11Device device, out ID3D11DeviceContext immediate).CheckError();
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        using var factory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        var d2d = factory.CreateDevice(dxgi);
        var context = d2d.CreateDeviceContext(DeviceContextOptions.None);
        var target = context.CreateBitmap(new SizeI(960, 540), IntPtr.Zero, 0,
            new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm,
                Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
        context.Target = target;
        return (device, immediate, context, target);
    }

    private static MusicSkin CreateTestCustomSkin(string id, MediaColor primaryColor)
    {
        var primaryBrush = new SolidColorBrush(primaryColor);
        primaryBrush.Freeze();
        var primaryPen = new Pen(primaryBrush, 2.0);
        primaryPen.Freeze();

        return new MusicSkin
        {
            Id = id,
            DisplayName = "Test Custom Skin " + id,
            IsCustom = true,
            TapNoteFillBrush = primaryBrush,
            TapNoteStrokePen = primaryPen,
            HoldBodyBrush = primaryBrush,
            JudgementLineGlowBrush = primaryBrush,
            JudgementLinePen = primaryPen,
            KeyAutoGlowBrush = primaryBrush,
            HitEffectDurationMs = 350.0,
            HitEffectScale = 1.2
        };
    }

    [Fact]
    public void IsGpuSupportedSkin_DistinguishesCustomAndBuiltin()
    {
        Assert.True(MusicOverlayPresenter.IsGpuSupportedSkin("default"));
        Assert.True(MusicOverlayPresenter.IsGpuSupportedSkin(null));
        Assert.True(MusicOverlayPresenter.IsGpuSupportedSkin(""));
        Assert.True(MusicOverlayPresenter.IsGpuSupportedSkin("my-custom-skin"));
        Assert.True(MusicOverlayPresenter.IsGpuSupportedSkin("cyberpunk-neon"));

        // All C1 and C2 catalog skins are supported on GPU
        Assert.True(MusicOverlayPresenter.IsGpuSupportedSkin("builtin:furina"));
        Assert.True(MusicOverlayPresenter.IsGpuSupportedSkin("builtin:neuvillette"));
        Assert.True(MusicOverlayPresenter.IsGpuSupportedSkin("builtin:zhongli"));
        Assert.False(MusicOverlayPresenter.IsGpuSupportedSkin("builtin:firefly"));
        Assert.False(MusicOverlayPresenter.IsGpuSupportedSkin("BUILTIN:UNKNOWN"));
        Assert.False(MusicOverlayPresenter.IsGpuSupportedSkin("builtin:unknown_fallback"));
    }

    [Fact]
    public void CustomSkin_RendersOnRealD2DDevice_WithCustomColorsAndProceduralEffects()
    {
        var (device, immediate, context, target) = CreateWarpContext();
        using var d3d = device;
        using var imm = immediate;
        using var ctx = context;
        using var tgt = target;
        using var renderer = new MusicGpuRenderer();

        var customSkin = CreateTestCustomSkin("custom-amber", MediaColor.FromRgb(255, 180, 50));

        var chart = new MusicChartIndex(new[]
        {
            new PerformanceEvent { Key = "A", TimeMs = 1000, Kind = MusicNoteKind.Tap },
            new PerformanceEvent { Key = "S", TimeMs = 1000, DurationMs = 800, Kind = MusicNoteKind.Hold }
        });

        var config = new MusicOverlayConfig
        {
            Effects = new()
            {
                HitBursts = true,
                HoldGlow = true,
                Intensity = 1.0,
                ShowKeyLabels = true,
                LaneGuides = true,
                ShowBackgroundDeco = false
            }
        };

        renderer.OnDeviceCreated(context);

        // 1. Initial state without custom skin uses default sprites
        Assert.Null(renderer.CustomSprites);

        // 2. Publish frame with custom skin
        var snap = MusicRenderSnapshot.Empty with
        {
            SessionId = "session-custom-1",
            PositionMs = 1200,
            HostMonotonicMs = 5200,
            ActiveKeys = ["S"]
        };
        renderer.Publish(snap, chart, config, customSkin);

        // Render frame
        context.SetDpi(96, 96);
        context.BeginDraw();
        context.Clear(new Color4(0, 0, 0, 0));
        var frame = new GpuOverlayFrame(context, 960, 540, 96, 5200);
        renderer.Render(in frame);
        context.EndDraw().CheckError();

        // 3. Verify custom sprites were instantiated while default sprites remain warm baseline
        Assert.NotNull(renderer.CustomSprites);
        Assert.NotNull(renderer.Sprites);
        Assert.Equal(350.0, renderer.CustomSprites.HitEffectDurationMs);
        Assert.Equal(1.2, renderer.CustomSprites.HitEffectScale);

        // 4. Verify custom brushes reflect custom skin colors
        var tapBrush = renderer.CustomSprites.Brush(MusicGpuRenderer.Ink.Tap);
        Assert.Equal(1.0f, tapBrush.Color.R, 0.05f); // 255/255
        Assert.Equal(180f / 255f, tapBrush.Color.G, 0.05f);
        Assert.Equal(50f / 255f, tapBrush.Color.B, 0.05f);
    }

    [Fact]
    public void CustomSkin_DynamicSwitching_BetweenDefaultAndMultipleCustomSkins()
    {
        var (device, immediate, context, target) = CreateWarpContext();
        using var d3d = device;
        using var imm = immediate;
        using var ctx = context;
        using var tgt = target;
        using var renderer = new MusicGpuRenderer();

        var skinA = CreateTestCustomSkin("skin-blue", MediaColor.FromRgb(50, 120, 255));
        var skinB = CreateTestCustomSkin("skin-crimson", MediaColor.FromRgb(255, 30, 80));

        var chart = new MusicChartIndex(new[]
        {
            new PerformanceEvent { Key = "D", TimeMs = 500, Kind = MusicNoteKind.Tap }
        });

        var config = new MusicOverlayConfig();
        renderer.OnDeviceCreated(context);

        void Render(double timeMs)
        {
            context.SetDpi(96, 96);
            context.BeginDraw();
            context.Clear(new Color4(0, 0, 0, 0));
            var frame = new GpuOverlayFrame(context, 960, 540, 96, timeMs);
            renderer.Render(in frame);
            context.EndDraw().CheckError();
        }

        // 1. Render default skin
        var snapDefault = MusicRenderSnapshot.Empty with { SessionId = "s1", PositionMs = 400, HostMonotonicMs = 1000 };
        renderer.Publish(snapDefault, chart, config, null);
        Render(1000);
        Assert.NotNull(renderer.Sprites);
        Assert.Null(renderer.CustomSprites);

        // 2. Switch to Skin A
        renderer.Publish(snapDefault, chart, config, skinA);
        Render(1016);
        Assert.NotNull(renderer.CustomSprites);
        Assert.NotNull(renderer.Sprites);
        var oldCustomSprites = renderer.CustomSprites;

        // 3. Switch to Skin B
        renderer.Publish(snapDefault, chart, config, skinB);
        Render(1033);
        Assert.NotNull(renderer.CustomSprites);
        Assert.NotSame(oldCustomSprites, renderer.CustomSprites);

        // 4. Switch back to Default skin
        renderer.Publish(snapDefault, chart, config, MusicSkin.Default);
        Render(1050);
        Assert.Null(renderer.CustomSprites);
        Assert.NotNull(renderer.Sprites);

        // 5. Test OnDeviceDestroyed cleanup
        renderer.OnDeviceDestroyed();
        Assert.Null(renderer.CustomSprites);
        Assert.Null(renderer.Sprites);
    }
}
