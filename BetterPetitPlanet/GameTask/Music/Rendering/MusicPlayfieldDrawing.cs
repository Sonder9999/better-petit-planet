using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Skins;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

/// <summary>
/// High-performance native drawing routines rendering playfield tracks, notes, hold beams,
/// chord lines, and key feedback directly into WPF DrawingContext.
/// Zero per-note FrameworkElement creation, fully relying on frozen resources and supporting custom skin packages.
/// </summary>
public static class MusicPlayfieldDrawing
{
    private static BitmapSource? ResolveTexture(BitmapSource? skinBitmap, BitmapSource? defaultBitmap, bool isCustom) =>
        skinBitmap ?? (isCustom ? null : defaultBitmap);

    [ThreadStatic]
    private static List<PerformanceEvent>? s_chordNotesBuffer;
    /// <summary>
    /// Renders static track background, lane guides, judgement line, and key labels.
    /// This layer only needs to be redrawn when playfield size, layout, or skin change.
    /// </summary>
    public static void DrawTracks(
        DrawingContext dc,
        double width,
        double height,
        MusicOverlayLayoutConfig layout,
        MusicOverlayEffectsConfig effects,
        double pixelsPerDip = 1.0,
        MusicSkin? skin = null)
    {
        PlayfieldTrackRenderer.DrawTracks(dc, width, height, layout, effects, pixelsPerDip, skin);
    }

    /// <summary>
    /// Renders dynamic notes: chord sync lines, hold beams, and note caps for current visible frame.
    /// Supports textured notes (Nahida clover) and vector fallback.
    /// </summary>
    public static void DrawNotes(
        DrawingContext dc,
        IReadOnlyList<PerformanceEvent> visibleNotes,
        double displayTimeMs,
        int fallDurationMs,
        double width,
        double height,
        MusicOverlayLayoutConfig layout,
        MusicOverlayEffectsConfig effects,
        IReadOnlyList<string> activeKeys,
        MusicSkin? skin = null,
        bool drawBeams = true, bool drawSprites = true, bool drawSync = true)
    {
        using var diagnostic = MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.NotesMs);
        if (visibleNotes == null || visibleNotes.Count == 0 || width <= 0 || height <= 0) return;
        skin ??= MusicSkin.Default;

        var activeKeySet = activeKeys != null && activeKeys.Count > 0
            ? new HashSet<string>(activeKeys, StringComparer.OrdinalIgnoreCase)
            : null;

        var noteTapBitmap = ResolveTexture(skin.NoteTapBitmap, MusicDefaultSkin.NoteTapBitmap, skin.IsCustom);
        var noteHoldBitmap = ResolveTexture(skin.NoteHoldBitmap, MusicDefaultSkin.NoteHoldBitmap, skin.IsCustom);
        var holdBeamBitmap = ResolveTexture(skin.HoldBeamBitmap, MusicHoldBeamTexture.Default, skin.IsCustom);
        double judgeY = MusicPlayfieldGeometry.GetJudgementY(height, layout);

        // Reference scene order: beams, ribbons, source particles, note sprites.
        if (drawBeams)
        for (int i = 0; i < visibleNotes.Count; i++)
        {
            var note = visibleNotes[i];
            int duration = MusicChartIndex.GetNoteDuration(note);
            if (duration <= 0) continue;

            bool isHeld = activeKeySet != null && activeKeySet.Contains(note.Key);
            DrawHoldBeam(dc, note, displayTimeMs, fallDurationMs, width, height, layout, skin, isHeld, holdBeamBitmap);
        }

        // Flared ribbons connect each adjacent pair in the chord.
        if (drawSync && effects.SyncLines && visibleNotes.Count >= 2)
        {
            s_chordNotesBuffer ??= new List<PerformanceEvent>(32);
            s_chordNotesBuffer.Clear();
            for (int i = 0; i < visibleNotes.Count; i++)
            {
                s_chordNotesBuffer.Add(visibleNotes[i]);
            }
            s_chordNotesBuffer.Sort((a, b) => a.TimeMs != b.TimeMs ? a.TimeMs.CompareTo(b.TimeMs) : MusicPlayfieldGeometry.GetLaneIndex(a.Key).CompareTo(MusicPlayfieldGeometry.GetLaneIndex(b.Key)));

            double chordScale = (width / 1920.0) * layout.Scale;

            int start = 0;
            while (start < s_chordNotesBuffer.Count)
            {
                int end = start + 1;
                while (end < s_chordNotesBuffer.Count && s_chordNotesBuffer[end].TimeMs == s_chordNotesBuffer[start].TimeMs)
                {
                    end++;
                }

                int groupCount = end - start;
                if (groupCount >= 2)
                {
                    double chordTime = s_chordNotesBuffer[start].TimeMs;
                    if (displayTimeMs < chordTime)
                    {
                        double y = Math.Min(judgeY, MusicPlayfieldGeometry.TimeToY(chordTime, displayTimeMs, fallDurationMs, height, layout));
                        for (int k = start; k < end - 1; k++)
                            MusicSyncRibbon.Draw(dc,
                                MusicPlayfieldGeometry.GetLaneX(s_chordNotesBuffer[k].Key, width, layout),
                                MusicPlayfieldGeometry.GetLaneX(s_chordNotesBuffer[k + 1].Key, width, layout), y, chordScale, skin);
                    }
                }

                start = end;
            }
        }

