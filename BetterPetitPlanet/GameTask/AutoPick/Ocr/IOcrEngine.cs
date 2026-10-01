using OpenCvSharp;

namespace BetterPetitPlanet.GameTask.AutoPick.Ocr;

public interface IOcrEngine
{
    string Name { get; }
    bool IsAvailable { get; }
    string Recognize(Mat roiMat);
}
