using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Skins;

namespace BetterPetitPlanet.Test;

public class MusicReferenceBackgroundTest
{
    [Fact]
    public void EmptyBackground_UsesDefinitionBackdrop() => MusicRenderingTestHost.Run(() =>
    {
        var image = Render([], backdrop: 0x204060);
        Pixel(image, 40, 40, 32, 64, 96);
    });

    [Fact]
    public void StretchAndBottomBand_PreserveLayerOrderAndSrgbTint() => MusicRenderingTestHost.Run(() =>
    {
        var image = Render([
            Layer("white", new() { Kind = "stretch" }, tint: 0x204060),
            Layer("white", new() { Kind = "bottomBand", TopRatio = .5 }, .5, "add", 0x804020),
            Layer("white", new() { Kind = "bottomBand", TopRatio = .75 }, .5, "normal", 0x204080)
        ]);
        Pixel(image, 40, 20, 32, 64, 96);
        Pixel(image, 40, 60, 96, 96, 112);
        Pixel(image, 40, 90, 64, 80, 120);
    });

    [Theory]
    [InlineData("bottom", -80, 80)]
    [InlineData("center", 40, 48)]
    public void Center_UsesOriginalDimensionsAndAnchor(string anchor, double offset, int top) => MusicRenderingTestHost.Run(() =>
    {
        var image = Render([Layer("stripe", new() { Kind = "center", Scale = 1, YAnchor = anchor, BottomOffsetPx = offset })]);
        Pixel(image, 80, top + 4, 255, 0, 0);
        Pixel(image, 112, top + 4, 0, 0, 255);
        Pixel(image, 75, top + 4, 0, 0, 0);
        Pixel(image, 80, top - 1, 0, 0, 0);
        Pixel(image, 80, top + 20, 0, 0, 0);
    });

    [Fact]
    public void Tile_CentersOneTileAndOnlyRepeatsHorizontally() => MusicRenderingTestHost.Run(() =>
    {
        var image = Render([Layer("tile", new() { Kind = "tile", Scale = 1, BottomOffsetPx = 80 })]);
        // Original width is 500: the reference phase is 210, with a tile at x=710.
        Pixel(image, 74, 100, 255, 0, 0);
        Pixel(image, 117, 100, 0, 0, 255);
        Pixel(image, 24, 100, 255, 0, 0);
        Pixel(image, 67, 100, 0, 0, 255);
        Pixel(image, 24, 95, 0, 0, 0);
    });

    [Fact]
    public void MirroredArch_JoinsMidHalvesAndMirrorsBothWings() => MusicRenderingTestHost.Run(() =>
    {
        var image = Render([Layer("stripe", new() { Kind = "mirroredArch", WingUrl = "wing", Scale = 1, BottomOffsetPx = -80 })]);
        Pixel(image, 59, 84, 255, 0, 0);
        Pixel(image, 92, 84, 0, 0, 255);
        Pixel(image, 99, 84, 0, 0, 255);
        Pixel(image, 132, 84, 255, 0, 0);
        Pixel(image, 38, 93, 255, 0, 0);
        Pixel(image, 53, 93, 0, 0, 255);
        Pixel(image, 138, 93, 0, 0, 255);
        Pixel(image, 153, 93, 255, 0, 0);
        Pixel(image, 38, 89, 0, 0, 0);
        Pixel(image, 10, 93, 0, 0, 255);
        Pixel(image, 190, 93, 255, 0, 0);
    });

    [Fact]
    public void MissingArchWing_SkipsWholeLayer() => MusicRenderingTestHost.Run(() =>
    {
        var image = Render([Layer("stripe", new() { Kind = "mirroredArch", WingUrl = "missing", Scale = 1 })]);
        Pixel(image, 80, 100, 0, 0, 0);
    });

    [Fact]
    public void SymmetricPair_EdgeOffsetsTopAnchorAndAutomaticMirror() => MusicRenderingTestHost.Run(() =>
    {
        var image = Render([Layer("stripe", new() { Kind = "symmetricPair", Scale = 1, EdgeOffsetPx = 100, YAnchor = "top", TopOffsetPx = 60 })]);
        Pixel(image, 13, 10, 255, 0, 0);
        Pixel(image, 46, 10, 0, 0, 255);
        Pixel(image, 145, 10, 0, 0, 255);
        Pixel(image, 178, 10, 255, 0, 0);
        Pixel(image, 9, 10, 0, 0, 0);
        Pixel(image, 13, 5, 0, 0, 0);
    });

    [Fact]
    public void SymmetricPair_CenterOffsetsAndExplicitRightKeepIndependentSize() => MusicRenderingTestHost.Run(() =>
    {
        var image = Render([Layer("stripe", new() { Kind = "symmetricPair", RightUrl = "wing", Scale = 1, CenterOffsetPx = 100, BottomOffsetPx = -80 })]);
        Pixel(image, 49, 84, 255, 0, 0);
        Pixel(image, 82, 84, 0, 0, 255);
        Pixel(image, 108, 93, 255, 0, 0);
        Pixel(image, 123, 93, 0, 0, 255);
        Pixel(image, 108, 89, 0, 0, 0);
        Pixel(image, 95, 93, 0, 0, 0);
    });

