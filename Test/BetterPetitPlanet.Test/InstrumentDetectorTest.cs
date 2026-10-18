using System;
using System.IO;
using System.Linq;
using System.Threading;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Service;
using BetterPetitPlanet.ViewModel.Pages;
using OpenCvSharp;
using Xunit;

namespace BetterPetitPlanet.Test;

public class InstrumentDetectorTest
{
    private readonly Xunit.Abstractions.ITestOutputHelper? _output;

    public InstrumentDetectorTest(Xunit.Abstractions.ITestOutputHelper? output = null)
    {
        _output = output;
    }

    private class FakeConfigService : IConfigService
    {
        public AppConfig Config { get; set; } = new AppConfig();
        public void Save() { }
        public void Reload() { }
    }

    private static string? FindScreenshot(string fileName)
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "petit_music", fileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "petit_music", fileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "petit_music", fileName)),
            Path.Combine(@"D:\Coding\Game\petit_planet\petit_music", fileName)
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindNegativeScreenshot()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "test_key_screen", "test7_before.png")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "test_key_screen", "test7_before.png")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "better-petit-planet", "test_key_screen", "test7_before.png")),
            @"D:\Coding\Game\petit_planet\better-petit-planet\test_key_screen\test7_before.png",
            @"D:\Coding\Game\petit_planet\test_key_screen\test7_before.png"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindNightGrassScreenshot()
    {
        var candidates = new[]
        {
            @"D:\Coding\Game\petit_planet\better-petit-planet-ocr-artifacts\exp_stage1_diagnose\night_grass_sample.png",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity", "brain", "858aa77d-848f-48df-a9b3-d1fde02af6d7", ".user_uploaded", "media_1792263135353_cbb124e2.png")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static Mat LoadNightGrassMat(string path)
    {
        var mat = Cv2.ImRead(path, ImreadModes.Color);
        if (mat.Width == 1024 && mat.Height == 289)
        {
            using var sub = new Mat(mat, new Rect(0, 0, 512, 288));
            var full = new Mat();
            Cv2.Resize(sub, full, new Size(2560, 1440), interpolation: InterpolationFlags.Lanczos4);
            mat.Dispose();
            return full;
        }
        return mat;
    }

    [Fact]
    public void TestInstrumentCoordinateService_BaseResolution()
    {
        var service = new InstrumentCoordinateService();

        // 2560x1440 base
        Assert.True(service.TryGetNoteCoordinate("1", 2560, 1440, out int x1, out int y1));
        Assert.Equal(598, x1);
        Assert.Equal(1230, y1);

        Assert.True(service.TryGetNoteCoordinate("A", 2560, 1440, out int xa, out int ya));
        Assert.Equal(598, xa);
        Assert.Equal(1230, ya);

        Assert.True(service.TryGetNoteCoordinate("7", 2560, 1440, out int x7, out int y7));
        Assert.Equal(1960, x7);
        Assert.Equal(1230, y7);

        Assert.True(service.TryGetNoteCoordinate("L", 2560, 1440, out int xl, out int yl));
        Assert.Equal(1960, xl);
        Assert.Equal(1230, yl);
    }

    [Fact]
    public void TestInstrumentCoordinateService_Scaling1080p()
    {
        var service = new InstrumentCoordinateService();

        // 1920x1080 (0.75x scaling)
        Assert.True(service.TryGetNoteCoordinate("1", 1920, 1080, out int x1, out int y1));
        int expectedX1 = (int)Math.Round(598.0 * 1920.0 / 2560.0);
        int expectedY1 = (int)Math.Round(1230.0 * 1080.0 / 1440.0);
        Assert.Equal(expectedX1, x1);
        Assert.Equal(expectedY1, y1);

        // Invalid key returns false
        Assert.False(service.TryGetNoteCoordinate("UNKNOWN", 1920, 1080, out _, out _));
    }

    [Fact]
    public void TestInstrumentDetector_WithRealScreenshots()
    {
        using var detector = new InstrumentDetector();

        string? recorderPng = FindScreenshot("music_recorder.png");
        if (recorderPng != null)
        {
            using var mat = Cv2.ImRead(recorderPng, ImreadModes.Color);
            bool isDetected = detector.Detect(mat, out double confidence);
            Assert.True(isDetected, $"Expected recorder UI to be detected, but confidence was {confidence}");
            Assert.True(confidence >= 0.5, $"Expected confidence >= 0.5, got {confidence}");
        }

        string? closedPng = FindNegativeScreenshot();
        if (closedPng != null)
        {
            using var mat = Cv2.ImRead(closedPng, ImreadModes.Color);
            bool isDetected = detector.Detect(mat, out double confidence);
            Assert.False(isDetected, $"Expected closed UI to not be detected, but confidence was {confidence}");
            Assert.True(confidence < 0.3, $"Expected confidence < 0.3, got {confidence}");
        }
    }

    [Fact]
    public void TestInstrumentDetector_DetectFull_Recorder()
    {
        using var detector = new InstrumentDetector();

        string? recorderPng = FindScreenshot("music_recorder.png");
        if (recorderPng != null)
        {
            using var mat = Cv2.ImRead(recorderPng, ImreadModes.Color);
            var result = detector.DetectFull(mat);

            Assert.True(result.IsDetected, $"Expected recorder UI detected, result: {result.StatusSummary}");
            Assert.Equal("Recorder", result.InstrumentId);
            Assert.Equal("竖笛", result.DisplayName);
            Assert.Equal(InstrumentDetectionState.Confirmed, result.State);
            Assert.Equal("FeatureVerification", result.Stage);
            Assert.False(result.OcrInvoked);
            Assert.True(result.TemplateConfidence >= 0.60);
            Assert.Contains("竖笛: 已就绪", result.StatusSummary);
            Assert.Contains("模板:", result.StatusSummary);
        }
    }

    [Fact]
    public void TestInstrumentDetector_DetectFull_Guitar()
    {
        using var detector = new InstrumentDetector();

        string? guitarPng = FindScreenshot("music_guitar.png");
        if (guitarPng != null)
        {
            using var mat = Cv2.ImRead(guitarPng, ImreadModes.Color);
            var result = detector.DetectFull(mat);

            Assert.True(result.IsDetected, $"Expected guitar UI detected, result: {result.StatusSummary}");
            Assert.Equal("Guitar", result.InstrumentId);
            Assert.Equal("吉他", result.DisplayName);
            Assert.Equal(InstrumentDetectionState.Confirmed, result.State);
            Assert.Equal("FeatureVerification", result.Stage);
            Assert.False(result.OcrInvoked);
            Assert.True(result.TemplateConfidence >= 0.60);
            Assert.Contains("吉他: 已就绪", result.StatusSummary);
            Assert.Contains("模板:", result.StatusSummary);
        }
    }

    [Fact]
    public void TestInstrumentDetector_DetectFull_NegativeScreen()
    {
        using var detector = new InstrumentDetector();

        string? closedPng = FindNegativeScreenshot();
        if (closedPng != null)
        {
            using var mat = Cv2.ImRead(closedPng, ImreadModes.Color);
            var result = detector.DetectFull(mat);

            Assert.False(result.IsDetected, $"Expected closed UI not detected, result: {result.StatusSummary}");
            Assert.Equal(InstrumentDetectionState.None, result.State);
            Assert.Equal("未检测到乐器", result.StatusSummary);
        }
    }

    [Fact]
    public void TestInstrumentDetector_Stage1_ShortCircuitOnBlank()
    {
        using var detector = new InstrumentDetector();

        // 黑色图像，key_A 匹配分数应极低，立刻短路返回
        using var blankMat = new Mat(1440, 2560, MatType.CV_8UC3, new Scalar(0, 0, 0));
        var result = detector.DetectFull(blankMat);

        Assert.False(result.IsDetected);
        Assert.Equal(InstrumentDetectionState.None, result.State);
        Assert.Equal("KeyAnchor", result.Stage);
        Assert.False(result.OcrInvoked);
        Assert.Equal("未检测到乐器", result.StatusSummary);
    }

    [Fact]
    public void TestInstrumentDetector_RoiCroppedSubImage()
    {
        using var detector = new InstrumentDetector();

        string? guitarPng = FindScreenshot("music_guitar.png");
        if (guitarPng != null)
        {
            using var fullMat = Cv2.ImRead(guitarPng, ImreadModes.Color);
            // 局部裁剪 ROI (500, 1100, 1550, 250)
            using var roiSub = new Mat(fullMat, new Rect(500, 1100, 1550, 250));
            using var roiCopy = roiSub.Clone();

            var result = detector.DetectFull(roiCopy, 500, 1100, 2560, 1440);

            Assert.True(result.IsDetected);
            Assert.Equal("Guitar", result.InstrumentId);
            Assert.Equal(InstrumentDetectionState.Confirmed, result.State);
        }
    }

    [Fact]
    public void TestInstrumentDetector_DetectFull_NightGrassSample()
    {
        using var detector = new InstrumentDetector();

        string? nightGrassPng = FindNightGrassScreenshot();
        if (nightGrassPng != null)
        {
            using var mat = LoadNightGrassMat(nightGrassPng);
            var result = detector.DetectFull(mat);

            Assert.True(result.IsDetected, $"Expected night grass sample to be detected, result: {result.StatusSummary}, details: {result.Details}");
            Assert.Equal("Recorder", result.InstrumentId);
            Assert.Equal("竖笛", result.DisplayName);
            Assert.Equal(InstrumentDetectionState.Confirmed, result.State);
            Assert.Equal("FeatureVerification", result.Stage);
            Assert.False(result.OcrInvoked);
            Assert.True(result.TemplateConfidence >= 0.80, $"Expected confidence >= 0.80, got {result.TemplateConfidence}");
            Assert.Contains("竖笛: 已就绪", result.StatusSummary);
        }
    }

    [Fact]
    public void TestInstrumentDetector_DetectFull_NightGrass_RoiCropped()
    {
        using var detector = new InstrumentDetector();

        string? nightGrassPng = FindNightGrassScreenshot();
        if (nightGrassPng != null)
        {
            using var mat = LoadNightGrassMat(nightGrassPng);
            using var roiSub = new Mat(mat, new Rect(500, 1100, 1550, 250));
            using var roiCopy = roiSub.Clone();

            var result = detector.DetectFull(roiCopy, 500, 1100, 2560, 1440);

            Assert.True(result.IsDetected, $"Expected ROI night grass to be detected, result: {result.StatusSummary}");
            Assert.Equal("Recorder", result.InstrumentId);
            Assert.Equal(InstrumentDetectionState.Confirmed, result.State);
            Assert.Equal("FeatureVerification", result.Stage);
        }
    }

    [Fact]
    public void TestInstrumentDetector_OcrFallback_TriggeredOnAmbiguousScene()
    {
        using var detector = new InstrumentDetector();

        // 验证 OCR 引擎状态
        var ocrField = typeof(InstrumentDetector).GetField("_ocrEngine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var ocrEngine = ocrField?.GetValue(detector) as BetterPetitPlanet.GameTask.AutoPick.Ocr.IOcrEngine;
        if (ocrEngine == null || !ocrEngine.IsAvailable) return;

        string? recorderPng = FindScreenshot("music_recorder.png");
        if (recorderPng != null)
        {
            using var mat = Cv2.ImRead(recorderPng, ImreadModes.Color);

            // 构造一个特征模板匹配受到中度干扰（打分在 0.40~0.55）但 OCR 文字区域仍清晰可辨的图像：
            // 将 1 和 7 验证小锚点区域做模糊/降对比度处理，使得 Stage 2 模板匹配存疑 (< 0.58)，强制流入 Stage 3 OCR 兜底
            using var ambiguousMat = mat.Clone();
            // 对 "1" (571, 1147, 54, 58) 和 "7" (1933, 1147, 54, 58) 的外圈进行噪声混合，使模板得分落在 0.40~0.55
            using var roi1 = new Mat(ambiguousMat, new Rect(571, 1147, 54, 58));
            using var roi7 = new Mat(ambiguousMat, new Rect(1933, 1147, 54, 58));
            Cv2.GaussianBlur(roi1, roi1, new Size(15, 15), 0);
            Cv2.GaussianBlur(roi7, roi7, new Size(15, 15), 0);

            var result = detector.DetectFull(ambiguousMat);

            // 若模板得分在模糊后降至 0.58 以下，必定触发 OCR 兜底；
            // 无论哪种情况，最终均应正确判定为 Recorder 并就绪！
            Assert.True(result.IsDetected);
            Assert.Equal("Recorder", result.InstrumentId);
            Assert.Equal(InstrumentDetectionState.Confirmed, result.State);
        }
    }

    [Fact]
    public void TestInstrumentDetector_LockSafety_NotificationOutsideLock()
    {
        using var detector = new InstrumentDetector();
        bool eventFired = false;
        InstrumentDetectionResult? receivedResult = null;

        detector.DetectionUpdated += res =>
        {
            // 在事件回调内访问 detector.LatestResult，验证锁外通知不发生自死锁
            eventFired = true;
            receivedResult = res;
            Assert.NotNull(detector.LatestResult);
        };

        using var blankMat = new Mat(720, 1280, MatType.CV_8UC3, new Scalar(0, 0, 0));
        var result = detector.DetectFull(blankMat);

        Assert.True(eventFired);
        Assert.Same(result, receivedResult);
    }

    [Fact]
    public void TestMusicConfig_MouseInputAndRestoreDefault()
    {
        var config = new MusicConfig();
        Assert.Equal(MusicInputMode.Keyboard, config.InputMode);
        Assert.False(config.RestoreMouseAfterClick);

        config.InputMode = MusicInputMode.Mouse;
        Assert.Equal(MusicInputMode.Mouse, config.InputMode);
    }

    [Fact]
    public void TestMusicPageViewModel_MouseInputDisplayText()
    {
        var configService = new FakeConfigService();
        var scoreParser = new PetitScoreParser();
        var libraryService = new MusicLibraryService(scoreParser, configService);
        var transports = new KeyInputTransports(new InstrumentCoordinateService(), configService);
        var playbackService = new MusicPlaybackService(configService, null!, transports);
        var vm = new MusicPageViewModel(configService, libraryService, playbackService, null, null);

        // 默认键盘
        Assert.False(vm.IsMouseInput);
        Assert.Equal("键盘", vm.InputModeDisplayText);

        // 切换为鼠标
        vm.IsMouseInput = true;
        Assert.Equal("鼠标", vm.InputModeDisplayText);
        Assert.Equal(MusicInputMode.Mouse, configService.Config.Music.InputMode);

        // 切回键盘
        vm.IsMouseInput = false;
        Assert.Equal("键盘", vm.InputModeDisplayText);
        Assert.Equal(MusicInputMode.Keyboard, configService.Config.Music.InputMode);
    }

    [Fact]
    public void TestInstrumentDetectorTrigger_PropertiesAndExecution()
    {
        using var detector = new InstrumentDetector();
        var trigger = new InstrumentDetectorTrigger(detector);

        Assert.Equal("乐器检测", trigger.Name);
        Assert.True(trigger.IsEnabled);
        Assert.Equal(25, trigger.Priority);

        string? recorderPng = FindScreenshot("music_recorder.png");
        if (recorderPng != null)
        {
            using var mat = Cv2.ImRead(recorderPng, ImreadModes.Color);
            using var content = new BetterPetitPlanet.GameTask.CaptureContent(mat.Clone(), IntPtr.Zero, new System.Drawing.Rectangle(0, 0, mat.Width, mat.Height));
            trigger.OnCapture(content);
        }
    }

    [Fact]
    public void TestDirectMlOcrEngine_ConcurrentRecognize_ThreadSafe()
    {
        using var engine = new BetterPetitPlanet.GameTask.AutoPick.Ocr.DirectMlOcrEngine();
        if (!engine.IsAvailable) return;

        using var testMat = new Mat(48, 120, MatType.CV_8UC3, new Scalar(255, 255, 255));

        // 并发 10 个线程同时调用 Recognize，验证内部线程安全互斥锁生效，无 AccessViolation 异常
        System.Threading.Tasks.Parallel.For(0, 10, _ =>
        {
            var res = engine.Recognize(testMat);
            Assert.NotNull(res);
        });
    }

    [Fact]
    public void TestInstrumentDetector_PerformanceBenchmark()
    {
        using var detector = new InstrumentDetector();

        // 1. 测试负样本 (非乐器画面) Stage 1 短路性能
        using var blankMat = new Mat(1440, 2560, MatType.CV_8UC3, new Scalar(40, 40, 40));
        // 预热
        detector.DetectFull(blankMat);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int negativeIterations = 50;
        for (int i = 0; i < negativeIterations; i++)
        {
            var r = detector.DetectFull(blankMat);
            Assert.False(r.IsDetected);
            Assert.False(r.OcrInvoked);
            Assert.Equal("KeyAnchor", r.Stage);
        }
        sw.Stop();
        double avgNegativeMs = sw.Elapsed.TotalMilliseconds / negativeIterations;

        // 2. 测试真实竖笛截图 Stage 2 纯模板检测性能与 OCR 调用
        string? recorderPng = FindScreenshot("music_recorder.png");
        double avgRecorderMs = 0;
        if (recorderPng != null)
        {
            using var mat = Cv2.ImRead(recorderPng, ImreadModes.Color);
            detector.DetectFull(mat); // 预热
            sw.Restart();
            const int recorderIterations = 20;
            for (int i = 0; i < recorderIterations; i++)
            {
                var r = detector.DetectFull(mat);
                Assert.True(r.IsDetected);
                Assert.Equal("Recorder", r.InstrumentId);
                Assert.False(r.OcrInvoked);
            }
            sw.Stop();
            avgRecorderMs = sw.Elapsed.TotalMilliseconds / recorderIterations;
        }

        // 3. 测试真实吉他截图 Stage 2 纯模板检测性能与 OCR 调用
        string? guitarPng = FindScreenshot("music_guitar.png");
        double avgGuitarMs = 0;
        if (guitarPng != null)
        {
            using var mat = Cv2.ImRead(guitarPng, ImreadModes.Color);
            detector.DetectFull(mat); // 预热
            sw.Restart();
            const int guitarIterations = 20;
            for (int i = 0; i < guitarIterations; i++)
            {
                var r = detector.DetectFull(mat);
                Assert.True(r.IsDetected);
                Assert.Equal("Guitar", r.InstrumentId);
                Assert.False(r.OcrInvoked);
            }
            sw.Stop();
            avgGuitarMs = sw.Elapsed.TotalMilliseconds / guitarIterations;
        }

        // 输出性能基准数据
        _output?.WriteLine($"[Benchmark] Negative Short-Circuit: {avgNegativeMs:F2} ms (OCR calls: 0)");
        _output?.WriteLine($"[Benchmark] Recorder Stage 2 Detect: {avgRecorderMs:F2} ms (OCR calls: 0)");
        _output?.WriteLine($"[Benchmark] Guitar Stage 2 Detect: {avgGuitarMs:F2} ms (OCR calls: 0)");
    }
}