        if (!drawSprites) return;

        // 3. Draw Tap Notes and Hold Head & Tail Blocks (Strictly scaled to 1920 design canvas)
        double notesScaleFactor = (width / 1920.0) * layout.Scale;
        double noteW = MusicPlayfieldGeometry.GetNoteWidth(width, layout);
        double noteH = MusicPlayfieldGeometry.GetNoteHeight(width, layout);
        Size TextureSize(int index)
        {
            if (skin.Reference is { } reference)
            {
                var image = reference.Images[reference.Definition.NoteTextures[index]];
                return new Size(image.SourceWidth * notesScaleFactor, image.SourceHeight * notesScaleFactor);
            }
            return new Size(noteW, noteW);
        }
        var holdSize = TextureSize(0);
        var tapSize = TextureSize(1);
        double cornerRadius = Math.Max(4.0, 8.0 * notesScaleFactor);

        for (int i = 0; i < visibleNotes.Count; i++)
        {
            var note = visibleNotes[i];
            int duration = MusicChartIndex.GetNoteDuration(note);

            double naturalY = MusicPlayfieldGeometry.TimeToY(
                note.TimeMs, displayTimeMs, fallDurationMs, height, layout);

            double laneX = MusicPlayfieldGeometry.GetLaneX(note.Key, width, layout);

            if (duration > 0)
            {
                double naturalTailY = MusicPlayfieldGeometry.TimeToY(
                    note.TimeMs + duration, displayTimeMs, fallDurationMs, height, layout);

                // Exit condition: if tail has penetrated/reached judgement line, skip all drawing of this hold note
                if (naturalTailY >= judgeY) continue;

                // Reference keeps the source hold head above the continuous particle layer.
                if (naturalTailY < judgeY)
                {
                    if (noteHoldBitmap != null)
                    {
                        var noteRect = new Rect(laneX - holdSize.Width / 2, Math.Min(judgeY, naturalY) - holdSize.Height / 2, holdSize.Width, holdSize.Height);
                        dc.DrawImage(noteHoldBitmap, noteRect);
                    }
                    else
                    {
                        // The fallback cap has the same lifetime and judgement-line anchor
                        // as the textured cap. A surviving sustain tail must not drag its
                        // head into the key-label area after the head has been judged.
                        var noteRect = new Rect(laneX - noteW / 2.0, Math.Min(judgeY, naturalY) - noteH / 2.0, noteW, noteH);
                        dc.DrawRoundedRectangle(
                            skin.TapNoteFillBrush,
                            skin.TapNoteStrokePen,
                            noteRect,
                            cornerRadius,
                            cornerRadius);
                    }
                }
                // Draw the hold tail above the head until it reaches judgement
                if (naturalTailY < judgeY)
                {
                    if (noteHoldBitmap != null)
                    {
                        var tailRect = new Rect(laneX - holdSize.Width / 2, naturalTailY - holdSize.Height / 2, holdSize.Width, holdSize.Height);
                        dc.DrawImage(noteHoldBitmap, tailRect);
                    }
                    else
                    {
                        double tailW = noteW * 0.85;
                        double tailH = noteH * 0.4;
                        var tailRect = new Rect(laneX - tailW / 2.0, naturalTailY - tailH / 2.0, tailW, tailH);
                        dc.DrawRoundedRectangle(
                            skin.HoldTailCapBrush,
                            skin.HoldBodyStrokePen,
                            tailRect,
                            cornerRadius / 2.0,
                            cornerRadius / 2.0);
                    }
                }

            }
            else
            {
                // A resolved tap is replaced by its independent hit animation.
                if (displayTimeMs >= note.TimeMs) continue;
                double headY = Math.Min(judgeY, naturalY);

                if (noteTapBitmap != null)
                {
                    var noteRect = new Rect(laneX - tapSize.Width / 2, headY - tapSize.Height / 2, tapSize.Width, tapSize.Height);
                    dc.DrawImage(noteTapBitmap, noteRect);
                }
                else
                {
                    var noteRect = new Rect(laneX - noteW / 2.0, headY - noteH / 2.0, noteW, noteH);
                    dc.DrawRoundedRectangle(
                        skin.TapNoteFillBrush,
                        skin.TapNoteStrokePen,
                        noteRect,
                        cornerRadius,
                        cornerRadius);
                }
            }
        }
    }

    /// <summary>
    /// Renders a single hold note beam strictly adhering to the 144px official specification:
    /// - Total width 144px, body width 124px, soft feathering edge margin 10px (@ 1920 design canvas).
    /// - Tail geometric extension: 20px extending behind the tail flower center to eliminate rendering seams.
    /// - Flat-cut head: locked precisely to judgement line (judgeY) during holding, wrapped by judgement particles and halo.
    /// - Gradient coloring with bilateral inner boundary highlight lines (2.4px highlight + 8px glow).
    /// </summary>
    public static void DrawHoldBeam(
        DrawingContext dc,
        PerformanceEvent note,
        double displayTimeMs,
        int fallDurationMs,
        double width,
        double height,
        MusicOverlayLayoutConfig layout,
        MusicSkin skin,
        bool isHeld,
        BitmapSource? holdBeamBitmap = null)
    {
        int duration = MusicChartIndex.GetNoteDuration(note);
        if (duration <= 0) return;

        double judgeY = MusicPlayfieldGeometry.GetJudgementY(height, layout);
        double naturalTailY = MusicPlayfieldGeometry.TimeToY(
            note.TimeMs + duration, displayTimeMs, fallDurationMs, height, layout);

        // Entire hold note exit check: if tail has penetrated or reached judgement line, skip beam rendering
        if (naturalTailY >= judgeY) return;

        var bounds = MusicPlayfieldGeometry.ComputeHoldBounds(
            note, displayTimeMs, fallDurationMs, width, height, layout, isHeld);

        // Head flat-cut: in holding state, head Y is locked strictly to judgement line judgeY.
        // Before reaching judge line or when not holding, follows natural head position.
        // Input controls active styling, while visible sustain content terminates at
        // the same line as its cap even when an input snapshot is late or unheld.
        double beamHeadY = Math.Min(judgeY, bounds.HeadY);

        // Tail geometric extension: extends 20px behind tail flower center (towards spawn / upstream)
        // so the tail flower perfectly covers the beam endpoint without gaps.
        double tailExtension = MusicPlayfieldGeometry.GetHoldBeamTailExtension(width, layout);
        double beamTailY = bounds.TailY - tailExtension;

        beamHeadY += 6 * width / 1920 * layout.Scale;
        double beamHeight = Math.Max(0, beamHeadY - beamTailY);
        if (beamHeight <= 0) return;

        holdBeamBitmap ??= ResolveTexture(skin.HoldBeamBitmap, MusicHoldBeamTexture.Default, skin.IsCustom);
        if (holdBeamBitmap != null)
        {
            var beamRect = new Rect(bounds.X, beamTailY, bounds.Width, beamHeight);
            dc.PushOpacity(isHeld ? 1 : .82);
            dc.DrawImage(holdBeamBitmap, beamRect);
            dc.Pop();
        }
        else
        {
            // Official reverse-engineered specification:
            // Total width: 144px, edge inset: 10px, body width: 124px (@ 1920 design canvas).
            double bodyWidth = MusicPlayfieldGeometry.GetHoldBeamBodyWidth(width, layout);
            double edgeInset = (bounds.Width - bodyWidth) / 2.0;
            double bodyX = bounds.X + edgeInset;
            var bodyRect = new Rect(bodyX, beamTailY, bodyWidth, beamHeight);

            var bodyBrush = isHeld ? skin.HoldBodyActiveBrush : skin.HoldBodyBrush;
            // Vertical gradient body fill covering 124px body
            dc.DrawRectangle(bodyBrush, null, bodyRect);

            // Bilateral inner boundaries at bodyX and bodyX + bodyWidth
            double leftX = bodyX;
            double rightX = bodyX + bodyWidth;

            // 1. Soft feathering outer glow (width 4.0px, alpha 0.0 -> 0.30)
            dc.DrawLine(skin.HoldEdgeGlowPen, new Point(leftX, beamTailY), new Point(leftX, beamHeadY));
            dc.DrawLine(skin.HoldEdgeGlowPen, new Point(rightX, beamTailY), new Point(rightX, beamHeadY));

            // 2. Parallel delicate highlight line (width 1.2px, alpha 0.04 -> 0.45)
            dc.DrawLine(skin.HoldEdgeHighlightPen, new Point(leftX, beamTailY), new Point(leftX, beamHeadY));
            dc.DrawLine(skin.HoldEdgeHighlightPen, new Point(rightX, beamTailY), new Point(rightX, beamHeadY));
        }
    }

    /// <summary>
    /// Renders active key press feedback, judge line hit bursts, and glow effects.
    /// Compatibility feedback entry point; the control renders held particles below note sprites.
    /// </summary>
    public static void DrawFeedback(
        DrawingContext dc,
        IReadOnlyList<string> activeKeys,
        IReadOnlyList<string> manualKeys,
        IReadOnlyList<string> autoKeys,
        double width,
        double height,
        MusicOverlayLayoutConfig layout,
        MusicOverlayEffectsConfig effects,
        MusicSkin? skin = null,
        IReadOnlyDictionary<string, double>? keyAlphas = null,
        IReadOnlyDictionary<string, double>? keyElapsedMs = null,
        IReadOnlySet<string>? holdingKeys = null,
        double pixelsPerDip = 1.0, bool drawEffects = true)
    {
        if (activeKeys == null || activeKeys.Count == 0 || width <= 0 || height <= 0) return;
        skin ??= MusicSkin.Default;

        double judgeY = MusicPlayfieldGeometry.GetJudgementY(height, layout);
        double scaleFactor = (width / 1920.0) * layout.Scale;

        var manualKeySet = manualKeys != null && manualKeys.Count > 0
            ? new HashSet<string>(manualKeys, StringComparer.OrdinalIgnoreCase)
            : null;

        for (int i = 0; i < activeKeys.Count; i++)
        {
            string key = activeKeys[i];
            double laneX = MusicPlayfieldGeometry.GetLaneX(key, width, layout);
            bool isManual = manualKeySet != null && manualKeySet.Contains(key);
            bool isHolding = holdingKeys != null && holdingKeys.Contains(key);

            double alpha = 1.0;
            if (keyAlphas != null && keyAlphas.TryGetValue(key, out double customAlpha))
            {
                alpha = Math.Clamp(customAlpha, 0.0, 1.0);
            }

            if (alpha <= 0.001) continue;

            // 1. Subtle, soft underlying glow (gentle halo)
            var glowBrush = isManual ? skin.KeyManualGlowBrush : skin.KeyAutoGlowBrush;
            dc.PushOpacity(Math.Clamp(alpha * 0.45, 0.0, 1.0));
            dc.DrawEllipse(glowBrush, null, new Point(laneX, judgeY), 48.0 * scaleFactor, 48.0 * scaleFactor);
            dc.Pop();

            double elapsed = (keyElapsedMs != null && keyElapsedMs.TryGetValue(key, out double el)) ? el : 0.0;

            if (isHolding)
            {
                // 2a. Multi-layer hold effect: ambient glow, expanding ring, breathing central symbol, and drifting particles
                if (effects.HoldGlow && drawEffects)
                {
                    int laneIdx = MusicPlayfieldGeometry.GetLaneIndex(key);
                    HoldEffectRenderer.DrawHoldingEffect(
                        dc, laneX, judgeY, elapsed, width, layout, skin, effects.Intensity, laneIdx >= 0 ? laneIdx : 0);
                }
            }
            else
            {
                // 2b. 64-frame continuous hit burst animation or 380ms cubic ease-out fallback
                if (effects.HitBursts && drawEffects)
                {
                    HitBurstEffectRenderer.DrawHitBurst(
                        dc, laneX, judgeY, elapsed, width, layout, skin, alpha * effects.Intensity);
                }
            }

            // 3. Active keycap button with glowing halo and crisp text
            if (effects.ShowKeyLabels)
            {
                double keyCapW = Math.Max(24.0, 52.0 * scaleFactor);
                double keyCapH = Math.Max(18.0, 36.0 * scaleFactor);
                double keyCapY = judgeY + Math.Max(12.0, 30.0 * scaleFactor);
                double cornerRadius = Math.Max(4.0, 8.0 * scaleFactor);
                var pillRect = new Rect(laneX - keyCapW / 2.0, keyCapY, keyCapW, keyCapH);

                var haloRect = pillRect;
                haloRect.Inflate(3.0 * scaleFactor, 3.0 * scaleFactor);
                dc.PushOpacity(Math.Clamp(alpha * 0.55, 0.0, 1.0));
                dc.DrawRoundedRectangle(glowBrush, null, haloRect, cornerRadius + 2.0, cornerRadius + 2.0);
                dc.Pop();

                var activePillBrush = new SolidColorBrush(Color.FromArgb((byte)(245 * alpha), 255, 255, 255));
                dc.DrawRoundedRectangle(activePillBrush, new Pen(glowBrush, 1.5), pillRect, cornerRadius, cornerRadius);

                double fontSize = Math.Max(11.0, 19.0 * scaleFactor);
                var formattedText = skin.CreateKeyLabelText(key, fontSize, pixelsPerDip);
                var textOrigin = new Point(
                    laneX - (formattedText.Width / 2.0),
                    keyCapY + ((keyCapH - formattedText.Height) / 2.0));
                dc.DrawText(formattedText, textOrigin);
            }
        }
    }
}
