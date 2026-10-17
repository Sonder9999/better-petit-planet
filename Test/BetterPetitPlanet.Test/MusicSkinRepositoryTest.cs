using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.GameTask.Music.Skins;

namespace BetterPetitPlanet.Test;

internal sealed class MusicSkinFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "bpp-music-skin-tests", Guid.NewGuid().ToString("N"));
    public string Package => Path.Combine(Root, "fixture");
    public MusicSkinFixture()
    {
        Directory.CreateDirectory(Package);
        WriteImage("note.png");
        WriteManifest();
    }
    public void WriteImage(string name, int width = 16, int height = 16)
    {
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        image.Freeze();
        BitmapEncoder encoder = Path.GetExtension(name) == ".jpg" ? new JpegBitmapEncoder() : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(Package, name));
        encoder.Save(stream);
    }
    public void WriteManifest(int version = 1, string id = "fixture", string resource = "note.png", bool preview = false)
    {
        var hashes = Directory.GetFiles(Package).Where(p => Path.GetFileName(p) != "skin.json")
            .ToDictionary(p => Path.GetFileName(p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        File.WriteAllText(Path.Combine(Package, "skin.json"), JsonSerializer.Serialize(new {
            schemaVersion = version, id, displayName = "Fixture", capabilities = new[] { "native-v1" },
            contentHashes = hashes, preview = preview ? resource : null,
            textures = new { noteTap = resource, noteHold = resource }, effects = new { hitFlower = resource }
        }));
    }
    public void Dispose()
    {
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bpp-music-skin-tests")) + Path.DirectorySeparatorChar;
        string resolved = Path.GetFullPath(Root);
        if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || resolved == parent.TrimEnd(Path.DirectorySeparatorChar))
            throw new InvalidOperationException("Unsafe fixture cleanup path");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}

public class MusicSkinRepositoryTest
{
    [Fact]
    public void Scan_DoesNotOpenOrHashAssetFiles()
    {
        using var fixture = new MusicSkinFixture();
        using var locked = new FileStream(Path.Combine(fixture.Package, "note.png"), FileMode.Open, FileAccess.Read, FileShare.None);
        var repo = new MusicSkinRepository(fixture.Root, limits: new());
        Assert.Contains(repo.GetAvailableSkins(), skin => skin.Id == "fixture");
        Assert.Equal(0, repo.AssetCache.CachedCount);
    }

    [Fact]
    public void Scan_ReadsMetadataWithoutDecodingCovers()
    {
        using var fixture = new MusicSkinFixture();
        fixture.WriteManifest(preview: true);
        var repo = new MusicSkinRepository(fixture.Root, limits: new());
        var skins = repo.GetAvailableSkins();
        Assert.Contains(skins, s => s.Id == "default" && s.IsBuiltIn);
        Assert.Contains(skins, s => s.Id == "fixture" && !s.IsBuiltIn);
        Assert.Equal(0, repo.AssetCache.CachedCount);
        Assert.Null(skins.Single(s => s.Id == "fixture").IconSource);
    }

    [Theory]
    [InlineData(99, "fixture")]
    [InlineData(1, "different")]
    [InlineData(1, "../fixture")]
    public async Task InvalidManifest_IsExcludedFromScanAndLoad(int schema, string id)
    {
        using var fixture = new MusicSkinFixture();
        fixture.WriteManifest(schema, id);
        var repo = new MusicSkinRepository(fixture.Root, limits: new());
        Assert.All(repo.GetAvailableSkins(), skin => Assert.True(skin.IsBuiltIn));
        Assert.DoesNotContain(repo.GetAvailableSkins(), skin => skin.Id == "fixture");
        Assert.Same(MusicSkin.Default, await repo.LoadSkinAsync("fixture"));
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("sub/../../outside.png")]
    [InlineData(@"C:\Windows\payload.png")]
    [InlineData("x.dll")]
    [InlineData("note.png:payload")]
    public void PathsCannotEscapeOrLoadCode(string path)
    {
        using var fixture = new MusicSkinFixture();
        Assert.ThrowsAny<ArgumentException>(() => MusicSkinAssetCache.ValidateAndResolvePath(fixture.Package, path));
    }

    [Fact]
    public async Task RequiredTextureFailure_RejectsWholePackage()
    {
        using var fixture = new MusicSkinFixture();
        fixture.WriteManifest(resource: "missing.png");
        Assert.Same(MusicSkin.Default, await new MusicSkinRepository(fixture.Root, limits: new()).LoadSkinAsync("fixture"));
    }

    [Fact]
    public async Task ManifestDoesNotImplicitlyLoadNotePoint()
    {
        using var fixture = new MusicSkinFixture();
        fixture.WriteImage("UI_Img_MusicGame_Note_Point.png");
        fixture.WriteManifest();
        var skin = await new MusicSkinRepository(fixture.Root, limits: new()).LoadSkinAsync("fixture");
        Assert.Equal("fixture", skin.Id);
        Assert.Null(skin.NotePointBitmap);
    }

    [Fact]
    public async Task ValidPackage_LoadsFrozenResourcesAndClosesFiles()
    {
        using var fixture = new MusicSkinFixture();
        var skin = await new MusicSkinRepository(fixture.Root, limits: new()).LoadSkinAsync("fixture");
        Assert.Equal("fixture", skin.Id);
        Assert.True(skin.NoteTapBitmap!.IsFrozen);
        Assert.Same(skin.NoteTapBitmap, skin.NoteHoldBitmap);
        Assert.True(skin.HoldBodyBrush.IsFrozen);
        using var exclusive = File.Open(Path.Combine(fixture.Package, "note.png"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("bytes")]
    [InlineData("pixels")]
    [InlineData("image-bytes")]
    [InlineData("manifest-bytes")]
    public void InstalledPackage_EnforcesConfigurableLimits(string limit)
    {
        using var fixture = new MusicSkinFixture();
        var limits = limit switch
        {
            "count" => new MusicSkinResourceLimits { MaxFiles = 1 },
            "bytes" => new MusicSkinResourceLimits { MaxPackageBytes = 10 },
            "pixels" => new MusicSkinResourceLimits { MaxTotalImagePixels = 255 },
            "image-bytes" => new MusicSkinResourceLimits { MaxImageBytes = 8 },
            _ => new MusicSkinResourceLimits { MaxManifestBytes = 2 }
        };
        Assert.Throws<InvalidOperationException>(() => new MusicSkinPackageValidator(limits).ValidateDirectory(fixture.Package, "fixture"));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("signature")]
    [InlineData("code")]
    [InlineData("capabilities")]
    [InlineData("missing-schema")]
    public async Task InvalidPackage_IsRejectedWithoutPartialTextures(string kind)
    {
        using var fixture = new MusicSkinFixture();
        string image = Path.Combine(fixture.Package, "note.png");
        if (kind == "hash") fixture.WriteImage("note.png", 8, 8);
        if (kind == "signature")
        {
            File.WriteAllBytes(image, new byte[64]);
            fixture.WriteManifest();
        }
        if (kind == "code") File.WriteAllText(Path.Combine(fixture.Package, "payload.dll"), "data");
        string manifest = Path.Combine(fixture.Package, "skin.json");
        if (kind == "capabilities") File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("native-v1", "execute-js"));
        if (kind == "missing-schema") File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"schemaVersion\":1,", ""));
        var repo = new MusicSkinRepository(fixture.Root, limits: new());
        Assert.Same(MusicSkin.Default, await repo.LoadSkinAsync("fixture"));
        Assert.Equal(0, repo.AssetCache.CachedCount);
    }

    [Theory]
    [InlineData("hitEffectDurationMs", "-1")]
    [InlineData("hitEffectDurationMs", "1e100")]
    [InlineData("hitEffectScale", "-1")]
    [InlineData("hitEffectScale", "1e100")]
    [InlineData("hitEffectScale", "1e999")]
    public async Task InvalidEffectNumbers_AreRejectedBeforeRendering(string field, string value)
    {
        using var fixture = new MusicSkinFixture();
        string path = Path.Combine(fixture.Package, "skin.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"effects\":{", "\"effects\":{\"" + field + "\":" + value + ","));
        var result = await new MusicSkinRepository(fixture.Root, limits: new()).LoadSkinResultAsync("fixture");
        Assert.True(result.UsedFallback);
        Assert.NotNull(result.Diagnostic);
    }

    [Fact]
    public async Task MissingAndInvalidRequests_FallBackToDefault()
    {
        using var fixture = new MusicSkinFixture();
        var repo = new MusicSkinRepository(fixture.Root, limits: new());
        foreach (var id in new[] { "missing", "../fixture", "fixture/child", "CON", "fixture.", "" })
            Assert.Same(MusicSkin.Default, await repo.LoadSkinAsync(id));
        Assert.Same(MusicSkin.Default, await repo.LoadSkinAsync(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizeSourceHeader_IsRejectedBeforePixelDecode(bool jpeg)
    {
        using var fixture = new MusicSkinFixture();
        string path = Path.Combine(fixture.Package, jpeg ? "huge.jpg" : "huge.png");
        // Deliberately truncated after dimensions: a decoder must never see this oversized image.
        byte[] header = jpeg
            ? [0xff, 0xd8, 0xff, 0xc0, 0, 17, 8, 0x20, 0, 0x20, 0, 3]
            : [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0x20, 0, 0, 0, 0x20, 0, 8, 6];
        File.WriteAllBytes(path, header);
        var error = Assert.Throws<InvalidOperationException>(() => MusicSkinAssetCache.LoadBitmap(path, 16, 16));
        Assert.Contains("尺寸", error.Message);
    }
}
