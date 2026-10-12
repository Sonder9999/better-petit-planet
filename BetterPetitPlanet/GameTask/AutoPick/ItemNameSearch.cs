using System;
using BetterPetitPlanet.Core.Config;
using OpenCvSharp;

namespace BetterPetitPlanet.GameTask.AutoPick;

public static class ItemNameSearch
{
    public static ItemNameSearchArea Calculate(Size frameSize, ItemNameRecognitionConfig config)
    {
        if (frameSize.Width <= 0 || frameSize.Height <= 0 ||
            !Ratio(config.XMinRatio) || !Ratio(config.XMaxRatio) ||
            !Ratio(config.YMinRatio) || !Ratio(config.YMaxRatio) ||
            !Ratio(config.AnchorXRatio) || !Ratio(config.AnchorYRatio) ||
            config.XMinRatio >= config.XMaxRatio || config.YMinRatio >= config.YMaxRatio)
            return default;

        int x = (int)Math.Round(frameSize.Width * config.XMinRatio);
        int y = (int)Math.Round(frameSize.Height * config.YMinRatio);
        int right = (int)Math.Round(frameSize.Width * config.XMaxRatio);
        int bottom = (int)Math.Round(frameSize.Height * config.YMaxRatio);
        var bounds = new Rect(x, y, right - x, bottom - y);
        var anchor = new Point2d(frameSize.Width * config.AnchorXRatio - x,
            frameSize.Height * config.AnchorYRatio - y);
        var prompt = PetitRoiCalculator.Calculate(frameSize.Width, frameSize.Height);
        prompt.X -= x;
        prompt.Y -= y;
        return new ItemNameSearchArea(bounds, anchor, prompt);
    }

    private static bool Ratio(double value) => double.IsFinite(value) && value >= 0 && value <= 1;
}
