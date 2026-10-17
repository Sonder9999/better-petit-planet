using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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

    public static void Validate(ReferenceSkinDefinition skin)
    {
        using var archive = ZipFile.OpenRead(Path.Combine(MusicReferenceSkinCatalog.AssetRoot, "frames", skin.Id + ".zip"));
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
                // Frame pixels are independent of the archive once decoded.
                if (Archives.Count >= 4)
                {
                    foreach (var opened in Archives.Values) opened.Dispose();
                    Archives.Clear();
                }
                Archives[skin] = archive = ZipFile.OpenRead(Path.Combine(MusicReferenceSkinCatalog.AssetRoot, "frames", skin + ".zip"));
            }
            using var stream = (archive.GetEntry(key) ?? throw new FileNotFoundException("内置皮肤动画帧缺失", key)).Open();
            var pixels = new ReferencePixels(stream);
            while (_bytes + pixels.ByteSize > MaximumBytes && Lru.Last is { } last)
            {
                Lru.RemoveLast(); Frames.Remove(last.Value.Key); _bytes -= last.Value.Pixels.ByteSize;
            }
            var node = Lru.AddFirst(new Entry(key, pixels)); Frames.Add(key, node); _bytes += pixels.ByteSize;
            return pixels;
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Frames.Clear(); Lru.Clear(); _bytes = 0;
            foreach (var archive in Archives.Values) archive.Dispose();
            Archives.Clear();
        }
    }
}
