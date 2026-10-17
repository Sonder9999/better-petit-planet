using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.GameTask.Music.Skins;

namespace BetterPetitPlanet.Test;

public sealed class MusicSkinCacheTest
{
    [Fact]
    public void DecodeSize_IsPartOfCacheIdentity()
    {
        using var fixture = new MusicSkinFixture();
        fixture.WriteImage("note.png", 64, 32);
        var cache = new MusicSkinAssetCache();
        var small = cache.GetOrLoadBitmap(fixture.Package, "note.png", 8, 8)!;
        var large = cache.GetOrLoadBitmap(fixture.Package, "note.png", 32, 32)!;
        Assert.NotSame(small, large);
        Assert.Equal(8, small.PixelWidth);
        Assert.Equal(4, small.PixelHeight);
        Assert.Equal(32, large.PixelWidth);
        Assert.Equal(16, large.PixelHeight);
    }

    [Fact]
    public void Clear_DoesNotRemoveActiveResourceAccountingOrSharing()
    {
        using var fixture = new MusicSkinFixture();
        var cache = new MusicSkinAssetCache();
        var active = cache.GetOrLoadBitmap(fixture.Package, "note.png")!;
        cache.Clear();
        Assert.Equal(1, cache.CachedCount);
        Assert.Same(active, cache.GetOrLoadBitmap(fixture.Package, "note.png"));
        GC.KeepAlive(active);
    }

    [Fact]
    public void CacheDoesNotStronglyRetainUnusedDecodedResources()
    {
        using var fixture = new MusicSkinFixture();
        var cache = new MusicSkinAssetCache();
        var weak = LoadWithoutRetaining(cache, fixture);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(weak.TryGetTarget(out _));
        Assert.Equal(0, cache.CachedCount);
    }

