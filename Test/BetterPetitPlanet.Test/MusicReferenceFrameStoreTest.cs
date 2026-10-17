using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Skins;

using Xunit;

namespace BetterPetitPlanet.Test;

[Collection("ReferenceFrameStore")]
public class MusicReferenceFrameStoreTest
{
    private static byte[] CreatePngBytes(int width, int height)
    {
        var raw = new byte[width * height * 4];
        for (int i = 0; i < raw.Length; i += 4)
        {
            raw[i] = 128;
            raw[i + 1] = 200;
            raw[i + 2] = 255;
            raw[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, raw, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static ZipArchive CreateArchive(params (string entryName, byte[] data)[] entries)
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var stream = entry.Open();
                stream.Write(data);
            }
        }
        ms.Position = 0;
        return new ZipArchive(ms, ZipArchiveMode.Read);
    }

    [Fact]
    public void PackedFrames_DecodeSynchronouslyAtOriginalDimensions() => MusicRenderingTestHost.Run(() =>
    {
        ReferenceFrameStore.Clear();
        var png512 = CreatePngBytes(512, 512);
        ReferenceFrameStore.RegisterArchive("furina", CreateArchive(("furina-hit-atlas/0006.png", png512)));
        var frame = ReferenceFrameStore.Get("assets/fx/furina-hit-atlas.png", 6);
        Assert.Equal(512, frame.Width);
        Assert.Equal(512, frame.Height);
        Assert.Contains(frame.Bytes, value => value > 0);
    });

    [Fact]
    public void TraversingAllSkins_KeepsDecodedFrameCacheBounded() => MusicRenderingTestHost.Run(() =>
    {
        ReferenceFrameStore.Clear();
        var png512 = CreatePngBytes(512, 512);
        var entries = new (string, byte[])[70];
        for (int i = 0; i < 70; i++)
            entries[i] = ($"testlru-hit-atlas/{i:D4}.png", png512);
        ReferenceFrameStore.RegisterArchive("testlru", CreateArchive(entries));

        for (int frame = 0; frame < 70; frame++)
        {
            var image = ReferenceFrameStore.Get("assets/fx/testlru-hit-atlas.png", frame);
            Assert.Equal(512, image.Width);
            Assert.InRange(ReferenceFrameStore.CachedBytes, 1, ReferenceFrameStore.MaximumBytes);
        }
    });
}
