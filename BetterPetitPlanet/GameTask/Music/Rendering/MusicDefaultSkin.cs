using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

/// <summary>
/// High-fidelity native default skin for WPF DrawingVisual playfield, modeled after Nahida (静思浮梦).
/// Loads core high-resolution texture assets from Assets/MusicOverlay while providing pure frozen
/// vector fallback brushes and pens.
/// Shared brushes, pens, and bitmaps are frozen for reuse across frames.
/// </summary>
public static class MusicDefaultSkin
{
    // Track Background & Guides
    public static readonly Brush TrackBackgroundBrush;
    public static readonly Pen TrackLaneGuidePen;
    public static readonly Pen TrackCenterGuidePen;
    public static readonly Pen JudgementLinePen;
    public static readonly Brush JudgementLineGlowBrush;

    // Key Labels (A, S, D, F, J, K, L)
    public static readonly Typeface KeyLabelTypeface;
    public static readonly Brush KeyLabelBrush;

    // Tap Notes
    public static readonly Brush TapNoteFillBrush;
    public static readonly Pen TapNoteStrokePen;

    // Hold Notes (Beams, Caps, Gradients)
    public static readonly Brush HoldBodyBrush;
    public static readonly Brush HoldBodyActiveBrush;
    public static readonly Pen HoldBodyStrokePen;
    public static readonly Pen HoldEdgeHighlightPen;
    public static readonly Pen HoldEdgeGlowPen;
    public static readonly Brush HoldTailCapBrush;

    // Chord Sync Lines
    public static readonly Pen ChordSyncPen;
    public static readonly Brush ChordSyncFillBrush;

    // Key Press Feedback & Burst Glows
    public static readonly Brush KeyManualGlowBrush;
    public static readonly Brush KeyAutoGlowBrush;
    public static readonly Brush KeyHitBurstBrush;

    // High-Resolution Built-in Textures (Nahida Theme)
    public static readonly BitmapSource? NoteTapBitmap;
    public static readonly BitmapSource? NoteHoldBitmap;
    public static readonly BitmapSource? NotePointBitmap;
    public static readonly BitmapSource? BackgroundBitmap;
    public static readonly BitmapSource? BackgroundBottomBitmap;
    public static readonly BitmapSource? BackgroundGradientBitmap;
    public static readonly BitmapSource? BackgroundArchBitmap;
    public static readonly BitmapSource? BackgroundWingBitmap;
    public static readonly BitmapSource? HitFlowerBitmap;
    public static readonly BitmapSource? HitShockwaveBitmap;
    public static readonly BitmapSource? HitSparkBitmap;
    public static readonly BitmapSource? HitSparkFlowerBitmap;
    public static readonly BitmapSource? HitAtlasBitmap;
    public static readonly BitmapSource[]? HitAtlasFrames;
    public static readonly BitmapSource? PreviewBitmap;

    private static readonly string[] AssetSubDirectories = ["skin", "ui", "hold", "skin-previews"];
    private static readonly Lazy<string?> ResourceDiagnosticValue = new(CreateResourceDiagnostic);

    /// <summary>Reports the resources actually decoded in this process, not files added after static initialization.</summary>
    public static string? ResourceDiagnostic => ResourceDiagnosticValue.Value;

    private static string? CreateResourceDiagnostic()
    {
        var missing = new List<string>();
        Check(NoteTapBitmap, "skin/UI_Icon_MusicGame_Note_01_Nahida.png");
        Check(NoteHoldBitmap, "skin/UI_Icon_MusicGame_Note_02_Nahida.png");
        Check(BackgroundBitmap, "skin/UI_Img_MusicGame_Nahida_Bg_Light.png");
        Check(BackgroundGradientBitmap, "skin/UI_Img_MusicGame_Nahida_Bg_Light_Gradient.png");
        Check(BackgroundArchBitmap, "skin/UI_Img_MusicGame_Nahida_Bg_Mid00.png");
        Check(BackgroundWingBitmap, "skin/UI_Img_MusicGame_Nahida_Bg_Loop00.png");
        Check(BackgroundBottomBitmap, "ui/UI_Img_MusicGame_Gradient_Common.png");
        Check(PreviewBitmap, "skin-previews/nahida.jpg");
        if (!HoldEffectRenderer.HasSource) missing.Add("hold/{clover,ring,star,glow,dot}.png");
        return missing.Count == 0 ? null : "原生默认皮肤资源缺失或解码失败，已降级部分外观：" +
            string.Join("、", missing) + "。请恢复完整资源后重启应用。";

        void Check(BitmapSource? bitmap, string path) { if (bitmap == null) missing.Add(path); }
    }

