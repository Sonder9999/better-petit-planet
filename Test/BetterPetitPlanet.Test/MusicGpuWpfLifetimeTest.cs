using System.Windows.Media;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Skins;
using Xunit;

namespace BetterPetitPlanet.Test;

public class MusicGpuWpfLifetimeTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingTextureFallback_HoldHeadStopsAtLineWhileTailSurvives(bool active)
    {
        var note = new PerformanceEvent { Key = "L", TimeMs = 5900, DurationMs = 1600, Kind = MusicNoteKind.Hold };
        var layout = new MusicOverlayLayoutConfig { Height = .5 };
        // A custom skin with no textures forces the exact fallback branch from the screenshot,
        // independently of whether the process has native default assets installed.
        var fallback = new MusicSkin { IsCustom = true };
        DrawingGroup Draw(double time)
        {
            var group = new DrawingGroup();
            using (var dc = group.Open())
                MusicPlayfieldDrawing.DrawNotes(dc, new[] { note }, time, 3000, 1920, 1080, layout,
                    new MusicOverlayEffectsConfig(), active ? new[] { "L" } : [], fallback, drawBeams: false, drawSync: false);
            return group;
        }
        var drawing = Draw(7200);
        Assert.Equal(2, drawing.Children.Count);
        var head = Assert.IsType<GeometryDrawing>(drawing.Children[0]);
        var tail = Assert.IsType<GeometryDrawing>(drawing.Children[1]);
        double judge = MusicPlayfieldGeometry.GetJudgementY(1080, layout);
        Assert.Equal(judge, head.Geometry.Bounds.Top + head.Geometry.Bounds.Height / 2, 6);
        Assert.True(tail.Geometry.Bounds.Top + tail.Geometry.Bounds.Height / 2 < judge);
        Assert.Empty(Draw(7500).Children);
    }
}
