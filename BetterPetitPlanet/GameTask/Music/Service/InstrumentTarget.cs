namespace BetterPetitPlanet.GameTask.Music.Service;

/// <summary>
/// 竖笛按键单项标注目标（对应 music_recorder.xml 标注数据）
/// </summary>
public record InstrumentTarget(
    string Name,
    string ExpectedText,
    int X,
    int Y,
    int Width,
    int Height
);

/// <summary>
/// 竖笛 2560x1440 原生标注坐标定义集合
/// </summary>
public static class InstrumentTargets
{
    public const int BaseWidth = 2560;
    public const int BaseHeight = 1440;

    // 7 个按键数字目标 (用于 OCR 识别与独立图像对比)
    public static readonly InstrumentTarget[] Numbers =
    [
        new("1", "1", 571, 1147, 54, 58),
        new("2", "2", 799, 1147, 54, 58),
        new("3", "3", 1025, 1147, 54, 58),
        new("4", "4", 1253, 1147, 54, 58),
        new("5", "5", 1481, 1147, 54, 58),
        new("6", "6", 1707, 1147, 54, 58),
        new("7", "7", 1933, 1147, 54, 58),
    ];

    // 7 个唱名目标
    public static readonly InstrumentTarget[] Syllables =
    [
        new("do", "do", 572, 1214, 52, 32),
        new("re", "re", 800, 1214, 52, 32),
        new("mi", "mi", 1026, 1214, 52, 32),
        new("fa", "fa", 1254, 1214, 52, 32),
        new("so", "so", 1482, 1214, 52, 32),
        new("la", "la", 1708, 1214, 52, 32),
        new("ti", "ti", 1934, 1214, 52, 32),
    ];

    // 7 个按键快捷键字母目标
    public static readonly InstrumentTarget[] Keys =
    [
        new("key_A", "A", 574, 1272, 48, 38),
        new("key_S", "S", 802, 1272, 48, 38),
        new("key_D", "D", 1028, 1272, 48, 38),
        new("key_F", "F", 1256, 1272, 48, 38),
        new("key_J", "J", 1484, 1272, 48, 38),
        new("key_K", "K", 1710, 1272, 48, 38),
        new("key_L", "L", 1936, 1272, 48, 38),
    ];

    // 7 个独立音符按键小方块包围盒定义 (用于在测试界面分别标出 7 个独立按键)
    public static readonly (int X, int Y, int W, int H, string Label)[] ButtonBounds =
    [
        (560, 1140, 75, 178, "1 do A"),
        (788, 1140, 75, 178, "2 re S"),
        (1014, 1140, 75, 178, "3 mi D"),
        (1242, 1140, 75, 178, "4 fa F"),
        (1470, 1140, 75, 178, "5 so J"),
        (1696, 1140, 75, 178, "6 la K"),
        (1922, 1140, 75, 178, "7 ti L"),
    ];
}

/// <summary>
/// 竖笛识别综合判定结果
/// </summary>
public class InstrumentDetectionResult
{
    public bool IsDetected { get; set; }
    public double OcrConfidence { get; set; }
    public int OcrHits { get; set; }
    public int OcrTotal { get; set; }
    public double TemplateConfidence { get; set; }
    public int TemplateHits { get; set; }
    public int TemplateTotal { get; set; }
    public double CombinedConfidence { get; set; }
    public string Details { get; set; } = string.Empty;
    public string StatusSummary { get; set; } = string.Empty;
}
