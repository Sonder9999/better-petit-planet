using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace BetterPetitPlanet.GameTask.Music.Skins;

/// <summary>Reusable admission boundary for installed packages and future staged imports. Does not decode pixels.</summary>
public sealed class MusicSkinPackageValidator
{
    private readonly MusicSkinResourceLimits _limits;
    internal MusicSkinResourceLimits Limits => _limits;
    public MusicSkinPackageValidator(MusicSkinResourceLimits? limits = null)
    {
        _limits = limits ?? new();
        _limits.Validate();
    }

    public static void ValidateId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128 ||
            !Regex.IsMatch(id, @"\A[a-zA-Z0-9][a-zA-Z0-9_-]*\z") ||
            Regex.IsMatch(id, @"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\z", RegexOptions.IgnoreCase))
            throw new ArgumentException("皮肤标识无效：仅支持字母、数字、下划线和连字符，不能使用设备名", nameof(id));
        if (id.Equals("default", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("皮肤标识 default 保留给内置皮肤", nameof(id));
    }

    public static string ResolvePath(string packageDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(packageDirectory) || string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) || relativePath.Contains(':') || relativePath.Contains("..") ||
            relativePath.Split('/', '\\').Any(part => part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new ArgumentException("资源路径必须是包内规范相对路径，禁止越界或流名称");
        string extension = Path.GetExtension(relativePath).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".json"))
            throw new ArgumentException("只允许 JSON/PNG/JPG/JPEG 数据资源");
        string root = Path.GetFullPath(packageDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string resolved = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("资源路径超出皮肤包根目录");
        RejectReparsePoints(resolved);
        return resolved;
    }

    internal static void RejectReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("皮肤路径含重解析点（符号链接或目录联接），已拒绝访问");
        }
    }

    /// <summary>Bounded metadata discovery. Assets and hashes are validated only when the package is loaded.</summary>
    public MusicSkinManifest ReadManifest(string packageDirectory, string expectedId)
    {
        ValidateId(expectedId);
        RejectReparsePoints(packageDirectory);
        if (!Directory.Exists(packageDirectory)) throw new DirectoryNotFoundException($"皮肤 '{expectedId}' 未安装");
        string path = ResolvePath(packageDirectory, "skin.json");
        MusicSkinManifest manifest;
        using (var stream = OpenRead(path))
        {
            if (stream.Length > _limits.MaxManifestBytes) throw new InvalidOperationException("JSON 文件超过大小限制");
            manifest = JsonSerializer.Deserialize<MusicSkinManifest>(stream, new JsonSerializerOptions { MaxDepth = 32 })
                ?? throw new InvalidOperationException("皮肤清单为空");
        }
        if (manifest.SchemaVersion != 1) throw new InvalidOperationException($"不支持皮肤 schemaVersion {manifest.SchemaVersion}");
        ValidateId(manifest.Id);
        if (!manifest.Id.Equals(expectedId, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(Path.TrimEndingDirectorySeparator(packageDirectory)).Equals(expectedId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("皮肤 id 与目录标识不一致");
        if (string.IsNullOrWhiteSpace(manifest.DisplayName)) throw new InvalidOperationException("皮肤缺少 displayName");
        if (manifest.Capabilities == null || manifest.Capabilities.Count != 1 || manifest.Capabilities[0] != "native-v1")
            throw new InvalidOperationException("皮肤兼容能力必须为 native-v1");
        if (manifest.Effects is { } effects &&
            (!double.IsFinite(effects.HitEffectDurationMs) || effects.HitEffectDurationMs < 1 || effects.HitEffectDurationMs > 2000 ||
             !double.IsFinite(effects.HitEffectScale) || effects.HitEffectScale < 0.1 || effects.HitEffectScale > 4))
            throw new InvalidOperationException("皮肤特效参数超限：持续时间须为 1–2000 ms，缩放须为 0.1–4 的有限数值");
        if (manifest.ContentHashes == null) throw new InvalidOperationException("皮肤缺少内容哈希");
        ValidateColors(manifest.Colors);
        return manifest;
    }

    private static void ValidateColors(MusicSkinColors? colors)
    {
        if (colors == null) return;
        foreach (var (name, value) in new[]
        {
            ("primaryAccent", colors.PrimaryAccent), ("secondaryAccent", colors.SecondaryAccent),
            ("holdColor", colors.HoldColor), ("trackBackground", colors.TrackBackground),
            ("laneGuide", colors.LaneGuide), ("centerGuide", colors.CenterGuide),
            ("judgementLine", colors.JudgementLine), ("keyLabel", colors.KeyLabel),
            ("keyAutoGlow", colors.KeyAutoGlow), ("keyManualGlow", colors.KeyManualGlow)
        })
        {
            if (value != null && !MusicSkinColor.TryParse(value, out _))
                throw new InvalidOperationException($"皮肤颜色 colors.{name} 无效：仅支持 #RGB/#ARGB/#RRGGBB/#AARRGGBB 或 WPF 命名色");
        }
    }

    public MusicSkinManifest ValidateDirectory(string packageDirectory, string expectedId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manifest = ReadManifest(packageDirectory, expectedId);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directories = new Stack<string>();
        directories.Push(packageDirectory);
        int directoryCount = 0;
        long bytes = 0;
        long pixels = 0;
        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("皮肤包不允许重解析点");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (++directoryCount > _limits.MaxFiles) throw new InvalidOperationException("皮肤目录数量超过资源限制");
                    directories.Push(entry);
                    continue;
                }
                string relative = Path.GetRelativePath(packageDirectory, entry);
                string path = ResolvePath(packageDirectory, relative);
                if (files.Count >= _limits.MaxFiles) throw new InvalidOperationException($"皮肤文件数量超过 {_limits.MaxFiles} 上限");
                files.Add(relative.Replace('\\', '/'), path);
                using var stream = OpenRead(path);
                long length = stream.Length;
                if (length > _limits.MaxPackageBytes - bytes) throw new InvalidOperationException($"皮肤总字节超过 {_limits.MaxPackageBytes} 上限");
                bytes += length;
                if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    if (length > _limits.MaxManifestBytes) throw new InvalidOperationException("JSON 文件超过大小限制");
                    using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
                }
                else
                {
                    if (length > _limits.MaxImageBytes) throw new InvalidOperationException("图片文件超过单图字节限制");
                    var header = MusicSkinImageHeader.Read(stream, Path.GetExtension(path), _limits);
                    pixels += (long)header.Width * header.Height;
                    if (pixels > _limits.MaxTotalImagePixels) throw new InvalidOperationException($"图片总像素超过 {_limits.MaxTotalImagePixels} 上限");
                }
            }
        }
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, hash) in manifest.ContentHashes)
        {
            string normalized = Path.GetRelativePath(packageDirectory, ResolvePath(packageDirectory, relative)).Replace('\\', '/');
            if (!files.ContainsKey(normalized) || normalized.Equals("skin.json", StringComparison.OrdinalIgnoreCase) ||
                hash == null || !Regex.IsMatch(hash, @"\A[0-9a-fA-F]{64}\z") || !hashes.TryAdd(normalized, hash))
                throw new InvalidOperationException("皮肤内容哈希列表无效");
        }
        foreach (var (relative, path) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (relative.Equals("skin.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (!hashes.TryGetValue(relative, out var hash)) throw new InvalidOperationException($"资源缺少内容哈希：{relative}");
            using var stream = OpenRead(path);
            ValidateHash(stream, hash, cancellationToken);
        }
        foreach (string relative in ResourcePaths(manifest))
        {
            string path = ResolvePath(packageDirectory, relative);
            if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("纹理引用必须是图片");
            if (!files.ContainsKey(Path.GetRelativePath(packageDirectory, path).Replace('\\', '/')))
                throw new FileNotFoundException($"皮肤引用资源不存在：{relative}");
        }
        // Keep normalized keys for decoder-side hash revalidation on the same locked stream.
        manifest.ContentHashes = hashes;
        return manifest;
    }

    internal static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    internal static void ValidateHash(Stream stream, string expectedHash, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[8192];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer[..count]);
        }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("皮肤资源内容哈希不匹配");
        stream.Position = 0;
    }

    private static IEnumerable<string> ResourcePaths(MusicSkinManifest manifest) => new[]
    {
        manifest.Preview, manifest.Textures?.NoteTap, manifest.Textures?.NoteHold, manifest.Textures?.NotePoint,
        manifest.Textures?.Background, manifest.Textures?.BackgroundGradient, manifest.Textures?.BackgroundArch,
        manifest.Textures?.BackgroundWing, manifest.Textures?.HoldBeam, manifest.Effects?.HitFlower,
        manifest.Effects?.HitShockwave, manifest.Effects?.HitSpark, manifest.Effects?.HitRay
    }.Where(path => path != null).Select(path => path!);
}
