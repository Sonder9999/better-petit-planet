using BetterPetitPlanet.GameTask.Music.Skins;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.Core.Config;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BetterPetitPlanet.Test;

public class MusicReferenceSkinCatalogTest
{
    [Fact]
    public void ReferenceNotes_PreserveOriginalNonSquareTextureSize() => MusicRenderingTestHost.Run(() =>
    {
        var definition = new ReferenceSkinDefinition
        {
            Id = "neuvillette",
            NoteTextures = ["hold.png", "tap.png"],
            JudgePointTexture = "point.png"
        };
        var mockBitmap = BitmapSource.Create(153, 112, 96, 96, PixelFormats.Pbgra32, null, new byte[153 * 112 * 4], 153 * 4);
        var images = new Dictionary<string, ReferenceSkinImage>
        {
            ["hold.png"] = new(mockBitmap, 153, 112),
            ["tap.png"] = new(mockBitmap, 153, 112),
            ["point.png"] = new(mockBitmap, 153, 112)
        };
        var skin = new MusicSkin
        {
            Id = "builtin:neuvillette",
            NoteHoldBitmap = mockBitmap,
            NoteTapBitmap = mockBitmap,
            Reference = new ReferenceSkinResources(definition, images)
        };

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) MusicPlayfieldDrawing.DrawNotes(dc,
            [new PerformanceEvent { Key = "A", TimeMs = 1000, Kind = MusicNoteKind.Hold, DurationMs = 1600 },
             new PerformanceEvent { Key = "D", TimeMs = 1800 }],
            1500, 2000, 1920, 1080, new MusicOverlayLayoutConfig(), new MusicOverlayEffectsConfig(), ["A"], skin);
        var drawings = MusicReferenceEffectsTest.Descendants(visual.Drawing).OfType<ImageDrawing>();
        foreach (var image in drawings.Where(image => ReferenceEquals(image.ImageSource, skin.NoteHoldBitmap) || ReferenceEquals(image.ImageSource, skin.NoteTapBitmap)))
        {
            Assert.Equal(153, image.Rect.Width);
            Assert.Equal(112, image.Rect.Height);
        }
    });

    [Fact]
    public void BuiltInCatalog_OffersAllTwentyFiveReferenceThemesWithoutLocalPackages()
    {
        var repository = new MusicSkinRepository(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var skins = repository.GetAvailableSkins().Where(skin => skin.IsBuiltIn).ToArray();
        Assert.Equal(25, skins.Length);
        Assert.Equal(25, skins.Select(skin => skin.Id).Distinct().Count());
        Assert.Contains(skins, skin => skin.Id == "default");
        Assert.Contains(skins, skin => skin.Id == "builtin:furina");
        Assert.Contains(skins, skin => skin.Id == "builtin:columbina");
    }

    [Fact]
    public async Task BuiltInSkin_LoadWithoutFramesArchive_LoadsReferenceSuccessfully()
    {
        var repository = new MusicSkinRepository();
        var result = await repository.LoadSkinResultAsync("builtin:neuvillette");
        Assert.Equal("builtin:neuvillette", result.Skin.Id);
        Assert.NotNull(result.Skin.Reference);
        Assert.NotNull(result.Skin.NoteTapBitmap);
        Assert.NotNull(result.Skin.NoteHoldBitmap);
    }
}
