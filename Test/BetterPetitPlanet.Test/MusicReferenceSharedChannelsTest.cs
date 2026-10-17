using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Skins;

using Xunit;

namespace BetterPetitPlanet.Test;

[Collection("ReferenceFrameStore")]
public class MusicReferenceSharedChannelsTest
{
    private static byte[] CreatePngBytes(int width, int height)
    {
        var raw = new byte[width * height * 4];
        for (int i = 0; i < raw.Length; i += 4)
        {
            raw[i] = 100;
            raw[i + 1] = 150;
            raw[i + 2] = 200;
            raw[i + 3] = 180;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, raw, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static void EnsureFurinaArchive()
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var hitBytes = CreatePngBytes(512, 512);
            var holdBytes = CreatePngBytes(160, 160);
            for (int i = 0; i <= 60; i++)
            {
                using (var s1 = archive.CreateEntry($"furina-hit-atlas/{i:D4}.png").Open())
                    s1.Write(hitBytes);
                using (var s2 = archive.CreateEntry($"furina-hit-additive/{i:D4}.png").Open())
                    s2.Write(hitBytes);
            }
            for (int i = 0; i <= 150; i++)
            {
                using (var s1 = archive.CreateEntry($"furina-normal/{i:D4}.png").Open())
                    s1.Write(holdBytes);
                using (var s2 = archive.CreateEntry($"furina-additive/{i:D4}.png").Open())
                    s2.Write(holdBytes);
            }
        }
        ms.Position = 0;
        ReferenceFrameStore.RegisterArchive("furina", new ZipArchive(ms, ZipArchiveMode.Read));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChordSpriteReuse_CompositesAgainstEachNotesOwnBackdrop(bool hold) => MusicRenderingTestHost.Run(() =>
    {
        ReferenceFrameStore.Clear();
        EnsureFurinaArchive();
        var definition = MusicReferenceSkinCatalog.Definitions.Single(skin => skin.Id == "furina");
        var firstBack = Back(Color.FromArgb(128, 30, 60, 90));
        var secondBack = Back(Color.FromArgb(192, 90, 140, 65));
        double side = hold ? definition.HoldProfile!.Layers.Max(layer => layer.DisplaySize) : definition.Hit.DisplaySize;
        var bounds = new Rect(0, 0, side / 2, side / 2);
        BitmapSource first, second, expected;
        if (hold)
        {
            var a = new ReferenceCompiledHoldRenderer(definition.HoldProfile!);
            var b = new ReferenceCompiledHoldRenderer(definition.HoldProfile!);
            var c = new ReferenceCompiledHoldRenderer(definition.HoldProfile!);
            first = a.Render(firstBack, bounds, 350, 1, 1);
            var before = Pixels(first);
            second = b.Render(secondBack, bounds, 350, 1, 1, a.Channels);
            Assert.Equal(before, Pixels(first));
            expected = c.Render(secondBack, bounds, 350, 1, 1);
            // The reused renderer must still advance its schedule correctly later.
            Assert.Equal(Pixels(c.Render(secondBack, bounds, 750, 1, 1)), Pixels(b.Render(secondBack, bounds, 750, 1, 1)));
            second = b.Render(secondBack, bounds, 350, 1, 1, a.Channels);
            expected = c.Render(secondBack, bounds, 350, 1, 1);
        }
        else
        {
            var a = new ReferenceHitRenderer(); var b = new ReferenceHitRenderer(); var c = new ReferenceHitRenderer();
            first = a.Render(firstBack, bounds, definition.Hit, 350, 1, 1)!;
            var before = Pixels(first);
            second = b.Render(secondBack, bounds, definition.Hit, 350, 1, 1, a.Channels)!;
            Assert.Equal(before, Pixels(first));
            expected = c.Render(secondBack, bounds, definition.Hit, 350, 1, 1)!;
        }
        Assert.Equal(Pixels(expected), Pixels(second));
        Assert.False(Pixels(first).SequenceEqual(Pixels(second)));
    });

    private static Drawing Back(Color color) => new GeometryDrawing(new SolidColorBrush(color), null,
        new RectangleGeometry(new Rect(0, 0, 1024, 1024)));
    private static byte[] Pixels(BitmapSource image)
    {
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0); return pixels;
    }
}
