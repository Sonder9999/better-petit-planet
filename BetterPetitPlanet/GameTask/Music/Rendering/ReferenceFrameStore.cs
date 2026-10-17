using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.GameTask.Music.Skins;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

/// <summary>Lossless atlas frames are decoded on demand; switching themes cannot grow the retained pixel pool.</summary>
internal static class ReferenceFrameStore
{
    internal const long MaximumBytes = 64L * 1024 * 1024;
    private sealed record Entry(string Key, ReferencePixels Pixels);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, LinkedListNode<Entry>> Frames = [];
    private static readonly LinkedList<Entry> Lru = new();
    private static readonly Dictionary<string, ZipArchive> Archives = [];
    private static readonly Dictionary<string, BitmapSource> Atlases = [];
    private static long _bytes;
    internal static long CachedBytes { get { lock (Gate) return _bytes; } }

    internal static void RegisterArchive(string skin, ZipArchive archive)
    {
        lock (Gate)
        {
            if (Archives.TryGetValue(skin, out var old)) old.Dispose();
            Archives[skin] = archive;
        }
    }

    private static ZipArchive OpenZipReadOnly(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var stream = new MemoryStream(bytes);
        return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
    }

    public static void Validate(ReferenceSkinDefinition skin)
    {
        string zipPath = Path.Combine(MusicReferenceSkinCatalog.AssetRoot, "frames", skin.Id + ".zip");
        if (!File.Exists(zipPath))
        {
            // If individual frame zip package does not exist, ensure compiled atlas exists
            string? atlasRelative = skin.Hit?.Url;
            if (atlasRelative != null)
            {
                string direct = Path.Combine(MusicReferenceSkinCatalog.AssetRoot, atlasRelative);
                if (File.Exists(direct) || MusicDefaultSkin.ResolveAssetPath(atlasRelative) != null)
                    return;
            }
            return;
        }

        using var archive = OpenZipReadOnly(zipPath);
        void Check(string url, int count, int size)
        {
            string prefix = Path.GetFileNameWithoutExtension(url) + "/";
            for (int i = 0; i < count; i++)
                if (archive.GetEntry(prefix + i.ToString("D4") + ".png") == null)
                    throw new FileNotFoundException("内置皮肤动画帧不完整", url);
            var first = Get(url, 0);
            if (first.Width != size || first.Height != size) throw new InvalidDataException("内置皮肤动画帧尺寸不正确");
        }
        Check(skin.Hit.Url, skin.Hit.FrameCount, skin.Hit.FrameSize);
        if (skin.Hit.AdditiveUrl is { } light) Check(light, skin.Hit.FrameCount, skin.Hit.FrameSize);
        if (skin.HoldProfile is { } profile)
        {
            int count = 0;
            foreach (var layer in profile.Layers) count = Math.Max(count, layer.FrameStart + layer.FrameCount);
            Check(profile.NormalUrl, count, profile.FrameSize); Check(profile.AdditiveUrl, count, profile.FrameSize);
        }
    }

    public static ReferencePixels Get(string atlasUrl, int frame)
    {
        string key = Path.GetFileNameWithoutExtension(atlasUrl) + "/" + frame.ToString("D4") + ".png";
        lock (Gate)
        {
            if (Frames.TryGetValue(key, out var found)) { MusicOverlayDiagnostics.Record(MusicOverlayDiagnostics.Metric.CacheHit, 1); Lru.Remove(found); Lru.AddFirst(found); return found.Value.Pixels; }
            MusicOverlayDiagnostics.Record(MusicOverlayDiagnostics.Metric.CacheMiss, 1);
            string skin = Path.GetFileNameWithoutExtension(atlasUrl).Split('-')[0];
            if (!Archives.TryGetValue(skin, out var archive))
            {
                string zipPath = Path.Combine(MusicReferenceSkinCatalog.AssetRoot, "frames", skin + ".zip");
                if (File.Exists(zipPath))
                {
                    if (Archives.Count >= 4)
                    {
                        foreach (var opened in Archives.Values) opened.Dispose();
                        Archives.Clear();
                    }
                    Archives[skin] = archive = OpenZipReadOnly(zipPath);
                }
            }

            if (archive != null && archive.GetEntry(key) is { } entry)
            {
                using var stream = entry.Open();
                var pixels = new ReferencePixels(stream);
                StorePixelLru(key, pixels);
                return pixels;
            }

            var atlasPixels = LoadFromAtlas(atlasUrl, frame);
            if (atlasPixels != null)
            {
                StorePixelLru(key, atlasPixels);
                return atlasPixels;
            }

            throw new FileNotFoundException("内置皮肤动画帧缺失", key);
        }
    }

    private static ReferencePixels? LoadFromAtlas(string atlasUrl, int frame)
    {
        if (!Atlases.TryGetValue(atlasUrl, out var atlas))
        {
            string? path = Path.Combine(MusicReferenceSkinCatalog.AssetRoot, atlasUrl);
            if (!File.Exists(path)) path = MusicDefaultSkin.ResolveAssetPath(atlasUrl);
            if (path == null || !File.Exists(path)) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            Atlases[atlasUrl] = atlas = image;
        }

        const int frameSize = 512;
        int columns = atlas.PixelWidth >= 4096 ? 8 : Math.Max(1, atlas.PixelWidth / frameSize);
        int col = frame % columns;
        int row = frame / columns;

        if ((col + 1) * frameSize > atlas.PixelWidth || (row + 1) * frameSize > atlas.PixelHeight)
            return null;

        var cropped = new CroppedBitmap(atlas, new System.Windows.Int32Rect(col * frameSize, row * frameSize, frameSize, frameSize));
        cropped.Freeze();
        return new ReferencePixels(cropped);
    }

    private static void StorePixelLru(string key, ReferencePixels pixels)
    {
        while (_bytes + pixels.ByteSize > MaximumBytes && Lru.Last is { } last)
        {
            Lru.RemoveLast(); Frames.Remove(last.Value.Key); _bytes -= last.Value.Pixels.ByteSize;
        }
        var node = Lru.AddFirst(new Entry(key, pixels)); Frames.Add(key, node); _bytes += pixels.ByteSize;
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Frames.Clear(); Lru.Clear(); _bytes = 0;
            foreach (var archive in Archives.Values) archive.Dispose();
            Archives.Clear();
            Atlases.Clear();
        }
    }
}
