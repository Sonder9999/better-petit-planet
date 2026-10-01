using System;
using System.IO;
using BetterPetitPlanet.GameTask.AutoPick;
using BetterPetitPlanet.GameTask.AutoPick.Ocr;
using OpenCvSharp;
using Xunit;

namespace BetterPetitPlanet.Test;

public class DirectMlOcrTest
{
    [Fact]
    public void Recognize_PickPngSample_DetectsPickText()
    {
        string samplePath = @"D:\Coding\Game\petit_planet\petit_auto_pick\pick.png";
        if (!File.Exists(samplePath))
        {
            return;
        }

        using var engine = new DirectMlOcrEngine();
        if (!engine.IsAvailable)
        {
            return;
        }

        using var fullImage = Cv2.ImRead(samplePath);
        Assert.False(fullImage.Empty());

        var roi = PetitRoiCalculator.Calculate(fullImage.Cols, fullImage.Rows);
        using var roiMat = new Mat(fullImage, roi);

        var recognized = engine.Recognize(roiMat);
        Assert.Contains("拾取", recognized);
    }

    [Fact]
    public void Recognize_UserScreenshotSample_DetectsPickText()
    {
        string userScreenshot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".gemini", "antigravity", "brain", "1af34c3c-2614-4a98-9ed2-3f8faa3a6aaf",
            ".user_uploaded", "media_1790769675663.jpg");

        if (!File.Exists(userScreenshot))
        {
            return;
        }

        using var engine = new DirectMlOcrEngine();
        if (!engine.IsAvailable)
        {
            return;
        }

        using var fullImage = Cv2.ImRead(userScreenshot);
        Assert.False(fullImage.Empty());

        var roi = PetitRoiCalculator.Calculate(fullImage.Cols, fullImage.Rows);
        using var roiMat = new Mat(fullImage, roi);

        var recognized = engine.Recognize(roiMat);
        Assert.Contains("拾取", recognized);
    }
}
