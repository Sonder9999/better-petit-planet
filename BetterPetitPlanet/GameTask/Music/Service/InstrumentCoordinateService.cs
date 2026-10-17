using System;
using System.Collections.Generic;
using BetterPetitPlanet.GameTask.Music.Instrument;

namespace BetterPetitPlanet.GameTask.Music.Service;

/// <summary>
/// 乐器演奏界面音符/按键坐标计算服务。
/// 支持竖笛、吉他等多种乐器的基准分辨率 (2560x1440) 与动态窗口缩放。
/// </summary>
public sealed class InstrumentCoordinateService
{
    public const double BaseWidth = 2560.0;
    public const double BaseHeight = 1440.0;

    private readonly IInstrumentRegistry _registry;

    // 默认竖笛 7 音符基准中心坐标备份 (2560x1440)
    private static readonly Dictionary<string, (double X, double Y)> FallbackRecorderBaseCoordinates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1"] = (598.0, 1230.0),
        ["A"] = (598.0, 1230.0),
        ["DO"] = (598.0, 1230.0),

        ["2"] = (826.0, 1230.0),
        ["S"] = (826.0, 1230.0),
        ["RE"] = (826.0, 1230.0),

        ["3"] = (1052.0, 1230.0),
        ["D"] = (1052.0, 1230.0),
        ["MI"] = (1052.0, 1230.0),

        ["4"] = (1280.0, 1230.0),
        ["F"] = (1280.0, 1230.0),
        ["FA"] = (1280.0, 1230.0),

        ["5"] = (1508.0, 1230.0),
        ["J"] = (1508.0, 1230.0),
        ["SO"] = (1508.0, 1230.0),

        ["6"] = (1734.0, 1230.0),
        ["K"] = (1734.0, 1230.0),
        ["LA"] = (1734.0, 1230.0),

        ["7"] = (1960.0, 1230.0),
        ["L"] = (1960.0, 1230.0),
        ["TI"] = (1960.0, 1230.0),
    };

    public InstrumentCoordinateService(IInstrumentRegistry? registry = null)
    {
        _registry = registry ?? InstrumentRegistry.Default;
    }

    /// <summary>
    /// 根据当前游戏窗口客户区尺寸，计算竖笛默认按键/音符对应的客户区像素坐标
    /// </summary>
    public bool TryGetNoteCoordinate(string keyOrNote, int clientWidth, int clientHeight, out int clientX, out int clientY)
    {
        return TryGetNoteCoordinate("Recorder", keyOrNote, clientWidth, clientHeight, out clientX, out clientY);
    }

    /// <summary>
    /// 根据乐器类型与当前游戏窗口客户区尺寸，计算目标按键/和弦对应的客户区像素坐标
    /// </summary>
    public bool TryGetNoteCoordinate(string instrumentId, string keyOrNote, int clientWidth, int clientHeight, out int clientX, out int clientY)
    {
        clientX = 0;
        clientY = 0;

        if (string.IsNullOrWhiteSpace(keyOrNote) || clientWidth <= 0 || clientHeight <= 0)
        {
            return false;
        }

        if (_registry.TryGetScaledCoordinate(instrumentId, keyOrNote, clientWidth, clientHeight, out clientX, out clientY))
        {
            return true;
        }

        // 历史兼容回退逻辑
        var key = keyOrNote.Trim().ToUpperInvariant();
        if (FallbackRecorderBaseCoordinates.TryGetValue(key, out var basePoint))
        {
            double scaleX = clientWidth / BaseWidth;
            double scaleY = clientHeight / BaseHeight;

            clientX = (int)Math.Round(basePoint.X * scaleX);
            clientY = (int)Math.Round(basePoint.Y * scaleY);
            return true;
        }

        return false;
    }
}
