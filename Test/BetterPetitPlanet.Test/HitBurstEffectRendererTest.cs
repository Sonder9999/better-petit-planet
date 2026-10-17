using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Skins;
using Xunit;

namespace BetterPetitPlanet.Test;

public class HitBurstEffectRendererTest
{
    private static void RunInSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception != null)
        {
            throw new AggregateException("STA thread execution failed", exception);
        }
    }

    [Fact]
    public void SliceAtlas_Produces64FrozenCroppedBitmapsWithCorrectRectangles()
    {
        RunInSta(() =>
        {
            // Create a synthetic 4096x4096 BitmapSource using RenderTargetBitmap
            var mockAtlas = new RenderTargetBitmap(4096, 4096, 96, 96, PixelFormats.Pbgra32);
            mockAtlas.Freeze();

            var frames = HitBurstEffectRenderer.SliceAtlas(mockAtlas);
            Assert.NotNull(frames);
            Assert.Equal(HitBurstEffectRenderer.TotalFrames, frames.Length);

            // Check Frame 0: col 0, row 0 -> (0, 0, 512, 512)
            var frame0 = Assert.IsType<CroppedBitmap>(frames[0]);
            Assert.True(frame0.IsFrozen);
            Assert.Equal(new Int32Rect(0, 0, 512, 512), frame0.SourceRect);

            // Check Frame 7: col 7, row 0 -> (3584, 0, 512, 512)
            var frame7 = Assert.IsType<CroppedBitmap>(frames[7]);
            Assert.True(frame7.IsFrozen);
            Assert.Equal(new Int32Rect(3584, 0, 512, 512), frame7.SourceRect);

            // Check Frame 8: col 0, row 1 -> (0, 512, 512, 512)
            var frame8 = Assert.IsType<CroppedBitmap>(frames[8]);
            Assert.True(frame8.IsFrozen);
            Assert.Equal(new Int32Rect(0, 512, 512, 512), frame8.SourceRect);

            // Check Frame 63: col 7, row 7 -> (3584, 3584, 512, 512)
            var frame63 = Assert.IsType<CroppedBitmap>(frames[63]);
            Assert.True(frame63.IsFrozen);
            Assert.Equal(new Int32Rect(3584, 3584, 512, 512), frame63.SourceRect);

            // Null atlas returns null
            Assert.Null(HitBurstEffectRenderer.SliceAtlas(null));
        });
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(16.666, 0)]
    [InlineData(16.667, 1)]
    [InlineData(500.0, 30)]
    [InlineData(1000.0, 60)]
    [InlineData(1066.0, 63)]
    [InlineData(1067.0, -1)]
    [InlineData(-10.0, -1)]
    public void ComputeFrameIndex_CalculatesCorrect60FpsFrameIndex(double elapsedMs, int expectedFrame)
    {
        int actual = HitBurstEffectRenderer.ComputeFrameIndex(elapsedMs);
        Assert.Equal(expectedFrame, actual);
    }

    [Fact]
    public void ComputeBurstSize_ScalesProportionallyToDesignCanvas()
    {
        var layout = new MusicOverlayLayoutConfig { Scale = 1.0 };

        // 1920 base canvas -> 576.0px
        double size1920 = HitBurstEffectRenderer.ComputeBurstSize(1920.0, layout);
        Assert.Equal(576.0, size1920, precision: 4);

        // 960 half width -> 288.0px
        double size960 = HitBurstEffectRenderer.ComputeBurstSize(960.0, layout);
        Assert.Equal(288.0, size960, precision: 4);

        // 1920 width with 1.5 scale -> 864.0px
        layout.Scale = 1.5;
        double sizeScaled = HitBurstEffectRenderer.ComputeBurstSize(1920.0, layout);
        Assert.Equal(864.0, sizeScaled, precision: 4);
    }

    [Fact]
    public void ComputeFallbackProgress_EvaluatesCubicEaseOutCurve()
    {
        // At 0ms: p = 0, easeOut = 0
        bool active0 = HitBurstEffectRenderer.ComputeFallbackProgress(0.0, out double p0, out double ease0);
        Assert.True(active0);
        Assert.Equal(0.0, p0, precision: 4);
        Assert.Equal(0.0, ease0, precision: 4);

        // At 190ms (50% progress): p = 0.5, easeOut = 1 - 0.5^3 = 0.875
        bool activeHalf = HitBurstEffectRenderer.ComputeFallbackProgress(190.0, out double pHalf, out double easeHalf);
        Assert.True(activeHalf);
        Assert.Equal(0.5, pHalf, precision: 4);
        Assert.Equal(0.875, easeHalf, precision: 4);

        // At 380ms (100% progress): p = 1.0, easeOut = 1.0
        bool activeEnd = HitBurstEffectRenderer.ComputeFallbackProgress(380.0, out double pEnd, out double easeEnd);
        Assert.True(activeEnd);
        Assert.Equal(1.0, pEnd, precision: 4);
        Assert.Equal(1.0, easeEnd, precision: 4);

        // At > 380ms: outside duration
        bool activePast = HitBurstEffectRenderer.ComputeFallbackProgress(381.0, out _, out _);
        Assert.False(activePast);
    }

    [Fact]
    public void DrawHitBurst_ExecutesCleanlyAndCentersOnJudgementLine()
    {
        RunInSta(() =>
        {
            const double width = 1920.0;
            const double laneX = 500.0;
            const double judgeY = 800.0;
            var layout = new MusicOverlayLayoutConfig();
            var skin = MusicSkin.Default;

            // Draw at 500ms
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                bool rendered = HitBurstEffectRenderer.DrawHitBurst(
                    dc,
                    laneX,
                    judgeY,
                    elapsedMs: 500.0,
                    playfieldWidth: width,
                    layout: layout,
                    skin: skin);

                Assert.True(rendered);
            }

            Assert.NotNull(visual.Drawing);
            var bounds = visual.Drawing.Bounds;
            Assert.False(bounds.IsEmpty);

            // Bounding box must be centered around (laneX, judgeY)
            double centerX = bounds.X + bounds.Width / 2.0;
            double centerY = bounds.Y + bounds.Height / 2.0;
            Assert.Equal(laneX, centerX, precision: 2);
            Assert.Equal(judgeY, centerY, precision: 2);
        });
    }

    [Fact]
    public void DrawHitBurst_FallbackRendersWhenNoAtlasFramesProvided()
    {
        RunInSta(() =>
        {
            const double width = 1920.0;
            const double laneX = 400.0;
            const double judgeY = 700.0;
            var layout = new MusicOverlayLayoutConfig();

            // Skin with explicitly null atlas frames to trigger fallback
            var fallbackSkin = new MusicSkin
            {
                Id = "fallback-test",
                HitAtlasFrames = null
            };

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                bool rendered = HitBurstEffectRenderer.DrawHitBurst(
                    dc,
                    laneX,
                    judgeY,
                    elapsedMs: 150.0,
                    playfieldWidth: width,
                    layout: layout,
                    skin: fallbackSkin);

                Assert.True(rendered);
            }

            Assert.NotNull(visual.Drawing);
            Assert.False(visual.Drawing.Bounds.IsEmpty);
        });
    }
}
