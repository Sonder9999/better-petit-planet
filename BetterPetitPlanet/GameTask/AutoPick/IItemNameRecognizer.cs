using System;
using OpenCvSharp;

namespace BetterPetitPlanet.GameTask.AutoPick;

public interface IItemNameRecognizer : IDisposable
{
    string Recognize(Mat searchImage, ItemNameSearchArea area);
}

public readonly record struct ItemNameSearchArea(Rect Bounds, Point2d Anchor, Rect ExcludedPrompt);
