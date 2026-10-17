using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.View.Controls.Music;

namespace BetterPetitPlanet.Test;

public class MusicReferenceEffectsTest
{
    [Fact]
    public void PreviewHit_StillDrawsOriginalAtlasFrameAt750Milliseconds() => MusicRenderingTestHost.Run(() =>
    {
        var control = new MusicPlayfieldControl();
        control.SetChart(new PerformanceTimeline { Events = [new() { Key = "S", TimeMs = 3000 }] });
        control.RenderSnapshot(MusicRenderSnapshot.Empty with { SessionId = "preview-test", PositionMs = 3750 }, 960, 540);
        var images = Descendants(((DrawingVisual)VisualTreeHelper.GetChild(control, 2)).Drawing)
            .OfType<ImageDrawing>().Where(x => x.ImageSource is CroppedBitmap).ToArray();
        Assert.Contains(images, x => ((CroppedBitmap)x.ImageSource).SourceRect == new Int32Rect(2560, 2560, 512, 512));
    });

    [Fact]
    public void HoldingNote_KeepsSourceHeadFlowerAboveJudgement() => MusicRenderingTestHost.Run(() =>
    {
        var visual = new DrawingVisual();
        var layout = new MusicOverlayLayoutConfig();
        var note = new PerformanceEvent { Key = "A", TimeMs = 1000, Kind = MusicNoteKind.Hold, DurationMs = 1600 };
        using (var dc = visual.RenderOpen())
            MusicPlayfieldDrawing.DrawNotes(dc, [note], 1500, 2000, 960, 540, layout,
                new MusicOverlayEffectsConfig(), ["A"]);
        double judgeY = MusicPlayfieldGeometry.GetJudgementY(540, layout);
        Assert.Contains(Descendants(visual.Drawing).OfType<ImageDrawing>(), image =>
            ReferenceEquals(image.ImageSource, MusicDefaultSkin.NoteHoldBitmap) &&
            Math.Abs(image.Rect.Y + image.Rect.Height / 2 - judgeY) < .01);
    });

    [Fact]
    public void HoldBeam_UsesReferenceTextureDimensions()
    {
        var layout = new MusicOverlayLayoutConfig();
        Assert.Equal(144, MusicPlayfieldGeometry.GetHoldBeamWidth(1920, layout));
        Assert.Equal(124, MusicPlayfieldGeometry.GetHoldBeamBodyWidth(1920, layout));
        Assert.Equal(10, MusicPlayfieldGeometry.GetHoldBeamEdgeInset(1920, layout));
    }

    internal static IEnumerable<Drawing> Descendants(Drawing? drawing)
    {
        if (drawing == null) yield break;
        yield return drawing;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var nested in Descendants(child)) yield return nested;
    }
}
