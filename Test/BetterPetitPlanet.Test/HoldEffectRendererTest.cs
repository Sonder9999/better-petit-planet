using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Rendering;

namespace BetterPetitPlanet.Test;

public class HoldEffectRendererTest
{
    [Fact]
    public void SourceAtlases_AreAvailableForBuiltInSkin() => MusicRenderingTestHost.Run(() =>
        Assert.True(HoldEffectRenderer.HasSource));

    [Fact]
    public void SourceEffect_RewindRestoresSamePixels() => MusicRenderingTestHost.Run(() =>
    {
        var backdrop = new GeometryDrawing(Brushes.DarkSlateGray, null, new RectangleGeometry(new Rect(0, 0, 240, 240)));
        var renderer = new HoldEffectRenderer();
        var bounds = new Rect(0, 0, 240, 240);
        byte[] Capture(double age)
        {
            var image = renderer.Render(backdrop, bounds, age)!;
            var pixels = new byte[240 * 240 * 4];
            image.CopyPixels(pixels, 960, 0);
            return pixels;
        }
        var initial = Capture(500);
        Assert.NotEqual(initial, Capture(1200));
        Assert.Equal(initial, Capture(500));
    });

    [Fact]
    public void MusicPreviewSource_SimulatesContinuousHoldingAndHitBursts()
    {
        using var preview = new MusicPreviewSource(monotonicClock: () => 0, loopDurationMs: 9000);
        preview.Seek(3000);
        Assert.Contains("A", preview.CaptureSnapshot().ActiveKeys);
        Assert.True(preview.IsLaneHolding("A", 3000));
        Assert.True(preview.IsLaneHolding("D", 3000));
        Assert.False(preview.IsLaneHolding("S", 3000));
        Assert.Equal(500, preview.GetLaneHoldAgeMs("A", 3500));
        Assert.Equal(500, preview.GetHitBurstElapsedMs("S", 3500));
        Assert.False(preview.IsLaneHolding("A", 4700));
    }
}