    [Fact]
    public void MissingExplicitRight_ReusesLeftWithoutMirroring() => MusicRenderingTestHost.Run(() =>
    {
        var image = Render([Layer("stripe", new() { Kind = "symmetricPair", RightUrl = "missing", Scale = 1, CenterOffsetPx = 100 })]);
        Pixel(image, 109, 100, 255, 0, 0);
        Pixel(image, 142, 100, 0, 0, 255);
    });

    [Theory]
    [InlineData("normal", 96)]
    [InlineData("add", 128)]
    public void OverlappingPair_AppliesLayerAlphaAndTintToEachSprite(string blend, byte expected) => MusicRenderingTestHost.Run(() =>
    {
        var image = Render([Layer("white", new() { Kind = "symmetricPair", Scale = 1, CenterOffsetPx = -200 }, .5, blend, 0x800000)]);
        Pixel(image, 96, 100, expected, 0, 0);
        Pixel(image, 68, 100, 64, 0, 0);
    });

    [Fact]
    public void DecorationScale_ChangesDimensionsAndOffsetsFromScreenAnchor() => MusicRenderingTestHost.Run(() =>
    {
        var image = Render([Layer("stripe", new() { Kind = "center", Scale = 1, BottomOffsetPx = -80 })], scale: 2);
        Pixel(image, 60, 60, 255, 0, 0);
        Pixel(image, 131, 60, 0, 0, 255);
        Pixel(image, 60, 51, 0, 0, 0);
        Pixel(image, 60, 92, 0, 0, 0);
    });

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void DisabledBackground_LeavesTargetTransparent(bool enabled, double opacity) => MusicRenderingTestHost.Run(() =>
    {
        var image = Render([], backdrop: 0xffffff, opacity: opacity, enabled: enabled);
        Pixel(image, 30, 30, 0, 0, 0, 0);
    });

    [Fact]
    public void GlobalOpacity_AppliesOnceToCompletedBackground() => MusicRenderingTestHost.Run(() =>
    {
        var image = Render([Layer("white", new() { Kind = "stretch" }, .5, "normal", 0xff0000)], opacity: .5);
        Pixel(image, 30, 30, 64, 0, 0, 128);
    });

    [Fact]
    public void DpiRasterization_PreservesBoundsAtNonUniformOutputScale() => MusicRenderingTestHost.Run(() =>
    {
        var image = Render([Layer("stripe", new() { Kind = "center", Scale = 1, BottomOffsetPx = -80 })], width: 192, height: 216, dpi: 2);
        Assert.Equal(384, image.PixelWidth);
        Assert.Equal(432, image.PixelHeight);
        Pixel(image, 160, 336, 255, 0, 0);
        Pixel(image, 224, 336, 0, 0, 255);
        Pixel(image, 150, 336, 0, 0, 0);
        Pixel(image, 160, 318, 0, 0, 0);
    });

    private static ReferenceBackgroundLayer Layer(string url, ReferenceLayerFit fit, double alpha = 1,
        string blend = "normal", uint tint = 0xffffff) => new() { Url = url, Fit = fit, Alpha = alpha, Blend = blend, Tint = tint };

    private static BitmapSource Render(ReferenceBackgroundLayer[] layers, uint backdrop = 0, double opacity = 1,
        bool enabled = true, double scale = 1, int width = 192, int height = 108, double dpi = 1)
    {
        var resources = new ReferenceSkinResources(new() { Background = layers, Backdrop = backdrop }, new Dictionary<string, ReferenceSkinImage>
        {
            ["white"] = new(Solid(), 600, 200),
            ["stripe"] = new(Stripe(), 400, 200),
            ["tile"] = new(Stripe(), 500, 200),
            ["wing"] = new(Stripe(), 200, 100)
        });
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            ReferenceBackgroundRenderer.Draw(dc, width, height, new MusicOverlayLayoutConfig { Scale = scale },
                new MusicOverlayEffectsConfig { BackgroundOpacity = opacity, ShowBackgroundDeco = enabled }, resources, dpi);
        }
        var result = new RenderTargetBitmap((int)(width * dpi), (int)(height * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        result.Render(visual);
        return result;
    }

    private static BitmapSource Solid() => BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, new byte[] { 255, 255, 255, 255 }, 4);

    private static BitmapSource Stripe() => BitmapSource.Create(4, 1, 96, 96, PixelFormats.Pbgra32, null,
        new byte[] { 0, 0, 255, 255, 0, 0, 255, 255, 255, 0, 0, 255, 255, 0, 0, 255 }, 16);

    private static void Pixel(BitmapSource image, int x, int y, byte r, byte g, byte b, byte a = 255)
    {
        var pixel = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        byte[] expected = [b, g, r, a];
        for (int channel = 0; channel < 4; channel++)
            Assert.True(Math.Abs(pixel[channel] - expected[channel]) <= 1,
                $"({x},{y}) channel {channel}: expected {expected[channel]}, actual {pixel[channel]}");
    }
}
