using System;
using System.ComponentModel;

namespace BetterPetitPlanet.GameCapture;

public enum CaptureModes
{
    [Description("PrintWindow")]
    PrintWindow = 0,

    [Obsolete("Use PrintWindow instead.")]
    [Description("BitBlt (已更名为 PrintWindow)")]
    BitBlt = 0,

    [Description("WindowsGraphicsCapture")]
    WindowsGraphicsCapture = 1,

    [Description("WindowsGraphicsCapture (HDR)")]
    WindowsGraphicsCaptureHdr = 2,
}
