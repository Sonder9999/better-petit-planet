using System.Collections.Generic;

namespace BetterPetitPlanet.GameTask.Music.Instrument;

/// <summary>
/// 乐器识别与配置注册表服务接口
/// </summary>
public interface IInstrumentRegistry
{
    /// <summary>
    /// 获取指定 ID 或别名的乐器档案，若不存在抛出 KeyNotFoundException
    /// </summary>
    InstrumentProfile GetProfile(string id);

    /// <summary>
    /// 尝试获取指定 ID 或别名的乐器档案
    /// </summary>
    bool TryGetProfile(string id, out InstrumentProfile? profile);

    /// <summary>
    /// 获取所有已注册的乐器档案
    /// </summary>
    IReadOnlyCollection<InstrumentProfile> GetAllProfiles();

    /// <summary>
    /// 注册乐器档案
    /// </summary>
    bool RegisterProfile(InstrumentProfile profile);

    /// <summary>
    /// 校验指定乐器的模板与标注资源有效性
    /// </summary>
    InstrumentValidationResult ValidateProfile(string id, string? baseDirectory = null);

    /// <summary>
    /// 校验所有已注册乐器的模板与标注资源有效性
    /// </summary>
    IReadOnlyList<InstrumentValidationResult> ValidateAllProfiles(string? baseDirectory = null);

    /// <summary>
    /// 查询指定乐器对应按键/和弦的标准基准坐标 (2560x1440)
    /// </summary>
    bool TryGetBaseCoordinate(string instrumentId, string keyOrChord, out double baseX, out double baseY);

    /// <summary>
    /// 根据游戏窗口客户区尺寸，计算指定乐器对应按键/和弦缩放后的像素坐标
    /// </summary>
    bool TryGetScaledCoordinate(string instrumentId, string keyOrChord, int clientWidth, int clientHeight, out int clientX, out int clientY);

    /// <summary>
    /// 解析乐器大底模板文件物理路径
    /// </summary>
    string? ResolveTemplatePath(string instrumentId, string? baseDirectory = null);

    /// <summary>
    /// 解析乐器特定目标小图的物理路径（自动在乐器专属目录及 common 目录中查找）
    /// </summary>
    string? ResolveTargetFilePath(string instrumentId, string targetFileName, string? baseDirectory = null);
}
