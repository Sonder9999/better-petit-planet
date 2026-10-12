using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace BetterPetitPlanet.GameTask.AutoPick.Ocr;

public interface ITextDetectionEngine : IDisposable
{
    /// <summary>Returns text rectangles in the supplied image's coordinates.</summary>
    IReadOnlyList<Rect> Detect(Mat image);
}
