using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Media;
using BetterPetitPlanet.GameTask.Music.Skins;

namespace BetterPetitPlanet.Test;

public sealed class MusicSkinColorTest
{
    public static IEnumerable<object[]> UnsupportedColors => new[]
    {
        "ContextColor file:///C:/nonexistent-skin-profile.icc 1,0,0,0",
        "ContextColor http://example.invalid/profile.icc 1,0,0,0",
        "ContextColor https://example.invalid/profile.icc 1,0,0,0",
        "file:///C:/nonexistent-skin-profile.icc", "https://example.invalid/color",
        "pack://application:,,,/profile.icc", "sc#1,1,0,0", "#12345", "#GGG", "#123456789", "Red;Blue", ""
    }.Select(value => new object[] { value });

    [Theory]
    [MemberData(nameof(UnsupportedColors))]
    public void Manifest_RejectsUnsupportedColorBeforeAnyWpfConversion(string value)
    {
        using var fixture = new MusicSkinFixture();
        WriteColor(fixture, "primaryAccent", value);
        // Only validate metadata: the failing baseline must never send URI syntax to ColorConverter.
        var error = Assert.Throws<InvalidOperationException>(() => new MusicSkinPackageValidator().ReadManifest(fixture.Package, "fixture"));
        Assert.Contains("颜色", error.Message);
        Assert.Contains("primaryAccent", error.Message);
    }

    [Theory]
    [InlineData("secondaryAccent")]
    [InlineData("holdColor")]
    [InlineData("trackBackground")]
    [InlineData("laneGuide")]
    [InlineData("centerGuide")]
    [InlineData("judgementLine")]
    [InlineData("keyLabel")]
    [InlineData("keyAutoGlow")]
    [InlineData("keyManualGlow")]
    public void EveryColorField_IsValidated(string field)
    {
        using var fixture = new MusicSkinFixture();
        WriteColor(fixture, field, "ContextColor file:///C:/nonexistent-skin-profile.icc 1,0,0,0");
        var error = Assert.Throws<InvalidOperationException>(() => new MusicSkinPackageValidator().ValidateDirectory(fixture.Package, "fixture"));
        Assert.Contains(field, error.Message);
    }

    [Fact]
    public void PublicParser_RejectsExtendedColorSyntax() =>
        Assert.Equal(Colors.Purple, MusicSkinRepository.ParseColor("sc#1,1,0,0", Colors.Purple));

    [Theory]
    [MemberData(nameof(UnsupportedColors))]
    public void PublicParser_UnsupportedTextReturnsFallbackWithoutConversion(string value) =>
        Assert.Equal(Colors.Purple, MusicSkinRepository.ParseColor(value, Colors.Purple));

    [Fact]
    public async Task RejectedManifest_ReturnsClearDiagnosticBeforeImageDecode()
    {
        using var fixture = new MusicSkinFixture();
        WriteColor(fixture, "primaryAccent", "ContextColor file:///C:/nonexistent-skin-profile.icc 1,0,0,0");
        var repo = new MusicSkinRepository(fixture.Root, limits: new());
        var result = await repo.LoadSkinResultAsync("fixture");
        Assert.True(result.UsedFallback);
        Assert.Contains("colors.primaryAccent", result.Diagnostic);
        Assert.Equal(0, repo.AssetCache.CachedCount);
    }

    [Theory]
    [InlineData("#123", 255, 17, 34, 51)]
    [InlineData("#8123", 136, 17, 34, 51)]
    [InlineData("#A1b2C3", 255, 161, 178, 195)]
    [InlineData("#80A1b2C3", 128, 161, 178, 195)]
    [InlineData("Red", 255, 255, 0, 0)]
    [InlineData("dArKsLaTeBlUe", 255, 72, 61, 139)]
    [InlineData("Transparent", 0, 255, 255, 255)]
    [InlineData(" #123 ", 255, 17, 34, 51)]
    public void SafeHexAndNamedColors_RemainCompatible(string value, byte alpha, byte red, byte green, byte blue)
    {
        using var fixture = new MusicSkinFixture();
        WriteColor(fixture, "primaryAccent", value);
        new MusicSkinPackageValidator().ValidateDirectory(fixture.Package, "fixture");
        Assert.Equal(Color.FromArgb(alpha, red, green, blue), MusicSkinRepository.ParseColor(value, Colors.Purple));
    }

    private static void WriteColor(MusicSkinFixture fixture, string field, string value)
    {
        var path = Path.Combine(fixture.Package, "skin.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!;
        manifest["colors"] = new JsonObject { [field] = value };
        File.WriteAllText(path, manifest.ToJsonString());
    }
}
