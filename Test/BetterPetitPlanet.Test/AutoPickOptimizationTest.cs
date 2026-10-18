using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameCapture;
using BetterPetitPlanet.GameTask;
using BetterPetitPlanet.GameTask.AutoPick;
using BetterPetitPlanet.GameTask.AutoPick.Ocr;
using OpenCvSharp;
using Xunit;

namespace BetterPetitPlanet.Test;

public class AutoPickOptimizationTest
{
    private static CaptureContent CreateDummyContent(Mat frame)
    {
        return new CaptureContent(
            frameFactory: () => new GameCaptureFrame(frame.Clone()),
            roiCaptureFunc: roi =>
            {
                var clampedX = Math.Clamp(roi.X, 0, frame.Width - 1);
                var clampedY = Math.Clamp(roi.Y, 0, frame.Height - 1);
                var clampedW = Math.Clamp(roi.Width, 1, frame.Width - clampedX);
                var clampedH = Math.Clamp(roi.Height, 1, frame.Height - clampedY);
                var crop = new Mat(frame, new OpenCvSharp.Rect(clampedX, clampedY, clampedW, clampedH)).Clone();
                return new GameCaptureFrame(crop, null, roi, new OpenCvSharp.Size(frame.Width, frame.Height));
            },
            hwnd: IntPtr.Zero,
            clientRect: new Rectangle(0, 0, frame.Width, frame.Height),
            windowSize: new OpenCvSharp.Size(frame.Width, frame.Height));
    }

    [Fact]
    public void Prescreener_EmptyOrInvalidMat_ReturnsFalse()
    {
        Assert.False(AutoPickPrescreener.CanContainPrompt(null!));
        using var empty = new Mat();
        Assert.False(AutoPickPrescreener.CanContainPrompt(empty));

        using var tooSmall = new Mat(10, 10, MatType.CV_8UC3, Scalar.All(100));
        Assert.False(AutoPickPrescreener.CanContainPrompt(tooSmall));
    }

    [Fact]
    public void Prescreener_UniformEmptyScene_ReturnsFalse()
    {
        // 448x129 empty scene (e.g. green grass / terrain noise)
        using var scene = new Mat(129, 448, MatType.CV_8UC3, new Scalar(40, 100, 30));
        Assert.False(AutoPickPrescreener.CanContainPrompt(scene, "F", 0.70));
    }

    [Fact]
    public void Prescreener_WithEmbeddedFTemplate_ReturnsTrue()
    {
        // Create an artificial ROI with the template embedded in the expected search region
        var tpl = AutoPickPrescreener.Template;
        Assert.NotNull(tpl);
        Assert.False(tpl.Empty());

        using var roi = new Mat(129, 448, MatType.CV_8UC3, new Scalar(138, 189, 255)); // Light pastel blue
        int h = roi.Rows;
        int w = roi.Cols;
        int y1 = (int)(h * 0.35);
        int x1 = (int)(w * 0.32);

        // Stamp template
        using var tplBgr = new Mat();
        Cv2.CvtColor(tpl, tplBgr, ColorConversionCodes.GRAY2BGR);
        var targetRect = new OpenCvSharp.Rect(x1, y1, tpl.Cols, tpl.Rows);
        tplBgr.CopyTo(new Mat(roi, targetRect));

        Assert.True(AutoPickPrescreener.CanContainPrompt(roi, "F", 0.70));
    }

    [Fact]
    public async Task InputChannel_ConcurrentTriggers_AreStrictlySerializedAndNonOverlapping()
    {
        int concurrentActive = 0;
        int maxConcurrency = 0;
        int executedCount = 0;
        var executionLog = new List<(string Action, long Timestamp)>();

        var channel = new AutoPickInputChannel(
            logger: null,
            suppressCheck: () => false,
            customExecutor: async (key, holdMs, hwnd, bg) =>
            {
                int current = Interlocked.Increment(ref concurrentActive);
                lock (executionLog)
                {
                    if (current > maxConcurrency) maxConcurrency = current;
                    executionLog.Add(("KeyDown", Stopwatch.GetTimestamp()));
                }

                await Task.Delay(holdMs);

                lock (executionLog)
                {
                    executionLog.Add(("KeyUp", Stopwatch.GetTimestamp()));
                    Interlocked.Decrement(ref concurrentActive);
                    Interlocked.Increment(ref executedCount);
                }
            });

        // Fire 10 concurrent trigger attempts
        var tasks = new List<bool>();
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(channel.TryTrigger("F", 30, 20, IntPtr.Zero, false));
        }

