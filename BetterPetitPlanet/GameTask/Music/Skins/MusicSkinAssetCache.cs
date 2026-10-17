using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Media.Imaging;

namespace BetterPetitPlanet.GameTask.Music.Skins;

/// <summary>
/// Weak image sharing plus a live-pixel budget. The cache owns no strong bitmap references.
/// Clear only removes dead entries: images retained by skins, controls or WPF drawings remain charged.
/// Bytes are a conservative pixel-storage estimate, not process private bytes or GPU memory.
/// </summary>
public sealed class MusicSkinAssetCache
{
    private sealed record Entry(WeakReference<BitmapSource> Bitmap, long Bytes);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly MusicSkinResourceLimits _limits;
    private long _reservedBytes;
    internal static MusicSkinAssetCache Shared { get; } = new();

    public MusicSkinAssetCache(MusicSkinResourceLimits? limits = null)
    {
        _limits = limits ?? new();
        _limits.Validate();
    }

    public int CachedCount { get { lock (_gate) { Prune(); return _entries.Count; } } }
    public long LiveDecodedBytes { get { lock (_gate) { Prune(); return _entries.Values.Sum(entry => entry.Bytes) + _reservedBytes; } } }

    public static string ValidateAndResolvePath(string baseDirectory, string relativePath) =>
        MusicSkinPackageValidator.ResolvePath(baseDirectory, relativePath);

    public static BitmapSource? LoadBitmap(string filePath, int decodePixelWidth = 0, int decodePixelHeight = 0) =>
        Shared.GetOrLoadBitmap(Path.GetDirectoryName(Path.GetFullPath(filePath))!, Path.GetFileName(filePath), decodePixelWidth, decodePixelHeight);

    public BitmapSource? GetOrLoadBitmap(string baseDirectory, string? relativePath, int decodePixelWidth = 0,
        int decodePixelHeight = 0, CancellationToken cancellationToken = default, string? expectedHash = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (relativePath == null) return null;
        if (decodePixelWidth < 0 || decodePixelHeight < 0) throw new ArgumentOutOfRangeException(nameof(decodePixelWidth));
        string path = ValidateAndResolvePath(baseDirectory, relativePath);
        // Admission and decode are serialized. Duplicate requests cannot reserve/decode twice.
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Prune();
            MusicSkinPackageValidator.RejectReparsePoints(path);
            using var stream = MusicSkinPackageValidator.OpenRead(path);
            if (stream.Length > _limits.MaxImageBytes) throw new InvalidOperationException("图片文件超过单图字节限制");
            var header = MusicSkinImageHeader.Read(stream, Path.GetExtension(path), _limits);
            if (expectedHash != null) MusicSkinPackageValidator.ValidateHash(stream, expectedHash, cancellationToken);
            string hash = expectedHash ?? Convert.ToHexString(SHA256.HashData(stream));
            stream.Position = 0;
            double ratio = Math.Min(1, Math.Min(decodePixelWidth > 0 ? (double)decodePixelWidth / header.Width : 1,
                decodePixelHeight > 0 ? (double)decodePixelHeight / header.Height : 1));
            int width = Math.Max(1, (int)(header.Width * ratio));
            int height = Math.Max(1, (int)(header.Height * ratio));
            string key = $"{path}|{hash}|{width}x{height}";
            if (_entries.TryGetValue(key, out var existing) && existing.Bitmap.TryGetTarget(out var bitmap)) return bitmap;
            // PNG may decode to 64bpp. Charge eight bytes per output pixel for every image.
            long bytes = checked((long)width * height * 8);
            long liveBytes = _entries.Values.Sum(entry => entry.Bytes);
            if (bytes > _limits.MaxDecodedBytes - liveBytes - _reservedBytes || _entries.Count >= _limits.MaxDecodedAssets)
                throw new InvalidOperationException("皮肤解码预算不足；活动图片或尚未回收的图片仍占用预算，请关闭其他预览后稍候重试");
            _reservedBytes += bytes;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = width;
                image.DecodePixelHeight = height;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                if (image.PixelWidth != width || image.PixelHeight != height || image.Format.BitsPerPixel > 64)
                    throw new InvalidOperationException("图片解码结果超出预留像素预算");
                _entries[key] = new(new WeakReference<BitmapSource>(image), bytes);
                cancellationToken.ThrowIfCancellationRequested();
                return image;
            }
            finally { _reservedBytes -= bytes; }
        }
    }

    private void Prune()
    {
        foreach (var key in _entries.Where(pair => !pair.Value.Bitmap.TryGetTarget(out _)).Select(pair => pair.Key).ToArray())
            _entries.Remove(key);
    }

    public void Clear() { lock (_gate) Prune(); }
}
