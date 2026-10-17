namespace BetterPetitPlanet.GameTask.Music.Instrument;

public enum AnchorCategory
{
    Key,
    Chord,
    Number,
    Syllable
}

/// <summary>
/// 乐器识别锚点定义，包含在 2560x1440 原生分辨率下的坐标包围盒、OCR 期望字符与模板图片文件名。
/// </summary>
public record AnchorTarget(
    string Name,
    string Label,
    int X,
    int Y,
    int Width,
    int Height,
    string ExpectedText,
    string TemplateFileName,
    bool IsExclusive = false,
    AnchorCategory Category = AnchorCategory.Key
)
{
    public (int X, int Y, int Width, int Height) Bounds => (X, Y, Width, Height);

    public double CenterX => X + Width / 2.0;
    public double CenterY => Y + Height / 2.0;

    public BetterPetitPlanet.GameTask.Music.Service.InstrumentTarget ToInstrumentTarget() =>
        new(Name, ExpectedText, X, Y, Width, Height);
}
