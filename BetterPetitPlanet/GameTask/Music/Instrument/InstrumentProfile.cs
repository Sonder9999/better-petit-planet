using System;
using System.Collections.Generic;

namespace BetterPetitPlanet.GameTask.Music.Instrument;

/// <summary>
/// 乐器识别与弹奏配置档案
/// </summary>
public sealed class InstrumentProfile
{
    private readonly IReadOnlyList<AnchorTarget>? _allTargets;

    /// <summary>
    /// 唯一标识，如 "Recorder", "Guitar"
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// 显示名称，如 "竖笛", "吉他"
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// 别名列表，用于在解析曲谱或识别时匹配（如 "guitar", "吉他"）
    /// </summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>
    /// 基准设计分辨率，默认 (2560.0, 1440.0)
    /// </summary>
    public (double Width, double Height) BaseResolution { get; init; } = (2560.0, 1440.0);

    /// <summary>
    /// 大底模板相对路径，例如 "Assets/Instruments/Guitar/template.png"
    /// </summary>
    public string TemplateRelativePath { get; init; } = string.Empty;

    /// <summary>
    /// 专属模板小图目录相对路径，例如 "Assets/Instruments/Guitar/targets"
    /// </summary>
    public string TargetsDirectoryRelativePath { get; init; } = string.Empty;

    /// <summary>
    /// 公共按键锚点（如 key_A ~ key_L）
    /// </summary>
    public IReadOnlyList<AnchorTarget> KeyAnchors { get; init; } = [];

    /// <summary>
    /// 乐器专属目标（竖笛为数字 1-7 与唱名 do-ti；吉他为和弦 C, Dm, Em, F, G, Am, G7）
    /// </summary>
    public IReadOnlyList<AnchorTarget> ExclusiveTargets { get; init; } = [];

    /// <summary>
    /// Stage 2 用于快速特征比对的验证锚点（缺省时自动使用 ExclusiveTargets 的首尾外缘目标）
    /// </summary>
    public IReadOnlyList<AnchorTarget> VerificationAnchors { get; init; } = [];

    /// <summary>
    /// 全部锚点目标（未显式指定时自动合并 ExclusiveTargets 与 KeyAnchors）
    /// </summary>
    public IReadOnlyList<AnchorTarget> AllTargets
    {
        get
        {
            if (_allTargets != null && _allTargets.Count > 0)
            {
                return _allTargets;
            }

            var combined = new List<AnchorTarget>(ExclusiveTargets);
            combined.AddRange(KeyAnchors);
            return combined;
        }
        init => _allTargets = value;
    }

    /// <summary>
    /// 7 键演奏点击基准中心坐标映射字典（key/chord -> (baseX, baseY)）
    /// </summary>
    public IReadOnlyDictionary<string, (double X, double Y)> PlayCoordinates { get; init; } =
        new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 独立按键大方块包围盒定义（用于在测试/标定界面标出 7 个按键范围）
    /// </summary>
    public IReadOnlyList<(int X, int Y, int W, int H, string Label)> ButtonBounds { get; init; } = [];
}
