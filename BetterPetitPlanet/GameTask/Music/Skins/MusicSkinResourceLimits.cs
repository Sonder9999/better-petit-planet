using System;

namespace BetterPetitPlanet.GameTask.Music.Skins;

/// <summary>Installed directories and future importers share these resource admission limits.</summary>
public sealed record MusicSkinResourceLimits
{
    public int MaxFiles { get; init; } = 512;
    public long MaxPackageBytes { get; init; } = 256L * 1024 * 1024;
    public long MaxImageBytes { get; init; } = 32L * 1024 * 1024;
    public int MaxManifestBytes { get; init; } = 1024 * 1024;
    public int MaxImageDimension { get; init; } = 4096;
    public long MaxTotalImagePixels { get; init; } = 64L * 1024 * 1024;
    public int MaxDecodedAssets { get; init; } = 512;
    public long MaxDecodedBytes { get; init; } = 128L * 1024 * 1024;

    internal void Validate()
    {
        if (MaxFiles <= 0 || MaxPackageBytes <= 0 || MaxImageBytes <= 0 || MaxManifestBytes <= 0 ||
            MaxDecodedAssets <= 0 || MaxImageDimension <= 0 || MaxTotalImagePixels <= 0 || MaxDecodedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MusicSkinResourceLimits), "皮肤资源上限必须为正数");
    }
}
