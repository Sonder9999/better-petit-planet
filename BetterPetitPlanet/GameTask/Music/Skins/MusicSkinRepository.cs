using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.GameTask.Music.Rendering;
using Microsoft.Extensions.Logging;

namespace BetterPetitPlanet.GameTask.Music.Skins;

/// <summary>
/// Service repository managing music playfield skins.
/// Scans local packages in data/music-skins, validates manifests, decodes frozen bitmap assets,
/// provides fallback to zero-asset default skin on any load or format failure, and handles async loading races.
/// </summary>
public sealed class MusicSkinRepository
{
    private readonly string _skinsDirectory;
    private readonly ILogger<MusicSkinRepository>? _logger;
    private readonly MusicSkinAssetCache _assetCache;
    private readonly MusicSkinPackageValidator _validator;

    public string SkinsDirectory => _skinsDirectory;
    public MusicSkinAssetCache AssetCache => _assetCache;
    internal MusicSkinPackageValidator PackageValidator => _validator;

    private static ImageSource? _cachedDefaultCover;

    public MusicSkinRepository(string? skinsDirectory = null, ILogger<MusicSkinRepository>? logger = null, MusicSkinResourceLimits? limits = null)
    {
        _logger = logger;
        _skinsDirectory = ResolveSkinsDirectory(skinsDirectory);
        _assetCache = limits == null ? MusicSkinAssetCache.Shared : new MusicSkinAssetCache(limits);
        _validator = new MusicSkinPackageValidator(limits);
    }

    /// <summary>
    /// Robustly discovers the skins root directory across execution context, parent workspaces, and fallback paths.
    /// </summary>
    public static string ResolveSkinsDirectory(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        // 1. Probing AppDomain.CurrentDomain.BaseDirectory
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string candidate = Path.Combine(baseDir, "data", "music-skins");
        if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);

