using System.Diagnostics;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.AutoPick;
using BetterPetitPlanet.GameTask.AutoPick.Ocr;
using Newtonsoft.Json;
using OpenCvSharp;
using Xunit.Abstractions;

namespace BetterPetitPlanet.Test;

public sealed class ItemNameScreenshotTest(ITestOutputHelper output)
{
    [ScreenshotFact]
    public async Task Recognize_UserScreenshots_WithBundledModels()
    {
        var root = FindRepositoryRoot();
        var relativeSamples = Environment.GetEnvironmentVariable("PETIT_ITEM_NAME_SAMPLES")!;
        Assert.False(Path.IsPathFullyQualified(relativeSamples), "Use a repository-relative sample directory.");
        var samples = Path.GetFullPath(Path.Combine(root, relativeSamples));
        Assert.True(Directory.Exists(samples), $"Sample directory '{samples}' does not exist.");
        var files = Directory.GetFiles(samples, "*.png").Order().ToArray();
        Assert.NotEmpty(files);

        var sampleDirAnnotation = Path.Combine(samples, "item-name-samples.json");
        var fixtureAnnotation = Path.Combine(root, "Test", "BetterPetitPlanet.Test", "Fixtures", "item-name-samples.json");
        string expectedPath = File.Exists(sampleDirAnnotation) ? sampleDirAnnotation : fixtureAnnotation;
        Assert.True(File.Exists(expectedPath),
            $"Annotation file 'item-name-samples.json' not found. Please place it in sample folder '{samples}' or in '{fixtureAnnotation}'. Refer to 'item-name-samples.example.json' for the format.");

        var expected = JsonConvert.DeserializeObject<Dictionary<string, string>>(await File.ReadAllTextAsync(expectedPath))!;
        Assert.Equal(expected.Keys.Order(), files.Select(Path.GetFileName).Order());
        var outputDirectory = Path.Combine(root, "Test", "BetterPetitPlanet.Test", "Validation", "item-names");
        Directory.CreateDirectory(outputDirectory);
        var config = new ItemNameRecognitionConfig();
        using var detector = new DirectMlDetEngine(config.Detection);
        Assert.True(detector.IsAvailable, "Bundled DBNet must load; this test must not silently pass without models.");
        using var ocr = new DirectMlOcrEngine();
        Assert.True(ocr.IsAvailable);
        var recordingDetector = new RecordingDetector(detector);
        var recordingOcr = new RecordingOcr(ocr);
        using var recognizer = new ItemNameRecognizer(recordingDetector, recordingOcr, config.MinimumTextLength);
        var results = new List<object>();
        var mismatches = new List<string>();
        for (int i = 0; i < files.Length; i++)
        {
            var fileName = Path.GetFileName(files[i]);
            var sampleName = Path.GetFileNameWithoutExtension(files[i]);
            using var frame = Cv2.ImRead(files[i]);
            Assert.False(frame.Empty());
            var area = ItemNameSearch.Calculate(frame.Size(), config);
            using var search = new Mat(frame, area.Bounds);
            recordingOcr.CropPath = Path.Combine(outputDirectory, $"{sampleName}-text.png");
            var timer = Stopwatch.StartNew();
            var name = recognizer.Recognize(search, area);
            if (expected[fileName] != name)
                mismatches.Add($"{fileName}: expected '{expected[fileName]}' but got '{name}'");
            timer.Stop();
            using var annotated = search.Clone();
            foreach (var box in recordingDetector.Boxes)
                Cv2.Rectangle(annotated, box, new Scalar(0, 180, 255), 2);
            Cv2.Circle(annotated, new Point((int)area.Anchor.X, (int)area.Anchor.Y), 8, new Scalar(0, 0, 255), 2);
            Cv2.ImWrite(Path.Combine(outputDirectory, $"{sampleName}-detected.jpg"), annotated);
            output.WriteLine($"{sampleName}: '{name}' ({timer.Elapsed.TotalMilliseconds:F1} ms), boxes={recordingDetector.Boxes.Count}");
            results.Add(new { file = fileName, name,
                milliseconds = timer.Elapsed.TotalMilliseconds, boxes = recordingDetector.Boxes });
        }
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "results.json"),
            JsonConvert.SerializeObject(results, Formatting.Indented));
        Assert.Empty(mismatches);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "better_petit_planet.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class RecordingDetector(ITextDetectionEngine inner) : ITextDetectionEngine
    {
        public IReadOnlyList<Rect> Boxes { get; private set; } = [];
        public IReadOnlyList<Rect> Detect(Mat image) => Boxes = inner.Detect(image);
        public void Dispose() { } // Test owns inner engine.
    }

    private sealed class RecordingOcr(IOcrEngine inner) : IOcrEngine
    {
        public string CropPath { get; set; } = string.Empty;
        public string Name => inner.Name;
        public bool IsAvailable => inner.IsAvailable;
        public string Recognize(Mat roiMat)
        {
            Cv2.ImWrite(CropPath, roiMat);
            return inner.Recognize(roiMat);
        }
    }
}

public sealed class ScreenshotFactAttribute : FactAttribute
{
    public ScreenshotFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PETIT_ITEM_NAME_SAMPLES")))
            Skip = "Set PETIT_ITEM_NAME_SAMPLES to a repository-relative PNG sample directory for real-image validation.";
    }
}