    [Fact]
    public void LivePixels_StayChargedUntilExternalReferenceIsCollected()
    {
        using var fixture = new MusicSkinFixture();
        var cache = new MusicSkinAssetCache(new() { MaxDecodedBytes = 16 * 16 * 8 });
        var weak = FillBudgetAndRejectNext(cache, fixture);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(weak.TryGetTarget(out _));
        Assert.Equal(0, cache.LiveDecodedBytes);
        Assert.NotNull(cache.GetOrLoadBitmap(fixture.Package, "note.png", 8, 8));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<BitmapSource> FillBudgetAndRejectNext(MusicSkinAssetCache cache, MusicSkinFixture fixture)
    {
        var active = cache.GetOrLoadBitmap(fixture.Package, "note.png")!;
        Assert.Equal(16 * 16 * 8, cache.LiveDecodedBytes);
        cache.Clear();
        Assert.Equal(16 * 16 * 8, cache.LiveDecodedBytes);
        var error = Assert.Throws<InvalidOperationException>(() => cache.GetOrLoadBitmap(fixture.Package, "note.png", 8, 8));
        Assert.Contains("重试", error.Message);
        Assert.Same(active, cache.GetOrLoadBitmap(fixture.Package, "note.png"));
        return new(active);
    }

    [Fact]
    public async Task ConcurrentRequests_ShareSingleChargeAndDecode()
    {
        using var fixture = new MusicSkinFixture();
        var cache = new MusicSkinAssetCache(new() { MaxDecodedBytes = 16 * 16 * 8 });
        var images = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => cache.GetOrLoadBitmap(fixture.Package, "note.png"))));
        Assert.All(images, image => Assert.Same(images[0], image));
        Assert.Equal(1, cache.CachedCount);
        Assert.Equal(16 * 16 * 8, cache.LiveDecodedBytes);
        GC.KeepAlive(images);
    }

    [Fact]
    public void CanceledRequest_DoesNotReserveBudgetOrAffectExistingImage()
    {
        using var fixture = new MusicSkinFixture();
        var cache = new MusicSkinAssetCache();
        var image = cache.GetOrLoadBitmap(fixture.Package, "note.png");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => cache.GetOrLoadBitmap(fixture.Package, "note.png", 8, 8, cancellation.Token));
        Assert.Equal(16 * 16 * 8, cache.LiveDecodedBytes);
        Assert.Same(image, cache.GetOrLoadBitmap(fixture.Package, "note.png"));
    }

    [Fact]
    public async Task Repository_ReturnsBudgetDiagnosticWithoutPartialSkin()
    {
        using var fixture = new MusicSkinFixture();
        var repo = new MusicSkinRepository(fixture.Root, limits: new() { MaxDecodedBytes = 100 });
        var result = await repo.LoadSkinResultAsync("fixture");
        Assert.True(result.UsedFallback);
        Assert.Same(MusicSkin.Default, result.Skin);
        Assert.Equal("fixture", result.RequestedSkinId);
        Assert.Contains("预算", result.Diagnostic);
        Assert.Equal(0, repo.AssetCache.LiveDecodedBytes);
    }

    [Fact]
    public async Task Repository_UsesDisplayBoundsAndPreservesMissingIdentity()
    {
        using var fixture = new MusicSkinFixture();
        fixture.WriteImage("note.png", 128, 64);
        fixture.WriteManifest();
        var repo = new MusicSkinRepository(fixture.Root, limits: new());
        var result = await repo.LoadSkinResultAsync("fixture", new(24, 24));
        Assert.Null(result.Diagnostic);
        Assert.Equal(24, result.Skin.NoteTapBitmap!.PixelWidth);
        Assert.Equal(12, result.Skin.NoteTapBitmap.PixelHeight);
        var missing = await repo.LoadSkinResultAsync("missing");
        Assert.Equal("missing", missing.RequestedSkinId);
        Assert.Contains("未安装", missing.Diagnostic);
        Assert.Same(MusicSkin.Default, missing.Skin);
    }

    [Fact]
    public async Task FailedHalfPackage_DoesNotRetainImagesOrBudgetAfterCollection()
    {
        using var fixture = new MusicSkinFixture();
        fixture.WriteImage("broken.png");
        string brokenPath = Path.Combine(fixture.Package, "broken.png");
        File.WriteAllBytes(brokenPath, File.ReadAllBytes(brokenPath)[..33]);
        fixture.WriteManifest();
        string path = Path.Combine(fixture.Package, "skin.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"noteHold\":\"note.png\"", "\"noteHold\":\"broken.png\""));
        var repo = new MusicSkinRepository(fixture.Root, limits: new());
        var result = await repo.LoadSkinResultAsync("fixture");
        Assert.True(result.UsedFallback);
        Assert.Same(MusicSkin.Default, result.Skin);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.Equal(0, repo.AssetCache.LiveDecodedBytes);
        Assert.Equal(0, repo.AssetCache.CachedCount);
    }

    [Fact]
    public async Task SamePathUpdatedPackage_DoesNotReuseOldLivePixels()
    {
        using var fixture = new MusicSkinFixture();
        var repo = new MusicSkinRepository(fixture.Root, limits: new());
        var original = await repo.LoadSkinResultAsync("fixture");
        fixture.WriteImage("note.png", 8, 8);
        fixture.WriteManifest();
        var updated = await repo.LoadSkinResultAsync("fixture");
        Assert.Null(updated.Diagnostic);
        Assert.NotSame(original.Skin.NoteTapBitmap, updated.Skin.NoteTapBitmap);
        Assert.Equal(16, original.Skin.NoteTapBitmap!.PixelWidth);
        Assert.Equal(8, updated.Skin.NoteTapBitmap!.PixelWidth);
        Assert.Equal((16 * 16 + 8 * 8) * 8, repo.AssetCache.LiveDecodedBytes);
        GC.KeepAlive(original); GC.KeepAlive(updated);
    }

    [Fact]
    public void MetadataEntryLimit_IsBoundedEvenForTinyImages()
    {
        using var fixture = new MusicSkinFixture();
        var cache = new MusicSkinAssetCache(new() { MaxDecodedAssets = 1 });
        var active = cache.GetOrLoadBitmap(fixture.Package, "note.png", 1, 1);
        Assert.Throws<InvalidOperationException>(() => cache.GetOrLoadBitmap(fixture.Package, "note.png", 2, 2));
        Assert.Equal(1, cache.CachedCount);
        GC.KeepAlive(active);
    }

    [Fact]
    public async Task CanceledRepositoryRequest_DoesNotPublishFallbackOrConsumeBudget()
    {
        using var fixture = new MusicSkinFixture();
        var repo = new MusicSkinRepository(fixture.Root, limits: new());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.LoadSkinResultAsync("fixture", cancellationToken: cancellation.Token));
        Assert.Equal(0, repo.AssetCache.LiveDecodedBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<BitmapSource> LoadWithoutRetaining(MusicSkinAssetCache cache, MusicSkinFixture fixture) =>
        new(cache.GetOrLoadBitmap(fixture.Package, "note.png")!);
}
