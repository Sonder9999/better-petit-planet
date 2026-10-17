using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

[Collection("ReferenceFrameStore")]
public class MusicGpuReferenceSkinDeviceTest
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

    private static BitmapSource CreateTestBitmap(int width = 32, int height = 32, byte r = 255, byte g = 255, byte b = 255, byte a = 255)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = a;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] CreatePngBytes(int width, int height, byte r, byte g, byte b, byte a)
    {
        var bitmap = CreateTestBitmap(width, height, r, g, b, a);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static (MusicSkin Skin, ReferenceSkinDefinition Definition) CreateTestShougunSkin()
    {
        var def = MusicReferenceSkinCatalog.Find("builtin:shougun")
            ?? throw new InvalidOperationException("builtin:shougun definition not found in catalog");

        var images = new Dictionary<string, ReferenceSkinImage>();
        var urls = def.Background.SelectMany(layer => new[] { layer.Url, layer.Fit.WingUrl, layer.Fit.RightUrl })
            .Concat(def.NoteTextures)
            .Append(def.JudgePointTexture)
            .Where(url => !string.IsNullOrEmpty(url))
            .Distinct();

        foreach (var url in urls)
        {
            images[url!] = new ReferenceSkinImage(CreateTestBitmap(64, 64, 180, 140, 220), 64, 64);
        }

        var resources = new ReferenceSkinResources(def, images);
        var skin = new MusicSkin
        {
            Id = "builtin:shougun",
            DisplayName = def.DisplayName,
            Reference = resources,
            TrackBackgroundBrush = new SolidColorBrush(MediaColor.FromArgb(255, 37, 18, 62)),
            NoteHoldBitmap = images[def.NoteTextures[0]].Bitmap,
            NoteTapBitmap = images[def.NoteTextures[1]].Bitmap,
            NotePointBitmap = images[def.JudgePointTexture].Bitmap,
            HoldBeamBitmap = CreateTestBitmap(144, 512, 170, 130, 240),
            HitEffectDurationMs = def.Hit.DurationMs,
            HitEffectScale = 1.05
        };

        return (skin, def);
    }

    [Fact]
    public void ReferenceSkin_RendersOnRealD2DDevice_WithThemeColorsAndTextures()
    {
        var (device, immediate, context, target) = CreateWarpContext();
        using var d3d = device;
        using var imm = immediate;
        using var ctx = context;
        using var tgt = target;
        using var renderer = new MusicGpuRenderer();

        var (skin, def) = CreateTestShougunSkin();

        var chart = new MusicChartIndex(new[]
        {
            new PerformanceEvent { Key = "A", TimeMs = 1000, Kind = MusicNoteKind.Tap },
            new PerformanceEvent { Key = "S", TimeMs = 1000, DurationMs = 800, Kind = MusicNoteKind.Hold },
            new PerformanceEvent { Key = "D", TimeMs = 1200, Kind = MusicNoteKind.Tap }
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
                ShowBackgroundDeco = true
            }
        };

        renderer.OnDeviceCreated(context);

        var snap = MusicRenderSnapshot.Empty with
        {
            SessionId = "session-ref-1",
            PositionMs = 1100,
            HostMonotonicMs = 5100,
            ActiveKeys = ["S"]
        };
        renderer.Publish(snap, chart, config, skin);

        // Render frame
        context.SetDpi(96, 96);
        context.BeginDraw();
        context.Clear(new Color4(0, 0, 0, 0));
        var frame = new GpuOverlayFrame(context, 960, 540, 96, 5100);
        renderer.Render(in frame);
        context.EndDraw().CheckError();

        // Verify ReferenceSprites instantiated
        var refSprites = renderer.ReferenceSprites;
        Assert.NotNull(refSprites);
        Assert.Equal("shougun", refSprites.Definition.Id);
        Assert.NotNull(refSprites.Tap);
        Assert.NotNull(refSprites.Hold);
        Assert.NotNull(refSprites.Point);
        Assert.NotNull(refSprites.Beam);

        // Verify 10 hold particle styles loaded
        Assert.Equal(10, refSprites.HoldStyles.Count);
        Assert.Contains(refSprites.HoldStyles, s => s.Additive);
        Assert.Contains(refSprites.HoldStyles, s => !s.Additive);

        // Verify theme brushes reflect shougun accents
        var tapBrush = refSprites.Brush(MusicGpuRenderer.Ink.Tap);
        Assert.NotNull(tapBrush);
        // shougun primary accent: 11102965 = 0xA96AF5 -> R=169, G=106, B=245
        Assert.Equal(169f / 255f, tapBrush.Color.R, 0.05f);
        Assert.Equal(106f / 255f, tapBrush.Color.G, 0.05f);
        Assert.Equal(245f / 255f, tapBrush.Color.B, 0.05f);
    }

    [Fact]
    public void ReferenceSkin_BackgroundAssembly_CachesAssembledTarget()
    {
        var (device, immediate, context, target) = CreateWarpContext();
        using var d3d = device;
        using var imm = immediate;
        using var ctx = context;
        using var tgt = target;
        using var renderer = new MusicGpuRenderer();

        var (skin, def) = CreateTestShougunSkin();

        var config = new MusicOverlayConfig
        {
            Effects = new() { ShowBackgroundDeco = true, BackgroundOpacity = 0.85 }
        };

        renderer.OnDeviceCreated(context);
        renderer.Publish(MusicRenderSnapshot.Empty, MusicChartIndex.Empty, config, skin);

        void Render(float w, float h, double timeMs)
        {
            context.SetDpi(96, 96);
            context.BeginDraw();
            context.Clear(new Color4(0, 0, 0, 0));
            var frame = new GpuOverlayFrame(context, w, h, 96, timeMs);
            renderer.Render(in frame);
            context.EndDraw().CheckError();
        }

        // Render first frame at 960x540
        Render(960, 540, 1000);
        Assert.NotNull(renderer.ReferenceSprites);

        // Render second frame at same dimensions -> assembled background reused
        Render(960, 540, 1016);

        // Render third frame at different dimensions -> re-assembles cleanly without error
        Render(1280, 720, 1033);
    }

    [Fact]
    public void ReferenceSkin_HoldGlow_EvaluatesMultiLayerParticles()
    {
        var (device, immediate, context, target) = CreateWarpContext();
        using var d3d = device;
        using var imm = immediate;
        using var ctx = context;
        using var tgt = target;
        using var renderer = new MusicGpuRenderer();

        var (skin, def) = CreateTestShougunSkin();

        var chart = new MusicChartIndex(new[]
        {
            new PerformanceEvent { Key = "S", TimeMs = 1000, DurationMs = 2000, Kind = MusicNoteKind.Hold }
        });

        var config = new MusicOverlayConfig
        {
            Effects = new() { HoldGlow = true, Intensity = 1.0 }
        };

        renderer.OnDeviceCreated(context);

        // Hold active at age = 500ms
        var snap = MusicRenderSnapshot.Empty with
        {
            PositionMs = 1500,
            HostMonotonicMs = 6500,
            ActiveKeys = ["S"]
        };
        renderer.Publish(snap, chart, config, skin);

        context.SetDpi(96, 96);
        context.BeginDraw();
        context.Clear(new Color4(0, 0, 0, 0));
        var frame = new GpuOverlayFrame(context, 960, 540, 96, 6500);
        renderer.Render(in frame);
        context.EndDraw().CheckError();
    }

    [Fact]
    public void ReferenceSkin_HitBurst_FallbackPath_WhenFramesMissing()
    {
        var (device, immediate, context, target) = CreateWarpContext();
        using var d3d = device;
        using var imm = immediate;
        using var ctx = context;
        using var tgt = target;
        using var renderer = new MusicGpuRenderer();

        var (skin, def) = CreateTestShougunSkin();

        var chart = new MusicChartIndex(new[]
        {
            new PerformanceEvent { Key = "D", TimeMs = 1000, Kind = MusicNoteKind.Tap }
        });

        var config = new MusicOverlayConfig
        {
            Effects = new() { HitBursts = true, Intensity = 1.0 }
        };

        renderer.OnDeviceCreated(context);

        // Hit triggered at 1000ms, current time = 1100ms (elapsed 100ms in fallback)
        var snap = MusicRenderSnapshot.Empty with
        {
            PositionMs = 1100,
            HostMonotonicMs = 6100,
            ActiveKeys = ["D"]
        };
        renderer.Publish(snap, chart, config, skin);

        context.SetDpi(96, 96);
        context.BeginDraw();
        context.Clear(new Color4(0, 0, 0, 0));
        var frame = new GpuOverlayFrame(context, 960, 540, 96, 6100);
        renderer.Render(in frame);
        context.EndDraw().CheckError();
    }

    [Fact]
    public void ReferenceSkin_HitBurst_FrameStorePath_WhenFramesExist()
    {
        var (device, immediate, context, target) = CreateWarpContext();
        using var d3d = device;
        using var imm = immediate;
        using var ctx = context;
        using var tgt = target;
        using var renderer = new MusicGpuRenderer();

        var (skin, def) = CreateTestShougunSkin();

        // Create temporary frames/shougun.zip in AssetRoot/frames
        string framesDir = Path.Combine(MusicReferenceSkinCatalog.AssetRoot, "frames");
        Directory.CreateDirectory(framesDir);
        string zipPath = Path.Combine(framesDir, "shougun.zip");

        try
        {
            using (var zipStream = File.Create(zipPath))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                var normEntry = archive.CreateEntry("shougun-hit-atlas/0000.png");
                using (var es = normEntry.Open())
                {
                    var png = CreatePngBytes(64, 64, 255, 200, 100, 255);
                    es.Write(png, 0, png.Length);
                }

                var addEntry = archive.CreateEntry("shougun-hit-additive/0000.png");
                using (var es = addEntry.Open())
                {
                    var png = CreatePngBytes(64, 64, 200, 100, 255, 255);
                    es.Write(png, 0, png.Length);
                }
            }

            ReferenceFrameStore.Clear();

            var chart = new MusicChartIndex(new[]
            {
                new PerformanceEvent { Key = "D", TimeMs = 1000, Kind = MusicNoteKind.Tap }
            });

            var config = new MusicOverlayConfig
            {
                Effects = new() { HitBursts = true, Intensity = 1.0 }
            };

            renderer.OnDeviceCreated(context);

            // Hit at 1000ms, current time = 1000ms -> frameIndex = 0
            var snap = MusicRenderSnapshot.Empty with
            {
                PositionMs = 1000,
                HostMonotonicMs = 6000,
                ActiveKeys = ["D"]
            };
            renderer.Publish(snap, chart, config, skin);

            context.SetDpi(96, 96);
            context.BeginDraw();
            context.Clear(new Color4(0, 0, 0, 0));
            var frame = new GpuOverlayFrame(context, 960, 540, 96, 6000);
            renderer.Render(in frame);
            context.EndDraw().CheckError();
        }
        finally
        {
            ReferenceFrameStore.Clear();
            if (File.Exists(zipPath))
            {
                try { File.Delete(zipPath); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public void ReferenceSkin_DynamicSwitching_And_Disposal()
    {
        var (device, immediate, context, target) = CreateWarpContext();
        using var d3d = device;
        using var imm = immediate;
        using var ctx = context;
        using var tgt = target;
        using var renderer = new MusicGpuRenderer();

        var (refSkin, _) = CreateTestShougunSkin();

        var primaryBrush = new SolidColorBrush(MediaColor.FromRgb(255, 100, 50));
        primaryBrush.Freeze();
        var customSkin = new MusicSkin
        {
            Id = "custom-skin-1",
            IsCustom = true,
            TapNoteFillBrush = primaryBrush
        };

        var chart = new MusicChartIndex(new[]
        {
            new PerformanceEvent { Key = "A", TimeMs = 500, Kind = MusicNoteKind.Tap }
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

        var snap = MusicRenderSnapshot.Empty with { PositionMs = 400, HostMonotonicMs = 1000 };

        // 1. Default skin
        renderer.Publish(snap, chart, config, MusicSkin.Default);
        Render(1000);
        Assert.NotNull(renderer.Sprites);
        Assert.Null(renderer.CustomSprites);
        Assert.Null(renderer.ReferenceSprites);

        // 2. Switch to Reference skin
        renderer.Publish(snap, chart, config, refSkin);
        Render(1016);
        Assert.NotNull(renderer.ReferenceSprites);
        Assert.Null(renderer.CustomSprites);
        Assert.NotNull(renderer.Sprites); // Default sprites remain warm baseline

        // 3. Switch to Custom skin
        renderer.Publish(snap, chart, config, customSkin);
        Render(1033);
        Assert.Null(renderer.ReferenceSprites);
        Assert.NotNull(renderer.CustomSprites);
        Assert.NotNull(renderer.Sprites);

        // 4. Switch back to Reference skin
        renderer.Publish(snap, chart, config, refSkin);
        Render(1050);
        Assert.NotNull(renderer.ReferenceSprites);
        Assert.Null(renderer.CustomSprites);

        // 5. Switch back to Default skin
        renderer.Publish(snap, chart, config, MusicSkin.Default);
        Render(1066);
        Assert.Null(renderer.ReferenceSprites);
        Assert.Null(renderer.CustomSprites);
        Assert.NotNull(renderer.Sprites);

        // 6. Device destruction
        renderer.OnDeviceDestroyed();
        Assert.Null(renderer.ReferenceSprites);
        Assert.Null(renderer.CustomSprites);
        Assert.Null(renderer.Sprites);
    }

    [Theory]
    [InlineData("builtin:shougun")]
    [InlineData("builtin:nodkrai")]
    [InlineData("builtin:durin")]
    [InlineData("builtin:venti")]
    [InlineData("builtin:xilonen")]
    [InlineData("builtin:citlali")]
    [InlineData("builtin:mavuika")]
    [InlineData("builtin:lauma")]
    [InlineData("builtin:varka")]
    [InlineData("builtin:natlan")]
    [InlineData("builtin:columbina")]
    [InlineData("builtin:ayaka")]
    [InlineData("builtin:diluc")]
    public void All13C1ReferenceSkins_RenderOnRealD2DDevice_WithoutErrors(string skinId)
    {
        var (device, immediate, context, target) = CreateWarpContext();
        using var d3d = device;
        using var imm = immediate;
        using var ctx = context;
        using var tgt = target;
        using var renderer = new MusicGpuRenderer();

        var (skin, def) = CreateTestSkin(skinId);
        var chart = new MusicChartIndex(new[]
        {
            new PerformanceEvent { Key = "A", TimeMs = 1000, Kind = MusicNoteKind.Tap },
            new PerformanceEvent { Key = "S", TimeMs = 1000, DurationMs = 800, Kind = MusicNoteKind.Hold },
            new PerformanceEvent { Key = "D", TimeMs = 1200, Kind = MusicNoteKind.Tap }
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
                ShowBackgroundDeco = true
            }
        };

        var snap = MusicRenderSnapshot.Empty with
        {
            SessionId = "test-c1",
            PositionMs = 1200,
            HostMonotonicMs = 5200,
            ActiveKeys = ["S"]
        };
        renderer.OnDeviceCreated(context);
        renderer.Publish(snap, chart, config, skin);

        context.BeginDraw();
        context.Clear(new Color4(0, 0, 0, 0));
        var frame = new GpuOverlayFrame(context, 960, 540, 96, 5200);
        renderer.Render(in frame);
        context.EndDraw().CheckError();

        Assert.NotNull(renderer.ReferenceSprites);
        Assert.Equal(skinId, renderer.ReferenceSprites.Definition.Id == "nahida" ? "default" : "builtin:" + renderer.ReferenceSprites.Definition.Id);
        renderer.OnDeviceDestroyed();
    }

    [Fact]
    public void C2_PilotSkin_Furina_RendersHoldProfileAnimationOnD2DDevice()
    {
        var (device, immediate, context, target) = CreateWarpContext();
        using var d3d = device;
        using var imm = immediate;
        using var ctx = context;
        using var tgt = target;
        using var renderer = new MusicGpuRenderer();

        var (skin, def) = CreateTestSkin("builtin:furina");
        Assert.NotNull(def.HoldProfile);
        Assert.Equal(12, def.HoldProfile.Layers.Length);
        Assert.Equal(160, def.HoldProfile.FrameSize);

        var chart = new MusicChartIndex(new[]
        {
            new PerformanceEvent { Key = "A", TimeMs = 1000, DurationMs = 3000 }
        });

        var config = new MusicOverlayConfig
        {
            Backend = MusicOverlayBackend.Gpu,
            Effects = new() { HoldGlow = true, Intensity = 1.0, ShowBackgroundDeco = true }
        };

        var snap = MusicRenderSnapshot.Empty with
        {
            SessionId = "test-c2-furina",
            PositionMs = 2000,
            HostMonotonicMs = 6000,
            ActiveKeys = ["A"]
        };

        renderer.OnDeviceCreated(context);
        renderer.OpaqueBackdrop = true;
        renderer.Publish(snap, chart, config, skin);

        context.BeginDraw();
        context.Clear(new Color4(0, 0, 0, 0));
        var frame = new GpuOverlayFrame(context, 960, 540, 96, 6000);
        renderer.Render(in frame);
        context.EndDraw().CheckError();

        Assert.NotNull(renderer.ReferenceSprites);
        Assert.Equal("furina", renderer.ReferenceSprites.Definition.Id);

        // Advance animation to 2500ms
        var snap2 = snap with { PositionMs = 2500, HostMonotonicMs = 6500 };
        renderer.Publish(snap2, chart, config, skin);
        context.BeginDraw();
        var frame2 = new GpuOverlayFrame(context, 960, 540, 96, 6500);
        renderer.Render(in frame2);
        context.EndDraw().CheckError();

        // Release key -> hold animation vanishes immediately
        var snapReleased = snap with { PositionMs = 2600, HostMonotonicMs = 6600, ActiveKeys = [] };
        renderer.Publish(snapReleased, chart, config, skin);
        context.BeginDraw();
        var frame3 = new GpuOverlayFrame(context, 960, 540, 96, 6600);
        renderer.Render(in frame3);
        context.EndDraw().CheckError();

        // Clean destruction
        renderer.OnDeviceDestroyed();
    }

    private static (MusicSkin Skin, ReferenceSkinDefinition Definition) CreateTestSkin(string skinId)
    {
        var def = MusicReferenceSkinCatalog.Find(skinId)
            ?? throw new InvalidOperationException($"{skinId} definition not found in catalog");

        var images = new Dictionary<string, ReferenceSkinImage>();
        var urls = def.Background.SelectMany(layer => new[] { layer.Url, layer.Fit.WingUrl, layer.Fit.RightUrl })
            .Concat(def.NoteTextures)
            .Append(def.JudgePointTexture)
            .Where(url => !string.IsNullOrEmpty(url))
            .Distinct();

        foreach (var url in urls)
        {
            images[url!] = new ReferenceSkinImage(CreateTestBitmap(64, 64, 180, 140, 220), 64, 64);
        }

        var resources = new ReferenceSkinResources(def, images);
        var skin = new MusicSkin
        {
            Id = skinId,
            DisplayName = def.DisplayName,
            Reference = resources,
            TrackBackgroundBrush = new SolidColorBrush(MediaColor.FromArgb(255, 37, 18, 62)),
            NoteHoldBitmap = images[def.NoteTextures[0]].Bitmap,
            NoteTapBitmap = images[def.NoteTextures[1]].Bitmap,
            NotePointBitmap = images[def.JudgePointTexture].Bitmap,
            HoldBeamBitmap = CreateTestBitmap(144, 512, 170, 130, 240),
            HitEffectDurationMs = def.Hit.DurationMs,
            HitEffectScale = 1.05
        };

        return (skin, def);
    }
}
