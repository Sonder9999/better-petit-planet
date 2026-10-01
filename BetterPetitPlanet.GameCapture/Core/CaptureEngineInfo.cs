namespace BetterPetitPlanet.GameCapture;

public sealed class CaptureEngineInfo
{
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsSupported { get; init; } = true;
}
