using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Skins;

namespace BetterPetitPlanet.Test;

public class MusicEffectCompatibilityTest
{
    [Theory]
    [InlineData(1.25, 360)]
    [InlineData(1.5, 432)]
    [InlineData(2, 576)]
    public void LocalHoldCapture_UsesActualDisplayPixelDensity(double dpi, int pixels) => MusicRenderingTestHost.Run(() =>
    {
        var renderer = new HoldEffectRenderer();
        var bitmap = renderer.Render(new DrawingGroup(), new Rect(0, 0, 288, 288), 500, pixelsPerDip: dpi);
        Assert.NotNull(bitmap);
        Assert.Equal(pixels, bitmap.PixelWidth);
        Assert.Equal(pixels, bitmap.PixelHeight);
    });

    [Fact]
    public void CustomSkin_HoldTextureAndJudgePointRemainVisible() => MusicRenderingTestHost.Run(() =>
    {
        var texture = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
        texture.Freeze();
        var skin = new MusicSkin { IsCustom = true, HitFlowerBitmap = texture, NotePointBitmap = texture };
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            HoldEffectRenderer.DrawHoldingEffect(dc, 240, 240, 500, 960, new MusicOverlayLayoutConfig(), skin);
            PlayfieldTrackRenderer.DrawJudgePoints(dc, 960, 540, new MusicOverlayLayoutConfig(), skin);
        }
        var images = MusicReferenceEffectsTest.Descendants(visual.Drawing).OfType<ImageDrawing>().ToArray();
        Assert.True(images.Count(image => ReferenceEquals(image.ImageSource, texture)) >= 8);
    });

    [Fact]
    public void CustomSkin_KeepsConfiguredBurstDuration() => MusicRenderingTestHost.Run(() =>
    {
        var skin = new MusicSkin { IsCustom = true, HitEffectDurationMs = 900 };
        Assert.Equal(900, HitBurstEffectRenderer.Duration(skin));
        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();
        Assert.True(HitBurstEffectRenderer.DrawHitBurst(dc, 240, 240, 800, 960, new MusicOverlayLayoutConfig(), skin));
        Assert.False(HitBurstEffectRenderer.DrawHitBurst(dc, 240, 240, 901, 960, new MusicOverlayLayoutConfig(), skin));
    });

    [Fact]
    public void CustomBackground_ArchDoesNotRequireWingTexture() => MusicRenderingTestHost.Run(() =>
    {
        var texture = BitmapSource.Create(120, 120, 96, 96, PixelFormats.Bgra32, null, new byte[120 * 120 * 4], 480);
        texture.Freeze();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) PlayfieldTrackRenderer.DrawTracks(dc, 960, 540,
            new MusicOverlayLayoutConfig(), new MusicOverlayEffectsConfig { BackgroundEnabled = true },
            skin: new MusicSkin { IsCustom = true, BackgroundArchBitmap = texture });
        Assert.Contains(MusicReferenceEffectsTest.Descendants(visual.Drawing).OfType<ImageDrawing>(),
            drawing => ReferenceEquals(drawing.ImageSource, texture));
    });
}
