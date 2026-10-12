using System;
using System.Collections.Generic;
using System.Linq;
using BetterPetitPlanet.Core.Config;
using OpenCvSharp;

namespace BetterPetitPlanet.GameTask.AutoPick.Ocr;

public sealed class DbNetPostProcessor
{
    private const int MinimumSide = 3;
    private const int MaximumCandidates = 1000;
    private readonly TextDetectionConfig _config;

    public DbNetPostProcessor(TextDetectionConfig? config = null)
    {
        _config = (config ?? new TextDetectionConfig()).ValidatedCopy();
    }

    public IReadOnlyList<Rect> Process(Mat probabilityMap, Size originalSize)
    {
        if (probabilityMap.Empty() || originalSize.Width <= 0 || originalSize.Height <= 0)
            return [];
        if (probabilityMap.Type() != MatType.CV_32FC1)
            throw new ArgumentException("DBNet requires a single-channel float probability map.", nameof(probabilityMap));

        using var binaryFloat = new Mat();
        using var bitmap = new Mat();
        Cv2.Threshold(probabilityMap, binaryFloat, _config.BinaryThreshold, 255, ThresholdTypes.Binary);
        binaryFloat.ConvertTo(bitmap, MatType.CV_8UC1);
        Cv2.FindContours(bitmap, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);

        var boxes = new List<Rect>();
        foreach (var contour in contours.Take(MaximumCandidates))
        {
            var rectangle = Cv2.MinAreaRect(contour);
            if (Math.Min(rectangle.Size.Width, rectangle.Size.Height) < MinimumSide)
                continue;
            double score = Score(probabilityMap, rectangle.Points());
            if (!double.IsFinite(score) || score < _config.BoxThreshold)
                continue;

            // DB unclip distance is polygon area * ratio / perimeter. For a rectangle,
            // its enclosing rotated rectangle grows by this distance on each side.
            double area = rectangle.Size.Width * rectangle.Size.Height;
            double perimeter = 2 * (rectangle.Size.Width + rectangle.Size.Height);
            float distance = (float)(area * _config.UnclipRatio / perimeter);
            var expanded = new RotatedRect(rectangle.Center,
                new Size2f(rectangle.Size.Width + 2 * distance, rectangle.Size.Height + 2 * distance), rectangle.Angle);
            if (Math.Min(expanded.Size.Width, expanded.Size.Height) < MinimumSide + 2)
                continue;
            var box = MapToOriginal(expanded.Points(), probabilityMap.Size(), originalSize);
            if (box.Width > MinimumSide && box.Height > MinimumSide)
                boxes.Add(box);
        }
        return boxes;
    }

    private static double Score(Mat map, Point2f[] points)
    {
        int left = Math.Clamp((int)Math.Floor(points.Min(p => p.X)), 0, map.Cols - 1);
        int top = Math.Clamp((int)Math.Floor(points.Min(p => p.Y)), 0, map.Rows - 1);
        int right = Math.Clamp((int)Math.Ceiling(points.Max(p => p.X)), 0, map.Cols - 1);
        int bottom = Math.Clamp((int)Math.Ceiling(points.Max(p => p.Y)), 0, map.Rows - 1);
        using var mask = new Mat(bottom - top + 1, right - left + 1, MatType.CV_8UC1, Scalar.All(0));
        var polygon = points.Select(p => new Point((int)(p.X - left), (int)(p.Y - top))).ToArray();
        Cv2.FillPoly(mask, new[] { polygon }, Scalar.All(1));
        using var region = new Mat(map, new Rect(left, top, mask.Cols, mask.Rows));
        return Cv2.Mean(region, mask).Val0;
    }

    private static Rect MapToOriginal(Point2f[] points, Size mapSize, Size originalSize)
    {
        double scaleX = (double)originalSize.Width / mapSize.Width;
        double scaleY = (double)originalSize.Height / mapSize.Height;
        int left = Math.Clamp((int)Math.Round(points.Min(p => p.X) * scaleX), 0, originalSize.Width);
        int top = Math.Clamp((int)Math.Round(points.Min(p => p.Y) * scaleY), 0, originalSize.Height);
        int right = Math.Clamp((int)Math.Round(points.Max(p => p.X) * scaleX), 0, originalSize.Width);
        int bottom = Math.Clamp((int)Math.Round(points.Max(p => p.Y) * scaleY), 0, originalSize.Height);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}
