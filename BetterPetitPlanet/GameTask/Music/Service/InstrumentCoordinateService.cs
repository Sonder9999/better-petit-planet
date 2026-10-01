using System;
using System.Collections.Generic;

namespace BetterPetitPlanet.GameTask.Music.Service;

/// <summary>
/// 乐器演奏界面音符坐标计算服务。
/// 基准分辨率 2560x1440，基于 music_recorder.xml 标注数据。
/// </summary>
public sealed class InstrumentCoordinateService
{
    public const double BaseWidth = 2560.0;
    public const double BaseHeight = 1440.0;

    // 竖笛 7 音符基准中心坐标 (2560x1440)
    private static readonly Dictionary<string, (double X, double Y)> RecorderBaseCoordinates = new(StringComparer.OrdinalIgnoreCase)
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

    /// <summary>
    /// 根据当前游戏窗口客户区尺寸，计算目标按键/音符对应的客户区像素坐标
    /// </summary>
    public bool TryGetNoteCoordinate(string keyOrNote, int clientWidth, int clientHeight, out int clientX, out int clientY)
    {
        clientX = 0;
        clientY = 0;

        if (string.IsNullOrWhiteSpace(keyOrNote) || clientWidth <= 0 || clientHeight <= 0)
        {
            return false;
        }

        var key = keyOrNote.Trim().ToUpperInvariant();
        if (!RecorderBaseCoordinates.TryGetValue(key, out var basePoint))
        {
            return false;
        }

        double scaleX = clientWidth / BaseWidth;
        double scaleY = clientHeight / BaseHeight;

        clientX = (int)Math.Round(basePoint.X * scaleX);
        clientY = (int)Math.Round(basePoint.Y * scaleY);
        return true;
    }
}
