using System;
using System.IO;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameCapture;
using BetterPetitPlanet.GameCapture.Engines.PrintWindow;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace BetterPetitPlanet.Test;

public class LiveCaptureDiagnosticTest
{
    private readonly ITestOutputHelper _output;

    public LiveCaptureDiagnosticTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Test_GameCaptureRegistry_EnginesAreAvailable()
    {
        var engines = GameCaptureRegistry.AvailableEngineNames;
        Assert.Contains("WindowsGraphicsCapture", engines);
        Assert.Contains("PrintWindow", engines);

        using var capture1 = GameCaptureRegistry.Create("PrintWindow");
        Assert.NotNull(capture1);
        Assert.IsType<PrintWindowCapture>(capture1);

        using var capture2 = GameCaptureRegistry.Create("BitBlt"); // Backward compatibility
        Assert.NotNull(capture2);
        Assert.IsType<PrintWindowCapture>(capture2);
    }

    [Fact]
    public void Test_DetectAndCaptureGame_Pipeline()
    {
        var configService = new ConfigService();
        var detector = new GameProcessDetector(configService);
        var hwnd = detector.FindMainWindowHandle();
        _output.WriteLine($"HWND: {hwnd} (0x{hwnd:x})");

        if (hwnd == IntPtr.Zero)
        {
            _output.WriteLine("Game window not currently running, skipping live capture assertion.");
            return;
        }

        Assert.True(detector.IsGameRunning());

        // Test PrintWindowCapture
        using var capture = new PrintWindowCapture();
        capture.Start(hwnd);
        using var frame = capture.Capture();
        Assert.NotNull(frame);
        Assert.False(frame.Frame.Empty());
        _output.WriteLine($"Frame captured: {frame.Frame.Width}x{frame.Frame.Height}");

        int nonZero = Cv2.CountNonZero(frame.Frame.CvtColor(ColorConversionCodes.BGR2GRAY));
        Assert.True(nonZero > 0);
    }
}
