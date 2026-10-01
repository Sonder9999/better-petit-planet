using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterPetitPlanet.GameCapture;

public static class GameCaptureRegistry
{
    private static readonly Dictionary<string, (CaptureEngineInfo Info, Func<IGameCapture> Factory)> _engines = new(StringComparer.OrdinalIgnoreCase);

    static GameCaptureRegistry()
    {
        // 注册 WindowsGraphicsCapture 引擎（Win10/Win11 推荐硬件级捕获）
        Register(
            new CaptureEngineInfo
            {
                Name = "WindowsGraphicsCapture",
                DisplayName = "WindowsGraphicsCapture",
                Description = "Windows 10/11 硬件加速捕获（流畅、高性能、低延迟）",
                IsSupported = true
            },
            () => new Graphics.GraphicsCapture()
        );

        // 注册 PrintWindow 引擎（Win32 专用兼容引擎，原 BitBlt 架构重构升级）
        Register(
            new CaptureEngineInfo
            {
                Name = "PrintWindow",
                DisplayName = "PrintWindow",
                Description = "Win32 PrintWindow 兼容模式（稳定抗遮挡，DirectX 硬件窗口适配）",
                IsSupported = true
            },
            () => new Engines.PrintWindow.PrintWindowCapture()
        );
    }

    public static void Register(CaptureEngineInfo info, Func<IGameCapture> factory)
    {
        _engines[info.Name] = (info, factory);
    }

    public static IReadOnlyList<string> AvailableEngineNames => _engines.Keys.ToList();

    public static IReadOnlyList<CaptureEngineInfo> AvailableEngines => _engines.Values.Select(v => v.Info).ToList();

    public static IGameCapture Create(string? engineName)
    {
        if (string.IsNullOrWhiteSpace(engineName))
        {
            return _engines["WindowsGraphicsCapture"].Factory();
        }

        // 历史命名向后兼容映射
        if (engineName.Equals("BitBlt", StringComparison.OrdinalIgnoreCase))
        {
            engineName = "PrintWindow";
        }

        if (_engines.TryGetValue(engineName, out var entry))
        {
            return entry.Factory();
        }

        // 默认回退
        return _engines["WindowsGraphicsCapture"].Factory();
    }
}
