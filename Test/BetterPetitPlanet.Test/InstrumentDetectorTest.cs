using System;
using System.IO;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Service;
using BetterPetitPlanet.ViewModel.Pages;
using OpenCvSharp;
using Xunit;

namespace BetterPetitPlanet.Test;

public class InstrumentDetectorTest
{
    private class FakeConfigService : IConfigService
    {
        public AppConfig Config { get; set; } = new AppConfig();
        public void Save() { }
        public void Reload() { }
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

        string recorderPng = @"D:\Coding\Game\petit_planet\petit_music\music_recorder.png";
        if (File.Exists(recorderPng))
        {
            using var mat = Cv2.ImRead(recorderPng, ImreadModes.Color);
            bool isDetected = detector.Detect(mat, out double confidence);
            Assert.True(isDetected, $"Expected recorder UI to be detected, but confidence was {confidence}");
            Assert.True(confidence >= 0.5, $"Expected confidence >= 0.5, got {confidence}");
        }

        string closedPng = @"D:\Coding\Game\petit_planet\better-petit-planet\test_key_screen\test7_before.png";
        if (File.Exists(closedPng))
        {
            using var mat = Cv2.ImRead(closedPng, ImreadModes.Color);
            bool isDetected = detector.Detect(mat, out double confidence);
            Assert.False(isDetected, $"Expected closed UI to not be detected, but confidence was {confidence}");
            Assert.True(confidence < 0.3, $"Expected confidence < 0.3, got {confidence}");
        }
    }

    [Fact]
    public void TestInstrumentDetector_DetectFull_PureOcr()
    {
        using var detector = new InstrumentDetector();

        string recorderPng = @"D:\Coding\Game\petit_planet\petit_music\music_recorder.png";
        if (File.Exists(recorderPng))
        {
            using var mat = Cv2.ImRead(recorderPng, ImreadModes.Color);
            var result = detector.DetectFull(mat);
            Assert.True(result.IsDetected, $"Expected recorder UI detected, result: {result.StatusSummary}");
            Assert.True(result.OcrConfidence >= 0.40);
            Assert.Equal(0.0, result.TemplateConfidence);
            Assert.Contains("OCR:", result.Details);
            Assert.Contains("已就绪", result.StatusSummary);
        }

        string closedPng = @"D:\Coding\Game\petit_planet\better-petit-planet\test_key_screen\test7_before.png";
        if (File.Exists(closedPng))
        {
            using var mat = Cv2.ImRead(closedPng, ImreadModes.Color);
            var result = detector.DetectFull(mat);
            Assert.False(result.IsDetected, $"Expected closed UI not detected, result: {result.StatusSummary}");
            Assert.True(result.OcrConfidence < 0.30);
            Assert.Contains("未拿出", result.StatusSummary);
        }
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

        string recorderPng = @"D:\Coding\Game\petit_planet\petit_music\music_recorder.png";
        if (File.Exists(recorderPng))
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

        // 并发 6 个线程同时调用 Recognize，验证内部线程安全互斥锁生效，无 AccessViolation 异常
        System.Threading.Tasks.Parallel.For(0, 10, _ =>
        {
            var res = engine.Recognize(testMat);
            Assert.NotNull(res);
        });
    }
}
