namespace BetterPetitPlanet.GameCapture;

public static class CaptureModeExtensions
{
    public static CaptureModes ToCaptureMode(this string? modeName)
    {
        if (string.IsNullOrWhiteSpace(modeName))
        {
            return CaptureModes.WindowsGraphicsCapture;
        }

        var clean = modeName.Replace("*", "").Trim();
        if (clean.Equals("PrintWindow", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("BitBlt", StringComparison.OrdinalIgnoreCase))
        {
            return CaptureModes.PrintWindow;
        }
        if (clean.StartsWith("WindowsGraphicsCapture", StringComparison.OrdinalIgnoreCase))
        {
            return CaptureModes.WindowsGraphicsCapture;
        }
        if (clean.StartsWith("DirectX", StringComparison.OrdinalIgnoreCase))
        {
            return CaptureModes.WindowsGraphicsCapture;
        }

        if (Enum.TryParse<CaptureModes>(clean, true, out var mode))
        {
            return mode;
        }

        return CaptureModes.WindowsGraphicsCapture;
    }
}