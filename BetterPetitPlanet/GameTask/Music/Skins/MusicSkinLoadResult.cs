namespace BetterPetitPlanet.GameTask.Music.Skins;

public sealed record MusicSkinLoadResult(string RequestedSkinId, MusicSkin Skin, string? Diagnostic = null)
{
    public bool UsedFallback => Diagnostic != null;
}

/// <summary>Maximum output dimensions in physical pixels; decoder preserves aspect ratio and never upscales.</summary>
public readonly record struct MusicSkinDecodeSize(int Width, int Height)
{
    public static MusicSkinDecodeSize Preview { get; } = new(960, 540);
    public static MusicSkinDecodeSize Overlay { get; } = new(1920, 1080);
}