        // Wait for execution to finish
        await Task.Delay(100);

        // Exactly one should have executed concurrently
        Assert.Equal(1, maxConcurrency);
        Assert.True(executedCount >= 1);
        Assert.False(channel.IsBusy);
    }

    [Fact]
    public async Task InputChannel_CooldownIsEnforcedStrictlyAfterKeyUp()
    {
        int keyUpCount = 0;
        long keyUpTime = 0;

        var channel = new AutoPickInputChannel(
            logger: null,
            suppressCheck: null,
            customExecutor: async (key, holdMs, hwnd, bg) =>
            {
                await Task.Delay(holdMs);
                keyUpCount++;
                keyUpTime = Stopwatch.GetTimestamp();
            });

        // Trigger with 25ms hold and 80ms cooldown
        bool first = channel.TryTrigger("F", holdDurationMs: 25, cooldownMs: 80, IntPtr.Zero, false);
        Assert.True(first);

        // While key is held down (within first 15ms): TryTrigger must be rejected because IsBusy is true
        Assert.True(channel.IsBusy);
        Assert.False(channel.TryTrigger("F", 25, 80, IntPtr.Zero, false));

        // Wait until key is released (typically ~25ms, bounded to 80ms)
        var sw = Stopwatch.StartNew();
        while (channel.IsBusy && sw.ElapsedMilliseconds < 100)
        {
            await Task.Delay(5);
        }
        Assert.False(channel.IsBusy);

        // Key has just been released, but cooldown (80ms after KeyUp) is nowhere near elapsed:
        Assert.False(channel.IsCooldownReady(80));
        Assert.False(channel.TryTrigger("F", 25, 80, IntPtr.Zero, false));

        // Wait for cooldown to fully expire
        await Task.Delay(90);
        Assert.True(channel.IsCooldownReady(80));
        Assert.True(channel.TryTrigger("F", 25, 80, IntPtr.Zero, false));
    }

    [Fact]
    public void InputChannel_SuppressionCheck_RejectsTrigger()
    {
        bool suppressed = true;
        var channel = new AutoPickInputChannel(null, () => suppressed);
        Assert.False(channel.TryTrigger("F", 40, 50, IntPtr.Zero, false));

        suppressed = false;
        Assert.True(channel.TryTrigger("F", 40, 50, IntPtr.Zero, false));
    }

    [Fact]
    public void StateTracker_ContinuousPicking_ArmsOnDisappearanceWaveform()
    {
        var tracker = new AutoPickStateTracker(reArmSafetyTimeoutMs: 300);

        // Flower 1 appears
        Assert.True(tracker.ShouldTrigger(hasPrompt: true, cooldownReady: true));
        tracker.RecordTrigger();

        // While flower 1 prompt remains on screen, further triggers are blocked
        Assert.False(tracker.ShouldTrigger(hasPrompt: true, cooldownReady: true));

        // Flower 1 is picked and prompt vanishes (waveform valley: 1 empty frame)
        Assert.False(tracker.ShouldTrigger(hasPrompt: false, cooldownReady: true));
        Assert.True(tracker.IsArmed); // Tracker re-armed upon seeing prompt disappearance!

        // Flower 2 appears with the exact same name and position!
        // Tracker allows Flower 2 to trigger immediately without missing
        Assert.True(tracker.ShouldTrigger(hasPrompt: true, cooldownReady: true));
    }

    [Fact]
    public async Task StateTracker_DenseFlowerField_SafetyTimeoutReArms()
    {
        var tracker = new AutoPickStateTracker(reArmSafetyTimeoutMs: 50);

        // Flower 1 triggered
        Assert.True(tracker.ShouldTrigger(hasPrompt: true, cooldownReady: true));
        tracker.RecordTrigger();

        // Prompt never disappears (dense overlapping flowers)
        Assert.False(tracker.ShouldTrigger(hasPrompt: true, cooldownReady: true));

        // After safety timeout expires, tracker re-arms automatically
        await Task.Delay(70);
        Assert.True(tracker.ShouldTrigger(hasPrompt: true, cooldownReady: true));
    }

    [Fact]
    public void AutoPickTrigger_BlacklistInterception_NeverTriggersKey()
    {
        var config = new AppConfig();
        config.General.RunInBackground = true;
        config.AutoPick.Enabled = true;
        config.AutoPick.FastPrescreenEnabled = false; // direct OCR test
        config.AutoPick.Blacklist = ["拾取雪球", "采矿"];
        config.AutoPick.Whitelist = ["拾取"];
        var configService = new TestConfigService(config);
        var processDetector = new GameProcessDetector(configService);
        var ocrEngine = new TestOcrEngine { RecognizedResult = "拾取雪球" };
        var inputChannel = new TestInputChannel();

        using var trigger = new AutoPickTrigger(
            configService,
            processDetector,
            ocrEngine,
            logger: null,
            itemNameRecognition: null,
            inputChannel: inputChannel);

        using var dummyFrame = new Mat(1440, 2560, MatType.CV_8UC3, Scalar.All(100));
        using var content = CreateDummyContent(dummyFrame);

        trigger.OnCapture(content);

        // Input channel TryTrigger must NEVER be called for blacklisted candidate
        Assert.Equal(0, inputChannel.TriggerCallCount);
    }

    [Fact]
    public void AutoPickTrigger_WhitelistMatch_SuccessfullyTriggers()
    {
        var config = new AppConfig();
        config.General.RunInBackground = true;
        config.AutoPick.Enabled = true;
        config.AutoPick.FastPrescreenEnabled = false;
        config.AutoPick.Whitelist = ["拾取"];
        var configService = new TestConfigService(config);
        var processDetector = new GameProcessDetector(configService);
        var ocrEngine = new TestOcrEngine { RecognizedResult = "F 拾取" };
        var inputChannel = new TestInputChannel();

        using var trigger = new AutoPickTrigger(
            configService,
            processDetector,
            ocrEngine,
            logger: null,
            itemNameRecognition: null,
            inputChannel: inputChannel);

        using var dummyFrame = new Mat(1440, 2560, MatType.CV_8UC3, Scalar.All(100));
        using var content = CreateDummyContent(dummyFrame);

        trigger.OnCapture(content);

        // Input channel should be triggered with configured parameters
        Assert.Equal(1, inputChannel.TriggerCallCount);
        Assert.Equal("F", inputChannel.LastKeyName);
    }

    [Fact]
    public void AutoPickTrigger_PrescreenEmptyScene_BypassesOcrEngine()
    {
        var config = new AppConfig();
        config.General.RunInBackground = true;
        config.AutoPick.Enabled = true;
        config.AutoPick.FastPrescreenEnabled = true;
        var configService = new TestConfigService(config);
        var processDetector = new GameProcessDetector(configService);
        var ocrEngine = new TestOcrEngine { RecognizedResult = "拾取" };
        var inputChannel = new TestInputChannel();

        using var trigger = new AutoPickTrigger(
            configService,
            processDetector,
            ocrEngine,
            logger: null,
            itemNameRecognition: null,
            inputChannel: inputChannel);

        // Entirely green terrain frame (no F button)
        using var dummyFrame = new Mat(1440, 2560, MatType.CV_8UC3, new Scalar(30, 90, 20));
        using var content = CreateDummyContent(dummyFrame);

        trigger.OnCapture(content);

        // OCR engine must NEVER be called because pre-screen filtered out the empty frame!
        Assert.Equal(0, ocrEngine.CallCount);
        Assert.Equal(0, inputChannel.TriggerCallCount);
    }

    [Fact]
    public void Prescreener_Evaluate_ReturnsRichDiagnostics()
    {
        using var empty = new Mat(136, 423, MatType.CV_8UC3, new Scalar(30, 90, 20));
        var res = AutoPickPrescreener.Evaluate(empty, "F", 0.60);

        Assert.False(res.CanContainPrompt);
        Assert.Contains("得分不足", res.Reason);
        Assert.True(res.Score < 0.60);
    }

    [Fact]
    public void Prescreener_WithRealFButtonTemplate_MatchesCorrectly()
    {
        var tpl = AutoPickPrescreener.Template;
        Assert.NotNull(tpl);
        Assert.False(tpl.Empty());
        Assert.Equal(35, tpl.Cols);
        Assert.Equal(37, tpl.Rows);

        using var roi = new Mat(136, 423, MatType.CV_8UC3, new Scalar(45, 55, 60)); // Dark brown background
        using var tplBgr = new Mat();
        Cv2.CvtColor(tpl, tplBgr, ColorConversionCodes.GRAY2BGR);
        var targetRect = new OpenCvSharp.Rect(10, 40, tpl.Cols, tpl.Rows);
        tplBgr.CopyTo(new Mat(roi, targetRect));

        var res = AutoPickPrescreener.Evaluate(roi, "F", 0.60);
        Assert.True(res.CanContainPrompt);
        Assert.True(res.Score >= 0.90);
        Assert.Contains("F键模板命中", res.Reason);
    }

    [Fact]
    public void AutoPickTrigger_SessionManagement_AcquiresAndReleasesSession()
    {
        var config = new AppConfig();
        config.AutoPick.Enabled = true;
        var configService = new TestConfigService(config);
        var detector = new GameProcessDetector(configService);
        var dispatcher = new TaskTriggerDispatcher();
        var taskManager = new GameTaskManager(configService, detector, dispatcher);

        using var trigger = new AutoPickTrigger(
            configService,
            detector,
            new TestOcrEngine(),
            taskManager: taskManager);

        Assert.True(taskManager.IsConsumerActive("AutoPick"));

        trigger.IsEnabled = false;
        Assert.False(taskManager.IsConsumerActive("AutoPick"));

        trigger.IsEnabled = true;
        Assert.True(taskManager.IsConsumerActive("AutoPick"));
    }

    [Fact]
    public void AutoPickTrigger_EndToEndWithPrescreenerAndWhitelist_TriggersKey()
    {
        var config = new AppConfig();
        config.General.RunInBackground = true;
        config.AutoPick.Enabled = true;
        config.AutoPick.FastPrescreenEnabled = true;
        config.AutoPick.PrescreenMinScore = 0.60;
        config.AutoPick.Whitelist = ["拾取"];
        config.AutoPick.PickKey = "F";

        var configService = new TestConfigService(config);
        var detector = new GameProcessDetector(configService);
        var ocrEngine = new TestOcrEngine { RecognizedResult = "拾取" };
        var inputChannel = new TestInputChannel();

        using var trigger = new AutoPickTrigger(
            configService,
            detector,
            ocrEngine,
            logger: null,
            inputChannel: inputChannel);

        // Create a 2560x1440 dummy frame
        using var frame = new Mat(1440, 2560, MatType.CV_8UC3, new Scalar(40, 50, 45));

        // In the ROI calculated by PetitRoiCalculator (X: [1651, 2074], Y: [814, 950]):
        // Stamp the F-button template in the search area
        var tpl = AutoPickPrescreener.Template;
        using var tplBgr = new Mat();
        Cv2.CvtColor(tpl, tplBgr, ColorConversionCodes.GRAY2BGR);
        // Stamp at X: 1670, Y: 840
        var stampRect = new OpenCvSharp.Rect(1670, 840, tpl.Cols, tpl.Rows);
        tplBgr.CopyTo(new Mat(frame, stampRect));

        using var content = CreateDummyContent(frame);

        trigger.OnCapture(content);

        // Pre-screen passed -> OCR ran -> Whitelist matched -> Key injected!
        Assert.Equal(1, ocrEngine.CallCount);
        Assert.Equal(1, inputChannel.TriggerCallCount);
        Assert.Equal("F", inputChannel.LastKeyName);
    }

    private sealed class TestConfigService(AppConfig config) : IConfigService
    {
        public AppConfig Config { get; set; } = config;
        public void Save() { }
        public void Reload() { }
    }

    private sealed class TestOcrEngine : IOcrEngine
    {
        public string Name => "Test";
        public bool IsAvailable => true;
        public string RecognizedResult { get; set; } = string.Empty;
        public int CallCount { get; private set; }

        public string Recognize(Mat roiMat)
        {
            CallCount++;
            return RecognizedResult;
        }
    }

    private sealed class TestInputChannel : IAutoPickInputChannel
    {
        public bool IsBusy => false;
        public long LastKeyUpTimestamp => 0;
        public int TriggerCallCount { get; private set; }
        public string? LastKeyName { get; private set; }

        public bool IsCooldownReady(int cooldownMs) => true;

        public bool TryTrigger(string keyName, int holdDurationMs, int cooldownMs, IntPtr hwnd, bool runInBackground)
        {
            TriggerCallCount++;
            LastKeyName = keyName;
            return true;
        }

        public void ForceReleaseAll() { }
        public void Dispose() { }
    }
}
