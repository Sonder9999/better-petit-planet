using System;
using OpenCvSharp;

namespace BetterPetitPlanet.GameTask.AutoPick;

public static class PetitRoiCalculator
{
    // Normalized ROI ratios matching petit_auto_pick with multi-resolution tolerance
    public const double XMinRatio = 0.660;
    public const double YMinRatio = 0.570;
    public const double XMaxRatio = 0.835;
    public const double YMaxRatio = 0.660;

    public static Rect Calculate(int clientWidth, int clientHeight)
    {
        if (clientWidth <= 0 || clientHeight <= 0)
        {
            return new Rect(0, 0, 0, 0);
        }

        int x = (int)Math.Round(clientWidth * XMinRatio);
        int y = (int)Math.Round(clientHeight * YMinRatio);
        int xMax = (int)Math.Round(clientWidth * XMaxRatio);
        int yMax = (int)Math.Round(clientHeight * YMaxRatio);

        // Boundary clamping
        x = Math.Clamp(x, 0, clientWidth - 1);
        y = Math.Clamp(y, 0, clientHeight - 1);
        int width = Math.Clamp(xMax - x, 1, clientWidth - x);
        int height = Math.Clamp(yMax - y, 1, clientHeight - y);

        return new Rect(x, y, width, height);
    }
}
