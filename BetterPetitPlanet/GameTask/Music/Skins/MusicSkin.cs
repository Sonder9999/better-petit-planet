using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.GameTask.Music.Rendering;

namespace BetterPetitPlanet.GameTask.Music.Skins;

/// <summary>
/// Fully resolved and thread-safe runtime skin package with frozen WPF resources.
/// All brushes, pens, and bitmaps are strictly frozen for zero-allocation cross-thread rendering.
/// </summary>
public sealed class MusicSkin
{
    public string Id { get; init; } = "default";
    public string DisplayName { get; init; } = "原生默认";
    public string? Author { get; init; }
    public bool IsCustom { get; init; }
    public ReferenceSkinResources? Reference { get; init; }

    // Track & Guides
    public Brush TrackBackgroundBrush { get; init; } = MusicDefaultSkin.TrackBackgroundBrush;
    public Pen TrackLaneGuidePen { get; init; } = MusicDefaultSkin.TrackLaneGuidePen;
    public Pen TrackCenterGuidePen { get; init; } = MusicDefaultSkin.TrackCenterGuidePen;
    public Pen JudgementLinePen { get; init; } = MusicDefaultSkin.JudgementLinePen;
    public Brush JudgementLineGlowBrush { get; init; } = MusicDefaultSkin.JudgementLineGlowBrush;

    // Key Labels
    public Brush KeyLabelBrush { get; init; } = MusicDefaultSkin.KeyLabelBrush;
    public Typeface KeyLabelTypeface { get; init; } = MusicDefaultSkin.KeyLabelTypeface;

    // Notes & Hold Beams
    public Brush TapNoteFillBrush { get; init; } = MusicDefaultSkin.TapNoteFillBrush;
    public Pen TapNoteStrokePen { get; init; } = MusicDefaultSkin.TapNoteStrokePen;
    public Brush HoldBodyBrush { get; init; } = MusicDefaultSkin.HoldBodyBrush;
    public Brush HoldBodyActiveBrush { get; init; } = MusicDefaultSkin.HoldBodyActiveBrush;
    public Pen HoldBodyStrokePen { get; init; } = MusicDefaultSkin.HoldBodyStrokePen;
    public Pen HoldEdgeHighlightPen { get; init; } = MusicDefaultSkin.HoldEdgeHighlightPen;
    public Pen HoldEdgeGlowPen { get; init; } = MusicDefaultSkin.HoldEdgeGlowPen;
    public Brush HoldTailCapBrush { get; init; } = MusicDefaultSkin.HoldTailCapBrush;

    // Chords
    public Pen ChordSyncPen { get; init; } = MusicDefaultSkin.ChordSyncPen;
    public Brush ChordSyncFillBrush { get; init; } = MusicDefaultSkin.ChordSyncFillBrush;

    // Feedback & Glows
    public Brush KeyManualGlowBrush { get; init; } = MusicDefaultSkin.KeyManualGlowBrush;
    public Brush KeyAutoGlowBrush { get; init; } = MusicDefaultSkin.KeyAutoGlowBrush;
    public Brush KeyHitBurstBrush { get; init; } = MusicDefaultSkin.KeyHitBurstBrush;

    // Texture Assets (Frozen BitmapSources, null if using vector fallback)
    public BitmapSource? NoteTapBitmap { get; init; }
    public BitmapSource? NoteHoldBitmap { get; init; }
    public BitmapSource? NotePointBitmap { get; init; }
    public BitmapSource? BackgroundBitmap { get; init; }
    public BitmapSource? BackgroundGradientBitmap { get; init; }
    public BitmapSource? BackgroundArchBitmap { get; init; }
    public BitmapSource? BackgroundWingBitmap { get; init; }
    public BitmapSource? HoldBeamBitmap { get; init; }
    public BitmapSource? HitFlowerBitmap { get; init; }
    public BitmapSource? HitShockwaveBitmap { get; init; }
    public BitmapSource? HitSparkBitmap { get; init; }
    public BitmapSource? HitSparkFlowerBitmap { get; init; }
    public BitmapSource? HitRayBitmap { get; init; }
    public BitmapSource[]? HitAtlasFrames { get; init; }
    public BitmapSource? PreviewBitmap { get; init; }

    // Hit Effect Properties
    public double HitEffectDurationMs { get; init; } = 280.0;
    public double HitEffectScale { get; init; } = 1.0;

    /// <summary>
    /// Singleton instance for the zero-asset, built-in native default skin.
    /// Bitmaps remain null on this baseline instance so that consumer asset release assertions pass strictly.
    /// In built-in mode, high-resolution rendering textures are provided via MusicDefaultSkin.
    /// </summary>
    public static MusicSkin Default { get; } = new()
    {
        Id = "default",
        DisplayName = "原生默认 (静思浮梦)",
        Author = "BetterPetitPlanet",
        IsCustom = false
    };

    public FormattedText CreateKeyLabelText(string label, double fontSize, double pixelsPerDip = 1.0)
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
}
