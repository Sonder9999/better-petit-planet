using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Skins;
using BetterPetitPlanet.View.Controls.Music;

namespace BetterPetitPlanet.Test;

public class MusicReferenceSkinLifecycleTest
{
    [Fact]
    public void UnchartedReferenceSkin_RetainsManualAndAutomaticKeyHighlightWithoutBursts() => MusicRenderingTestHost.Run(() =>
    {
        var mockBitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Pbgra32, null, new byte[16 * 16 * 4], 16 * 4);
        var definition = new ReferenceSkinDefinition
        {
            Id = "furina",
            NoteTextures = ["hold.png", "tap.png"],
            JudgePointTexture = "point.png"
        };
        var images = new Dictionary<string, ReferenceSkinImage>
        {
            ["hold.png"] = new(mockBitmap, 16, 16),
            ["tap.png"] = new(mockBitmap, 16, 16),
            ["point.png"] = new(mockBitmap, 16, 16)
        };
        var skin = new MusicSkin
        {
            Id = "builtin:furina",
            NoteHoldBitmap = mockBitmap,
            NoteTapBitmap = mockBitmap,
            NotePointBitmap = mockBitmap,
            Reference = new ReferenceSkinResources(definition, images)
        };

        var control = new MusicPlayfieldControl(() => 0, _ => { }, _ => { })
        { Skin = skin, EffectsConfig = new MusicOverlayEffectsConfig { HitBursts = false, BackgroundEnabled = false } };
        var empty = MusicRenderSnapshot.Empty;
        byte[] Render(MusicRenderSnapshot snapshot)
        {
            control.RenderSnapshot(snapshot, 960, 540);
            var bitmap = new RenderTargetBitmap(960, 540, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render((DrawingVisual)VisualTreeHelper.GetChild(control, 2));
            var bytes = new byte[960 * 540 * 4]; bitmap.CopyPixels(bytes, 960 * 4, 0); return bytes;
        }
        var idle = Render(empty);
        var manual = Render(empty with { ActiveKeys = ["A"], ActiveManualKeys = ["A"] });
        var automatic = Render(empty with { ActiveKeys = ["A"], ActiveAutomaticKeys = ["A"] });
        Assert.False(idle.SequenceEqual(manual));
        Assert.False(manual.SequenceEqual(automatic));
    });

    [Fact]
    public void MissingAnimationDuringRender_FallsBackOnceAfterClosingDrawingContexts() => MusicRenderingTestHost.Run(() =>
    {
        var mockBitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Pbgra32, null, new byte[16 * 16 * 4], 16 * 4);
        var definition = new ReferenceSkinDefinition
        {
            Id = "missing-test",
            NoteTextures = ["hold.png", "tap.png"],
            JudgePointTexture = "point.png",
            Hit = new ReferenceHitAnimation { Url = "assets/fx/missing-test.png", FrameCount = 60, Fps = 60, DisplaySize = 576 },
        };
        var images = new Dictionary<string, ReferenceSkinImage>
        {
            ["hold.png"] = new(mockBitmap, 16, 16),
            ["tap.png"] = new(mockBitmap, 16, 16),
            ["point.png"] = new(mockBitmap, 16, 16)
        };
        var brokenSkin = new MusicSkin
        {
            Id = "builtin:missing-test",
            NoteHoldBitmap = mockBitmap,
            NoteTapBitmap = mockBitmap,
            NotePointBitmap = mockBitmap,
            Reference = new ReferenceSkinResources(definition, images)
        };
        var control = new MusicPlayfieldControl
        {
            Skin = brokenSkin,
            EffectsConfig = new MusicOverlayEffectsConfig { BackgroundEnabled = false }
        };
        control.SetChart(new PerformanceTimeline { Events = [new() { Key = "A", TimeMs = 3000 }] });
        var snapshot = MusicRenderSnapshot.Empty with { SessionId = "preview-fallback", PositionMs = 3100 };
        control.RenderSnapshot(snapshot, 960, 540);
        Assert.Same(MusicSkin.Default, control.Skin);
        control.RenderSnapshot(snapshot, 960, 540);
        Assert.Same(MusicSkin.Default, control.Skin);
    });
}
