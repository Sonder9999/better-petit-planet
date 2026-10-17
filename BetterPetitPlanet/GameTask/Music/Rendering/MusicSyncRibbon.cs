using System;
using System.Windows;
using System.Windows.Media;
using BetterPetitPlanet.GameTask.Music.Skins;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

internal static class MusicSyncRibbon
{
    private static readonly Brush Fill = Frozen(Color.FromArgb(242, 255, 255, 209));
    private static readonly Brush Glow = Frozen(Color.FromArgb(46, 255, 255, 209));
    private static readonly Brush Outline = Frozen(Color.FromArgb(102, 200, 196, 148));
    private static readonly Brush Core = Frozen(Color.FromArgb(250, 255, 255, 255));

    public static void Draw(DrawingContext dc, double left, double right, double y, double scale, MusicSkin? skin = null)
    {
        double middle = 18 * scale, end = 36 * scale, flare = Math.Min(42 * scale, (right - left) * .35);
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(left, y - end / 2), true, true);
            path.PolyLineTo([
                new(left + flare * .45, y - middle * .58), new(left + flare, y - middle / 2),
                new(right - flare, y - middle / 2), new(right - flare * .45, y - middle * .58), new(right, y - end / 2),
                new(right, y + end / 2), new(right - flare * .45, y + middle * .58), new(right - flare, y + middle / 2),
                new(left + flare, y + middle / 2), new(left + flare * .45, y + middle * .58), new(left, y + end / 2)
            ], true, false);
        }
        geometry.Freeze();
        dc.DrawLine(new Pen(Glow, 24 * scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, new Point(left, y), new Point(right, y));
        dc.DrawGeometry(skin?.IsCustom == true ? skin.ChordSyncFillBrush : Fill,
            skin?.IsCustom == true ? skin.ChordSyncPen : new Pen(Outline, 1.5 * scale), geometry);
        dc.DrawLine(new Pen(Core, 2.8 * scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, new Point(left + 6 * scale, y), new Point(right - 6 * scale, y));
    }

    private static Brush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
}
