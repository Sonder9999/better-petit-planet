using System;

namespace BetterPetitPlanet.GameCapture;

public static class GameCaptureFactory
{
    public static string[] ModeNames() => GameCaptureRegistry.AvailableEngineNames.ToArray();

    public static IGameCapture Create(CaptureModes mode)
    {
        return mode switch
        {
            CaptureModes.PrintWindow => GameCaptureRegistry.Create("PrintWindow"),
            CaptureModes.WindowsGraphicsCapture => GameCaptureRegistry.Create("WindowsGraphicsCapture"),
            CaptureModes.WindowsGraphicsCaptureHdr => new Graphics.GraphicsCapture(true),
            _ => GameCaptureRegistry.Create(mode.ToString()),
        };
    }

    public static IGameCapture Create(string engineName) => GameCaptureRegistry.Create(engineName);
}