    static MusicDefaultSkin()
    {
        // 1. Playfield & Tracks (Nahida Backdrop: 0x12314a)
        TrackBackgroundBrush = CreateSolidBrush(Color.FromArgb(180, 18, 49, 74));
        TrackLaneGuidePen = CreatePen(Color.FromArgb(50, 255, 255, 255), 1.2);
        TrackCenterGuidePen = CreatePen(Color.FromArgb(100, 126, 227, 196), 1.8);
        JudgementLinePen = CreatePen(Color.FromArgb(240, 255, 255, 255), 2.5);
        JudgementLineGlowBrush = CreateSolidBrush(Color.FromArgb(80, 255, 255, 255));

        // 2. Key Labels (P3 Style: Crisp deep blue-grey text 0x2b3d55 for high-contrast white pill buttons)
        KeyLabelTypeface = new Typeface(
            new FontFamily("Segoe UI, Microsoft YaHei, Arial, sans-serif"),
            FontStyles.Normal,
            FontWeights.SemiBold,
            FontStretches.Normal);
        KeyLabelBrush = CreateSolidBrush(Color.FromArgb(255, 43, 61, 85));

        // 3. Tap Note Vector Fallback (Emerald Sumeru clover accent: #7fe3c4)
        TapNoteFillBrush = CreateSolidBrush(Color.FromArgb(240, 127, 227, 196));
        TapNoteStrokePen = CreatePen(Color.FromArgb(255, 255, 255, 255), 1.5);

        // 4. Vector fallback for skins without a beam texture. The default reference
        // beam uses MusicHoldBeamTexture and 144/124/10 design-pixel geometry.
        var holdGradient = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0.0),
            EndPoint = new Point(0.5, 1.0)
        };
        holdGradient.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * 0.04), 127, 227, 196), 0.0));
        holdGradient.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * 0.10), 127, 227, 196), 0.20));
        holdGradient.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * 0.20), 127, 227, 196), 0.50));
        holdGradient.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * 0.30), 172, 255, 241), 0.80));
        holdGradient.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * 0.38), 172, 255, 241), 1.0));
        holdGradient.Freeze();
        HoldBodyBrush = holdGradient;

        // Active holding body vertical gradient (when held down, alpha 0.06 -> 0.45)
        var activeHoldGradient = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0.0),
            EndPoint = new Point(0.5, 1.0)
        };
        activeHoldGradient.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * 0.06), 140, 240, 210), 0.0));
        activeHoldGradient.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * 0.14), 140, 240, 210), 0.20));
        activeHoldGradient.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * 0.26), 150, 245, 220), 0.50));
        activeHoldGradient.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * 0.38), 180, 255, 240), 0.80));
        activeHoldGradient.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * 0.45), 200, 255, 245), 1.0));
        activeHoldGradient.Freeze();
        HoldBodyActiveBrush = activeHoldGradient;

        HoldBodyStrokePen = CreatePen(Color.FromArgb(220, 220, 255, 240), 1.5);
        HoldTailCapBrush = CreateSolidBrush(Color.FromArgb(240, 198, 232, 107));

        // Lateral edge parallel highlight line: 1.2px, alpha 0.04 -> 0.45
        var edgeGrad = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0.0),
            EndPoint = new Point(0.5, 1.0)
        };
        edgeGrad.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * 0.04), 172, 255, 241), 0.0));
        edgeGrad.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * 0.16), 255, 245, 240), 0.25));
        edgeGrad.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * 0.30), 255, 250, 245), 0.70));
        edgeGrad.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * 0.45), 255, 255, 255), 1.0));
        edgeGrad.Freeze();
        HoldEdgeHighlightPen = new Pen(edgeGrad, 1.2);
        HoldEdgeHighlightPen.Freeze();

        // Lateral edge soft feathering outer glow: 4.0px, alpha 0.0 -> 0.30
        var edgeGlowGrad = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0.0),
            EndPoint = new Point(0.5, 1.0)
        };
        edgeGlowGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0, 127, 227, 196), 0.0));
        edgeGlowGrad.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * 0.08), 127, 227, 196), 0.25));
        edgeGlowGrad.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * 0.20), 172, 255, 241), 0.70));
        edgeGlowGrad.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * 0.30), 172, 255, 241), 1.0));
        edgeGlowGrad.Freeze();
        HoldEdgeGlowPen = new Pen(edgeGlowGrad, 4.0);
        HoldEdgeGlowPen.Freeze();

        // 5. Chord Sync Lines (Warm ivory white luminous ribbon connecting simultaneous notes: 0xffffd1)
        ChordSyncPen = CreatePen(Color.FromArgb(240, 255, 252, 220), 2.0);
        ChordSyncFillBrush = CreateSolidBrush(Color.FromArgb(140, 255, 250, 210));

        // 6. Feedback & Judgement Glow (Soft blue for manual, Sumeru teal for auto)
        KeyManualGlowBrush = CreateSolidBrush(Color.FromArgb(160, 120, 210, 255));
        KeyAutoGlowBrush = CreateSolidBrush(Color.FromArgb(180, 126, 227, 196));
        KeyHitBurstBrush = CreateSolidBrush(Color.FromArgb(200, 255, 255, 240));

        // 7. Load High-Resolution Cutout Assets (Nahida / Sumeru Theme)
        NoteTapBitmap = LoadAssetBitmap("Reference/assets/skin/UI_Icon_MusicGame_Note_01_Nahida.png", "UI_Icon_MusicGame_Note_01_Nahida.png");
        NoteHoldBitmap = LoadAssetBitmap("Reference/assets/skin/UI_Icon_MusicGame_Note_02_Nahida.png", "UI_Icon_MusicGame_Note_02_Nahida.png");
        NotePointBitmap = LoadAssetBitmap("Reference/assets/ui/UI_Img_MusicGame_Note_Point.png", "UI_Img_MusicGame_Note_Point.png");
        BackgroundBottomBitmap = LoadAssetBitmap("Reference/assets/ui/UI_Img_MusicGame_Gradient_Common.png", "UI_Img_MusicGame_Gradient_Common.png");
        BackgroundBitmap = LoadAssetBitmap("Reference/assets/skin/UI_Img_MusicGame_Nahida_Bg_Light.png", "UI_Img_MusicGame_Nahida_Bg_Light.png");
        BackgroundGradientBitmap = LoadAssetBitmap("Reference/assets/skin/UI_Img_MusicGame_Nahida_Bg_Light_Gradient.png", "UI_Img_MusicGame_Nahida_Bg_Light_Gradient.png");
        BackgroundArchBitmap = LoadAssetBitmap("Reference/assets/skin/UI_Img_MusicGame_Nahida_Bg_Mid00.png", "UI_Img_MusicGame_Nahida_Bg_Mid00.png");
        BackgroundWingBitmap = LoadAssetBitmap("Reference/assets/skin/UI_Img_MusicGame_Nahida_Bg_Loop00.png", "UI_Img_MusicGame_Nahida_Bg_Loop00.png");
        HitFlowerBitmap = LoadAssetBitmap("nahida_flower.png");
        HitShockwaveBitmap = LoadAssetBitmap("nahida_shockwave.png");
        HitSparkBitmap = LoadAssetBitmap("nahida_spark_dot.png");
        HitSparkFlowerBitmap = LoadAssetBitmap("nahida_spark_flower.png");
        HitAtlasBitmap = LoadAssetBitmap("nahida-hit-atlas.png");

        if (HitAtlasBitmap != null)
        {
            var frames = new BitmapSource[64];
            for (int i = 0; i < 64; i++)
            {
                int col = i % 8;
                int row = i / 8;
                var cropped = new CroppedBitmap(HitAtlasBitmap, new Int32Rect(col * 512, row * 512, 512, 512));
                cropped.Freeze();
                frames[i] = cropped;
            }
            HitAtlasFrames = frames;
        }

        PreviewBitmap = LoadAssetBitmap("Reference/assets/skin-previews/nahida.jpg", "nahida.jpg");
    }

    public static FormattedText CreateKeyLabelText(string label, double fontSize, double pixelsPerDip = 1.0)
    {
        return new FormattedText(
            label,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            KeyLabelTypeface,
            fontSize,
            KeyLabelBrush,
            pixelsPerDip);
    }

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

    private static BitmapSource? LoadAssetBitmap(string fileName, params string[] fallbacks)
    {
        try
        {
            string? path = ResolveAssetPath(fileName);
            if (path == null)
            {
                foreach (string fallback in fallbacks)
                {
                    path = ResolveAssetPath(fallback);
                    if (path != null) break;
                }
            }
            if (path == null || !File.Exists(path)) return null;

            using var diagnostic = MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.StaticDecodeMs);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    internal static string? ResolveAssetPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        if (Path.IsPathRooted(fileName) && File.Exists(fileName))
            return fileName;

        string normalized = fileName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string baseName = Path.GetFileName(normalized);
        string? strippedHoldName = baseName.StartsWith("hold-", StringComparison.OrdinalIgnoreCase)
            ? baseName.Substring("hold-".Length)
            : null;

        foreach (string root in GetOverlaySearchRoots())
        {
            // The versioned subset ships with clean checkouts; legacy Reference assets remain compatible.
            string nativeRelative = normalized.StartsWith("Reference" + Path.DirectorySeparatorChar + "assets" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
                ? normalized.Substring(("Reference/assets/").Length) : normalized;
            string native = Path.Combine(root, "NativeDefault", nativeRelative);
            if (File.Exists(native)) return native;

            // 1. Direct path relative to Assets/MusicOverlay (e.g. Reference/catalog.json, nahida-hit-atlas.png, nahida_flower.png)
            string direct = Path.Combine(root, normalized);
            if (File.Exists(direct)) return direct;

            // 2. Relative to Reference/assets/ (e.g. skin/..., hold/..., ui/..., skin-previews/...)
            string inAssets = Path.Combine(root, "Reference", "assets", normalized);
            if (File.Exists(inAssets)) return inAssets;

            // 3. Search in known subdirectories of Reference/assets/ (skin, ui, hold, skin-previews)
            foreach (string subDir in AssetSubDirectories)
            {
                string inSub = Path.Combine(root, "Reference", "assets", subDir, baseName);
                if (File.Exists(inSub)) return inSub;
            }

            // 4. Backward compatibility for hold-* files redirected to Reference/assets/hold/*.png
            if (strippedHoldName != null)
            {
                string inHold = Path.Combine(root, "Reference", "assets", "hold", strippedHoldName);
                if (File.Exists(inHold)) return inHold;

                foreach (string subDir in AssetSubDirectories)
                {
                    string inSub = Path.Combine(root, "Reference", "assets", subDir, strippedHoldName);
                    if (File.Exists(inSub)) return inSub;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> GetOverlaySearchRoots()
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfValid(string? path, List<string> list)
        {
            if (!string.IsNullOrEmpty(path) && visited.Add(path) && Directory.Exists(path))
            {
                list.Add(path);
            }
        }

        var roots = new List<string>();

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        AddIfValid(Path.Combine(baseDir, "Assets", "MusicOverlay"), roots);

        string cwd = Directory.GetCurrentDirectory();
        AddIfValid(Path.Combine(cwd, "Assets", "MusicOverlay"), roots);

        try
        {
            var dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 6 && dir != null; i++)
            {
                AddIfValid(Path.Combine(dir.FullName, "BetterPetitPlanet", "Assets", "MusicOverlay"), roots);
                AddIfValid(Path.Combine(dir.FullName, "Assets", "MusicOverlay"), roots);
                dir = dir.Parent;
            }
        }
        catch { }

        try
        {
            var dir = new DirectoryInfo(cwd);
            for (int i = 0; i < 6 && dir != null; i++)
            {
                AddIfValid(Path.Combine(dir.FullName, "BetterPetitPlanet", "Assets", "MusicOverlay"), roots);
                AddIfValid(Path.Combine(dir.FullName, "Assets", "MusicOverlay"), roots);
                dir = dir.Parent;
            }
        }
        catch { }

        return roots;
    }
}
