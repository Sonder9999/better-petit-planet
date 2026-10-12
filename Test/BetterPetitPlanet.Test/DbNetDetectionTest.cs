using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.AutoPick.Ocr;
using OpenCvSharp;

namespace BetterPetitPlanet.Test;

public sealed class DbNetDetectionTest
{
    [Fact]
    public void Process_FiltersByBinaryAndBoxThresholds()
    {
        using var map = new Mat(80, 160, MatType.CV_32FC1, Scalar.All(0));
        Cv2.Rectangle(map, new Rect(10, 10, 20, 10), Scalar.All(0.3), -1);
        Cv2.Rectangle(map, new Rect(60, 10, 20, 10), Scalar.All(0.4), -1);
        Cv2.Rectangle(map, new Rect(110, 10, 20, 10), Scalar.All(0.9), -1);

        var result = new DbNetPostProcessor().Process(map, map.Size());

        var box = Assert.Single(result);
        Assert.True(box.Contains(new Point(120, 15)));
    }

    [Fact]
    public void Process_MapsExpandedBoxToOriginalImageCoordinates()
    {
        using var map = new Mat(64, 128, MatType.CV_32FC1, Scalar.All(0));
        Cv2.Rectangle(map, new Rect(40, 20, 33, 17), Scalar.All(0.9), -1);
        var config = new TextDetectionConfig { UnclipRatio = 1.5f };

        var result = new DbNetPostProcessor(config).Process(map, new Size(384, 128));

        // 32x16 contour: area/perimeter*1.5 = 8 map pixels on every side.
        Assert.Equal(new Rect(96, 24, 144, 64), Assert.Single(result));
    }

    [Fact]
    public void Process_ClampsUnclippedBoxToImageEdges()
    {
        using var map = new Mat(64, 96, MatType.CV_32FC1, Scalar.All(0.9));

        var result = new DbNetPostProcessor().Process(map, new Size(192, 128));

        Assert.Equal(new Rect(0, 0, 192, 128), Assert.Single(result));
    }

    [Fact]
    public void Process_BlankAndTinyRegionsReturnNoText()
    {
        using var map = new Mat(64, 96, MatType.CV_32FC1, Scalar.All(0));
        var processor = new DbNetPostProcessor();
        Assert.Empty(processor.Process(map, map.Size()));
        Cv2.Rectangle(map, new Rect(20, 20, 2, 2), Scalar.All(0.9), -1);
        Assert.Empty(processor.Process(map, map.Size()));
        using var empty = new Mat();
        Assert.Empty(processor.Process(empty, new Size(96, 64)));
    }

    [Theory]
    [InlineData(1920, 1080, 960, 544)]
    [InlineData(1080, 1920, 544, 960)]
    [InlineData(641, 359, 640, 352)]
    [InlineData(10, 10, 32, 32)]
    public void GetInputSize_RoundsToStrideAndCapsLongestSide(int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.Equal(new Size(expectedWidth, expectedHeight), DirectMlDetEngine.GetInputSize(new Size(width, height), 960));
    }

    [Fact]
    public void InvalidOptions_AreRejectedBeforeLoadingModel()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DbNetPostProcessor(new TextDetectionConfig { BinaryThreshold = float.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DbNetPostProcessor(new TextDetectionConfig { BoxThreshold = 1.1f }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DbNetPostProcessor(new TextDetectionConfig { UnclipRatio = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => DirectMlDetEngine.GetInputSize(new Size(200, 100), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DirectMlDetEngine.GetInputSize(new Size(0, 100), 960));
    }

    [Fact]
    public void Detect_MissingModelAndDisposedEngineReturnNoText()
    {
        using var engine = new DirectMlDetEngine(new TextDetectionConfig { ModelPath = "Assets/Models/Ocr/nonexistent-detector.onnx" });
        using var input = new Mat(64, 96, MatType.CV_8UC3, Scalar.All(0));
        Assert.False(engine.IsAvailable);
        Assert.Empty(engine.Detect(input));
        engine.Dispose();
        Assert.Empty(engine.Detect(input));
    }

    [Fact]
    public void Detect_BundledModelFindsTextAndHandlesBlankImagesAcrossSizes()
    {
        using var engine = new DirectMlDetEngine(new TextDetectionConfig { EnableCpuFallback = false });
        Assert.True(engine.IsAvailable, "The bundled PP-OCRv3 detector must load with DirectML.");
        using var blank = new Mat(64, 96, MatType.CV_8UC3, Scalar.All(0));
        Assert.Empty(engine.Detect(blank));
        using var text = new Mat(128, 384, MatType.CV_8UC4, Scalar.All(0));
        Cv2.PutText(text, "PLANET", new Point(20, 80), HersheyFonts.HersheySimplex, 2.5, Scalar.All(255), 4);
        var boxes = engine.Detect(text);
        Assert.NotEmpty(boxes);
        Assert.All(boxes, box => Assert.Equal(box, box & new Rect(0, 0, text.Width, text.Height)));
        using var gray = new Mat(96, 128, MatType.CV_8UC1, Scalar.All(255));
        Assert.Empty(engine.Detect(gray));
        engine.Dispose();
        Assert.False(engine.IsAvailable);
        Assert.Empty(engine.Detect(text));
    }

    [Fact]
    public async Task Detect_BrokenModelDisablesEngineWithoutThrowing()
    {
        string relative = Path.Combine("data", "tests", $"invalid-det-{Guid.NewGuid():N}.onnx");
        string model = Path.Combine(AppContext.BaseDirectory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        await File.WriteAllTextAsync(model, "invalid ONNX model");
        try
        {
            using var engine = new DirectMlDetEngine(new TextDetectionConfig { ModelPath = relative });
            using var blank = new Mat(32, 32, MatType.CV_8UC3, Scalar.All(0));
            Assert.False(engine.IsAvailable);
            Assert.Empty(engine.Detect(blank));
        }
        finally
        {
            File.Delete(model);
        }
    }
}
