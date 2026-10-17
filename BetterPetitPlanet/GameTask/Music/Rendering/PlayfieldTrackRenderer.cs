using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Skins;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

/// <summary>
/// Static playfield track, judgement line, background decoration, and keycap renderer.
/// Renders Sumeru background arches, wings, track guides, luminous judgement bar, and keycaps into WPF DrawingContext.
/// </summary>
public static class PlayfieldTrackRenderer
{
    /// <summary>
    /// Renders static track background, lane guides, judgement line, and key labels.
    /// </summary>
    public static void DrawTracks(
        DrawingContext dc,
        double width,
        double height,
        MusicOverlayLayoutConfig layout,
        MusicOverlayEffectsConfig effects,
        double pixelsPerDip = 1.0,
        MusicSkin? skin = null, bool includeLabels = true, bool includePoints = true)
    {
        if (width <= 0 || height <= 0) return;
        skin ??= MusicSkin.Default;

        double judgeY = MusicPlayfieldGeometry.GetJudgementY(height, layout);
        double spawnY = MusicPlayfieldGeometry.GetSpawnY(height, layout);

        if (skin.Reference is { } reference)
            ReferenceBackgroundRenderer.Draw(dc, width, height, layout, effects, reference, pixelsPerDip);
        else MusicBackgroundRenderer.Draw(dc, width, height, layout, effects, skin);

        // 2. Vertical guide lines for each track
        if (effects.LaneGuides)
        {
            for (int i = 0; i < MusicPlayfieldGeometry.Lanes.Length; i++)
            {
                double laneX = MusicPlayfieldGeometry.GetLaneX(i, width, layout);
                var pen = (i == 3) ? skin.TrackCenterGuidePen : skin.TrackLaneGuidePen;
                dc.DrawLine(pen, new Point(laneX, spawnY), new Point(laneX, judgeY));
            }
        }

        // 3. Judgement line across all 7 tracks (P3 benchmark: continuous horizontal luminous bar)
        double scaleFactor = (width / 1920.0) * layout.Scale;
        double minX = MusicPlayfieldGeometry.GetLaneX(0, width, layout) - (90.0 * scaleFactor);
        double maxX = MusicPlayfieldGeometry.GetLaneX(6, width, layout) + (90.0 * scaleFactor);

        var lineBrush = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));
        lineBrush.Freeze();
        dc.DrawLine(skin.IsCustom ? skin.JudgementLinePen : new Pen(lineBrush, 3 * scaleFactor), new Point(minX, judgeY), new Point(maxX, judgeY));
        if (includePoints) DrawJudgePoints(dc, width, height, layout, skin);

        if (includeLabels) DrawLabels(dc, width, height, layout, effects, pixelsPerDip, skin);
    }

    public static void DrawJudgePoints(DrawingContext dc, double width, double height, MusicOverlayLayoutConfig layout, MusicSkin? skin = null)
    {
        double scale = width / 1920 * layout.Scale;
        double y = MusicPlayfieldGeometry.GetJudgementY(height, layout);
        var ring = new Pen(new SolidColorBrush(Color.FromArgb(115, 255, 255, 255)), 1.5 * scale);
        ring.Freeze();
        for (int i = 0; i < MusicPlayfieldGeometry.Lanes.Length; i++)
        {
            var center = new Point(MusicPlayfieldGeometry.GetLaneX(i, width, layout), y);
            if (skin?.IsCustom == true && skin.NotePointBitmap is { } point)
            {
                double size = Math.Max(16, 38 * scale);
                dc.DrawImage(point, new Rect(center.X - size / 2, center.Y - size / 2, size, size));
                continue;
            }
            dc.DrawEllipse(null, ring, center, 18 * scale, 18 * scale);
            dc.DrawEllipse(Brushes.White, null, center, 13 * scale, 13 * scale);
        }
    }

    public static void DrawLabels(DrawingContext dc, double width, double height,
        MusicOverlayLayoutConfig layout, MusicOverlayEffectsConfig effects, double pixelsPerDip = 1, MusicSkin? skin = null)
    {
        skin ??= MusicSkin.Default;
        double judgeY = MusicPlayfieldGeometry.GetJudgementY(height, layout);
        double scaleFactor = width / 1920 * layout.Scale;
        if (!effects.ShowKeyLabels) return;
        for (int i = 0; i < MusicPlayfieldGeometry.LaneKeys.Length; i++)
        {
            double x = MusicPlayfieldGeometry.GetLaneX(i, width, layout);
            var text = skin.CreateKeyLabelText(MusicPlayfieldGeometry.LaneKeys[i], 30 * scaleFactor, pixelsPerDip);
            double centerY = judgeY + 56 * scaleFactor;
            double pad = 14 * scaleFactor;
            var rect = new Rect(x - text.Width / 2 - pad, centerY - text.Height / 2 - pad * .3,
                text.Width + pad * 2, text.Height + pad * .6);
            dc.PushOpacity(.82);
            dc.DrawRoundedRectangle(Brushes.White, null, rect, 8 * scaleFactor, 8 * scaleFactor);
            dc.Pop();
            dc.DrawText(text, new Point(x - text.Width / 2, centerY - text.Height / 2));
        }
    }
}