        // 2. Probing Directory.GetCurrentDirectory()
        string currentDir = Directory.GetCurrentDirectory();
        candidate = Path.Combine(currentDir, "data", "music-skins");
        if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);

        // 3. Traverse parents from BaseDirectory
        try
        {
            var dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                candidate = Path.Combine(dir.FullName, "data", "music-skins");
                if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
                dir = dir.Parent;
            }
        }
        catch { }

        // 4. Traverse parents from CurrentDirectory
        try
        {
            var dir = new DirectoryInfo(currentDir);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                candidate = Path.Combine(dir.FullName, "data", "music-skins");
                if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
                dir = dir.Parent;
            }
        }
        catch { }

        return Path.GetFullPath(Path.Combine(baseDir, "data", "music-skins"));
    }

    /// <summary>
    /// Generates a crisp, frozen cover thumbnail for the built-in default skin.
    /// Uses high-resolution PreviewBitmap if available, or falls back to vector gradient drawing.
    /// </summary>
    public static ImageSource? CreateDefaultSkinCover()
    {
        if (MusicDefaultSkin.PreviewBitmap != null) return MusicDefaultSkin.PreviewBitmap;
        if (_cachedDefaultCover != null) return _cachedDefaultCover;

        try
        {
            var group = new DrawingGroup();
            using (var dc = group.Open())
            {
                var bgBrush = new LinearGradientBrush(
                    Color.FromRgb(20, 28, 48),
                    Color.FromRgb(10, 14, 26),
                    new Point(0, 0),
                    new Point(1, 1));
                bgBrush.Freeze();
                dc.DrawRoundedRectangle(bgBrush, null, new Rect(0, 0, 100, 100), 8, 8);

                var trackPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1.0);
                trackPen.Freeze();
                for (int i = 1; i <= 6; i++)
                {
                    double x = i * (100.0 / 7.0);
                    dc.DrawLine(trackPen, new Point(x, 10), new Point(x, 82));
                }

                var judgePen = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 215, 120)), 2.0);
                judgePen.Freeze();
                dc.DrawLine(judgePen, new Point(6, 82), new Point(94, 82));

                for (int i = 1; i <= 6; i++)
                {
                    double x = i * (100.0 / 7.0);
                    dc.DrawEllipse(Brushes.White, null, new Point(x, 82), 2.0, 2.0);
                }

                var noteBrush = new SolidColorBrush(Color.FromRgb(255, 205, 80));
                noteBrush.Freeze();
                dc.DrawRoundedRectangle(noteBrush, null, new Rect(38, 45, 24, 10), 3, 3);

                var note2Brush = new SolidColorBrush(Color.FromRgb(100, 220, 240));
                note2Brush.Freeze();
                dc.DrawRoundedRectangle(note2Brush, null, new Rect(66, 25, 24, 10), 3, 3);
            }

            var image = new DrawingImage(group);
            image.Freeze();
            _cachedDefaultCover = image;
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Scans the skins directory and returns descriptors for all detected and built-in skins.
    /// Never throws exceptions; invalid manifests are skipped with diagnostic logging.
    /// </summary>
    public IReadOnlyList<MusicSkinInfo> GetAvailableSkins()
    {
        var list = new List<MusicSkinInfo>
        {
            new("default", "原生默认 (静思浮梦)", "BetterPetitPlanet", "须弥纳西妲主题，轻量原生几何与高清矢量花朵渲染", IsBuiltIn: true, IconSource: CreateDefaultSkinCover())
        };

        try { list.AddRange(MusicReferenceSkinCatalog.Descriptors); }
        catch (Exception ex) { _logger?.LogWarning(ex, "读取内置音游皮肤列表失败，继续列出本地皮肤"); }
        if (!Directory.Exists(_skinsDirectory))
        {
            return list;
        }

        try
        {
            var directories = Directory.GetDirectories(_skinsDirectory);
            foreach (var dir in directories)
            {
                string dirName = Path.GetFileName(dir);
                if (string.Equals(dirName, "default", StringComparison.OrdinalIgnoreCase))
                    continue;

                string manifestPath = Path.Combine(dir, "skin.json");
                if (!File.Exists(manifestPath))
                    continue;

                try
                {
                    var manifest = _validator.ReadManifest(dir, dirName);
                    list.Add(new MusicSkinInfo(manifest.Id, manifest.DisplayName, manifest.Author, manifest.Description));
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "解析皮肤清单文件失败: {ManifestPath}", manifestPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "扫描皮肤目录失败: {SkinsDirectory}", _skinsDirectory);
        }

        return list;
    }

    /// <summary>
    /// Asynchronously loads a skin package by skinId.
    /// If skinId is missing, invalid, or fails to decode, safely falls back to MusicSkin.Default.
    /// </summary>
    public async Task<MusicSkin> LoadSkinAsync(string? skinId, CancellationToken cancellationToken = default) =>
        (await LoadSkinResultAsync(skinId, MusicSkinDecodeSize.Overlay, cancellationToken)).Skin;

    public async Task<MusicSkinLoadResult> LoadSkinResultAsync(string? skinId, MusicSkinDecodeSize? decodeSize = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string requestedId = skinId ?? "default";
        if (requestedId.Equals("default", StringComparison.OrdinalIgnoreCase))
            return new(requestedId, MusicSkin.Default, MusicDefaultSkin.ResourceDiagnostic);
        var size = decodeSize ?? MusicSkinDecodeSize.Overlay;
        if (size.Width <= 0 || size.Height <= 0) throw new ArgumentOutOfRangeException(nameof(decodeSize));
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (requestedId.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase) &&
                    MusicReferenceSkinCatalog.Find(requestedId) is { } builtin)
                    return new MusicSkinLoadResult(requestedId, MusicReferenceSkinCatalog.Load(builtin, size, cancellationToken));
                MusicSkinPackageValidator.ValidateId(requestedId);
                string skinDir = Path.Combine(_skinsDirectory, requestedId);
                var manifest = _validator.ValidateDirectory(skinDir, requestedId, cancellationToken);
                var skin = BuildSkinPackage(skinDir, manifest, size, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return new MusicSkinLoadResult(requestedId, skin);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "加载皮肤 '{SkinId}' 失败", requestedId);
                return new MusicSkinLoadResult(requestedId, MusicSkin.Default, $"皮肤 '{requestedId}' 无法加载，已使用原生默认：{ex.Message}");
            }
        }, cancellationToken);
    }

    private MusicSkin BuildSkinPackage(string skinDir, MusicSkinManifest manifest, MusicSkinDecodeSize size, CancellationToken cancellationToken)
    {
        BitmapSource? Texture(string? path, bool small = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path == null) return null;
            string hashKey = Path.GetRelativePath(skinDir, MusicSkinAssetCache.ValidateAndResolvePath(skinDir, path)).Replace('\\', '/');
            int width = small ? Math.Min(size.Width, 512) : size.Width;
            int height = small ? Math.Min(size.Height, 512) : size.Height;
            return _assetCache.GetOrLoadBitmap(skinDir, path, width, height, cancellationToken, manifest.ContentHashes[hashKey]);
        }
        var colors = manifest.Colors;
        var textures = manifest.Textures;
        var effects = manifest.Effects;

        // 1. Textures loading via asset cache
        BitmapSource? noteTap = Texture(textures?.NoteTap, small: true);
        BitmapSource? noteHold = Texture(textures?.NoteHold, small: true);
        BitmapSource? background = Texture(textures?.Background);
        BitmapSource? backgroundGradient = Texture(textures?.BackgroundGradient);
        BitmapSource? backgroundArch = Texture(textures?.BackgroundArch);
        BitmapSource? backgroundWing = Texture(textures?.BackgroundWing);
        BitmapSource? holdBeam = Texture(textures?.HoldBeam);
        BitmapSource? notePoint = Texture(textures?.NotePoint, small: true);

        BitmapSource? hitFlower = Texture(effects?.HitFlower, small: true);
        BitmapSource? hitShockwave = Texture(effects?.HitShockwave, small: true);
        BitmapSource? hitSpark = Texture(effects?.HitSpark, small: true);
        BitmapSource? hitRay = Texture(effects?.HitRay, small: true);
        BitmapSource? preview = Texture(manifest.Preview, small: true);

        // 2. Brushes & Colors resolution
        Color primaryAccent = ParseColor(colors?.PrimaryAccent, Color.FromRgb(255, 210, 100));
        Color secondaryAccent = ParseColor(colors?.SecondaryAccent, Color.FromRgb(255, 235, 150));
        Color holdColor = ParseColor(colors?.HoldColor, primaryAccent);
        Color trackBgColor = ParseColor(colors?.TrackBackground, Color.FromArgb(60, 15, 20, 32));
        Color laneGuideColor = ParseColor(colors?.LaneGuide, Color.FromArgb(45, 255, 255, 255));
        Color centerGuideColor = ParseColor(colors?.CenterGuide, Color.FromArgb(80, primaryAccent.R, primaryAccent.G, primaryAccent.B));
        Color judgeColor = ParseColor(colors?.JudgementLine, Color.FromArgb(235, 255, 245, 210));
        Color keyLabelColor = ParseColor(colors?.KeyLabel, Color.FromArgb(210, 240, 245, 255));
        Color keyAutoColor = ParseColor(colors?.KeyAutoGlow, Color.FromArgb(160, primaryAccent.R, primaryAccent.G, primaryAccent.B));
        Color keyManualColor = ParseColor(colors?.KeyManualGlow, Color.FromArgb(150, secondaryAccent.R, secondaryAccent.G, secondaryAccent.B));

        // Create & freeze brushes and pens
        var trackBgBrush = CreateSolidBrush(trackBgColor);
        var laneGuidePen = CreatePen(laneGuideColor, 1.0);
        var centerGuidePen = CreatePen(centerGuideColor, 1.2);
        var judgePen = CreatePen(judgeColor, 2.5);
        var judgeGlowBrush = CreateSolidBrush(Color.FromArgb(60, judgeColor.R, judgeColor.G, judgeColor.B));
        var keyLabelBrush = CreateSolidBrush(keyLabelColor);

        var tapFillBrush = CreateSolidBrush(primaryAccent);
        var tapStrokePen = CreatePen(Colors.White, 1.5);

        // Hold gradient beam
        var holdGradient = new LinearGradientBrush(
            Color.FromArgb(170, holdColor.R, holdColor.G, holdColor.B),
            Color.FromArgb(60, (byte)(holdColor.R * 0.7), (byte)(holdColor.G * 0.7), (byte)(holdColor.B * 0.7)),
            new Point(0.5, 1.0),
            new Point(0.5, 0.0));
        holdGradient.Freeze();

        var activeHoldGradient = new LinearGradientBrush(
            Color.FromArgb(230, (byte)Math.Min(255, holdColor.R + 40), (byte)Math.Min(255, holdColor.G + 40), (byte)Math.Min(255, holdColor.B + 40)),
            Color.FromArgb(110, holdColor.R, holdColor.G, holdColor.B),
            new Point(0.5, 1.0),
            new Point(0.5, 0.0));
        activeHoldGradient.Freeze();

        var holdStrokePen = CreatePen(Color.FromArgb(180, holdColor.R, holdColor.G, holdColor.B), 1.2);
        var holdTailCapBrush = CreateSolidBrush(secondaryAccent);

        var chordPen = CreatePen(Color.FromArgb(220, secondaryAccent.R, secondaryAccent.G, secondaryAccent.B), 2.0);
        var chordFillBrush = CreateSolidBrush(Color.FromArgb(120, secondaryAccent.R, secondaryAccent.G, secondaryAccent.B));

        var keyManualGlowBrush = CreateSolidBrush(keyManualColor);
        var keyAutoGlowBrush = CreateSolidBrush(keyAutoColor);
        var keyHitBurstBrush = CreateSolidBrush(Color.FromArgb(200, 255, 255, 240));

        return new MusicSkin
        {
            Id = manifest.Id,
            DisplayName = !string.IsNullOrWhiteSpace(manifest.DisplayName) ? manifest.DisplayName : manifest.Id,
            Author = manifest.Author,
            IsCustom = true,

            TrackBackgroundBrush = trackBgBrush,
            TrackLaneGuidePen = laneGuidePen,
            TrackCenterGuidePen = centerGuidePen,
            JudgementLinePen = judgePen,
            JudgementLineGlowBrush = judgeGlowBrush,
            KeyLabelBrush = keyLabelBrush,

            TapNoteFillBrush = tapFillBrush,
            TapNoteStrokePen = tapStrokePen,
            HoldBodyBrush = holdGradient,
            HoldBodyActiveBrush = activeHoldGradient,
            HoldBodyStrokePen = holdStrokePen,
            HoldTailCapBrush = holdTailCapBrush,

            ChordSyncPen = chordPen,
            ChordSyncFillBrush = chordFillBrush,
            KeyManualGlowBrush = keyManualGlowBrush,
            KeyAutoGlowBrush = keyAutoGlowBrush,
            KeyHitBurstBrush = keyHitBurstBrush,

            NoteTapBitmap = noteTap,
            NoteHoldBitmap = noteHold,
            NotePointBitmap = notePoint,
            BackgroundBitmap = background,
            BackgroundGradientBitmap = backgroundGradient,
            BackgroundArchBitmap = backgroundArch,
            BackgroundWingBitmap = backgroundWing,
            HoldBeamBitmap = holdBeam,

            HitFlowerBitmap = hitFlower,
            HitShockwaveBitmap = hitShockwave,
            HitSparkBitmap = hitSpark,
            HitRayBitmap = hitRay,
            PreviewBitmap = preview,

            HitEffectDurationMs = effects?.HitEffectDurationMs ?? 280.0,
            HitEffectScale = effects?.HitEffectScale ?? 1.0
        };
    }

    public static Color ParseColor(string? hex, Color fallback) =>
        MusicSkinColor.TryParse(hex, out var color) ? color : fallback;

    private static Brush CreateSolidBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(Color color, double thickness)
    {
        var pen = new Pen(CreateSolidBrush(color), thickness);
        pen.Freeze();
        return pen;
    }

    /// <summary>
    /// Clears asset memory cache and re-registers the built-in default skin.
    /// </summary>
    public void ClearCache() => _assetCache.Clear();
}
