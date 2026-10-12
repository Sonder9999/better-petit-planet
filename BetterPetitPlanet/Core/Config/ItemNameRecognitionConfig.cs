namespace BetterPetitPlanet.Core.Config;

public sealed class ItemNameRecognitionConfig
{
    public bool Enabled { get; set; }
    public int MinimumTextLength { get; set; } = 2;
    private TextDetectionConfig _detection = new();
    public TextDetectionConfig Detection
    {
        get => _detection;
        set => _detection = value ?? new();
    }
    public double XMinRatio { get; set; } = 0.20;
    public double XMaxRatio { get; set; } = 0.75;
    public double YMinRatio { get; set; } = 0.25;
    public double YMaxRatio { get; set; } = 0.75;
    public double AnchorXRatio { get; set; } = 0.50;
    public double AnchorYRatio { get; set; } = 0.55;
}
