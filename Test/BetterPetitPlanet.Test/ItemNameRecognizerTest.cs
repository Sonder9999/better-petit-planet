using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.AutoPick;
using BetterPetitPlanet.GameTask.AutoPick.Ocr;
using OpenCvSharp;

namespace BetterPetitPlanet.Test;

public sealed class ItemNameRecognizerTest
{
    [Fact]
    public void SingleCharacterTexture_IsNotAnItemName()
    {
        using var image = new Mat(100, 200, MatType.CV_8UC3);
        using var recognizer = new ItemNameRecognizer(new TestDetector([new Rect(10, 10, 20, 15)]),
            new TestOcr { Text = "B" });
        Assert.Empty(recognizer.Recognize(image, default));
    }

    [Fact]
    public void NearestBox_ExcludesPickupPrompt_AndCropsBeforeOcr()
    {
        using var image = new Mat(100, 200, MatType.CV_8UC3, Scalar.All(0));
        var nearest = new Rect(85, 40, 30, 20);
        using (var text = new Mat(image, nearest)) text.SetTo(Scalar.All(42));
        var ocr = new TestOcr();
        using var recognizer = new ItemNameRecognizer(new TestDetector([
            new Rect(1, 1, 20, 10), new Rect(110, 45, 20, 10), nearest]), ocr);
        var name = recognizer.Recognize(image, new ItemNameSearchArea(
            new Rect(0, 0, 200, 100), new Point2d(110, 50), new Rect(110, 40, 90, 20)));
        Assert.Equal("红向日葵", name);
        Assert.Equal(nearest.Size, ocr.InputSize);
        Assert.Equal(42, ocr.Pixel);
    }

    [Fact]
    public void EmptyOrOutOfBoundsBoxes_DoNotInvokeOcr()
    {
        var ocr = new TestOcr();
        using var recognizer = new ItemNameRecognizer(new TestDetector([
            new Rect(300, 300, 10, 10), default]), ocr);
        using var image = new Mat(100, 200, MatType.CV_8UC3);
        Assert.Empty(recognizer.Recognize(image, default));
        Assert.Equal(default, ocr.InputSize);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    public void SearchArea_UsesFrameCoordinates(int width, int height)
    {
        var config = new ItemNameRecognitionConfig();
        var area = ItemNameSearch.Calculate(new Size(width, height), config);
        Assert.Equal((int)Math.Round(width * .2), area.Bounds.X);
        Assert.Equal(width * .5 - area.Bounds.X, area.Anchor.X, 3);
        Assert.Equal(height * .55 - area.Bounds.Y, area.Anchor.Y, 3);
        Assert.True(area.Bounds.Right <= width);
        var prompt = PetitRoiCalculator.Calculate(width, height);
        Assert.Equal(prompt.X - area.Bounds.X, area.ExcludedPrompt.X);
    }

    [Fact]
    public void InvalidSearchRatios_ReturnEmptyArea()
    {
        Assert.Equal(default, ItemNameSearch.Calculate(new Size(100, 100),
            new ItemNameRecognitionConfig { XMinRatio = double.NaN }));
        Assert.Equal(default, ItemNameSearch.Calculate(new Size(100, 100),
            new ItemNameRecognitionConfig { YMinRatio = .9, YMaxRatio = .1 }));
    }

    private sealed class TestDetector(IReadOnlyList<Rect> boxes) : ITextDetectionEngine
    {
        public IReadOnlyList<Rect> Detect(Mat image) => boxes;
        public void Dispose() { }
    }

    private sealed class TestOcr : IOcrEngine
    {
        public string Name => "test";
        public bool IsAvailable => true;
        public string Text { get; init; } = " 红向日葵 ";
        public Size InputSize { get; private set; }
        public byte Pixel { get; private set; }
        public string Recognize(Mat roiMat)
        {
            InputSize = roiMat.Size();
            Pixel = roiMat.At<Vec3b>(0, 0).Item0;
            return Text;
        }
    }
}
