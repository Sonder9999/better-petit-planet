using System;
using BetterPetitPlanet.GameTask.AutoPick.Ocr;
using OpenCvSharp;

namespace BetterPetitPlanet.GameTask.AutoPick;

public sealed class ItemNameRecognizer : IItemNameRecognizer
{
    private readonly ITextDetectionEngine _detector;
    private readonly IOcrEngine _ocrEngine;
    private readonly int _minimumTextLength;

    public ItemNameRecognizer(ITextDetectionEngine detector, IOcrEngine ocrEngine, int minimumTextLength = 2)
    {
        if (minimumTextLength < 1 || minimumTextLength > 64)
            throw new ArgumentOutOfRangeException(nameof(minimumTextLength));
        _detector = detector;
        _ocrEngine = ocrEngine;
        _minimumTextLength = minimumTextLength;
    }

    public string Recognize(Mat searchImage, ItemNameSearchArea area)
    {
        if (searchImage.Empty()) return string.Empty;
        var imageBounds = new Rect(0, 0, searchImage.Width, searchImage.Height);
        Rect? closest = null;
        double closestDistance = double.MaxValue;
        foreach (var candidate in _detector.Detect(searchImage))
        {
            var box = candidate.Intersect(imageBounds);
            if (box.Width <= 0 || box.Height <= 0) continue;
            double centerX = box.X + box.Width / 2.0;
            double centerY = box.Y + box.Height / 2.0;
            var prompt = area.ExcludedPrompt;
            if (centerX >= prompt.X && centerX < prompt.Right &&
                centerY >= prompt.Y && centerY < prompt.Bottom) continue;
            double dx = centerX - area.Anchor.X;
            double dy = centerY - area.Anchor.Y;
            double distance = dx * dx + dy * dy;
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closest = box;
            }
        }

        if (closest == null) return string.Empty;
        using var crop = new Mat(searchImage, closest.Value);
        var name = _ocrEngine.Recognize(crop).Trim();
        return name.Length < _minimumTextLength || name.Contains("拾取", StringComparison.Ordinal)
            ? string.Empty : name;
    }

    public void Dispose()
    {
        try { _detector.Dispose(); }
        finally { (_ocrEngine as IDisposable)?.Dispose(); }
    }
}
