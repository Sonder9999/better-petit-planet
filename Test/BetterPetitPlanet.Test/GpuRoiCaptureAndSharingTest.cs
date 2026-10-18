using System;
using System.Drawing;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameCapture;
using BetterPetitPlanet.GameTask;
using OpenCvSharp;
using Xunit;

namespace BetterPetitPlanet.Test;

public class GpuRoiCaptureAndSharingTest
{
    [Fact]
    public void GameCaptureFrame_CarriesRichMetadata()
    {
        using var mat = new Mat(250, 1550, MatType.CV_8UC3, Scalar.All(100));
        var roi = new OpenCvSharp.Rect(500, 1100, 1550, 250);
        var origSize = new OpenCvSharp.Size(2560, 1440);
        var timestamp = DateTime.UtcNow;
        double monoMs = 12345.67;

        using var frame = new GameCaptureFrame(
            mat,
            captureRect: null,
            roiRect: roi,
            originalWindowSize: origSize,
            captureTimestamp: timestamp,
            monotonicTimestampMs: monoMs,
            isHdr: false,
            dpiScale: 1.25);

        Assert.Equal(roi, frame.RoiRect);
        Assert.Equal(origSize, frame.OriginalWindowSize);
        Assert.Equal(timestamp, frame.CaptureTimestamp);
        Assert.Equal(monoMs, frame.MonotonicTimestampMs);
        Assert.False(frame.IsHdr);
        Assert.Equal(1.25, frame.DpiScale);
    }

    [Fact]
    public void CaptureContent_GetRoi_ExtractsWithoutEvaluatingFullFrame()
    {
        bool frameFactoryCalled = false;
        bool roiCaptureCalled = false;

        using var roiMat = new Mat(250, 1550, MatType.CV_8UC3, Scalar.All(200));
        var requestedRoi = new OpenCvSharp.Rect(500, 1100, 1550, 250);

        using var content = new CaptureContent(
            frameFactory: () =>
            {
                frameFactoryCalled = true;
                return new GameCaptureFrame(new Mat(1440, 2560, MatType.CV_8UC3, Scalar.All(50)));
            },
            roiCaptureFunc: roi =>
            {
                roiCaptureCalled = true;
                return new GameCaptureFrame(roiMat.Clone(), null, roi, new OpenCvSharp.Size(2560, 1440));
            },
            hwnd: IntPtr.Zero,
            clientRect: new Rectangle(0, 0, 2560, 1440),
            windowSize: new OpenCvSharp.Size(2560, 1440));

        Assert.True(content.IsValid);
        Assert.Equal(2560, content.Width);
        Assert.Equal(1440, content.Height);

        // Get ROI
        var extracted = content.GetRoi(requestedRoi);
        Assert.NotNull(extracted);
        Assert.True(roiCaptureCalled);
        Assert.False(frameFactoryCalled); // Full frame was never mapped/realized!

        // Repeat call hits cache
        roiCaptureCalled = false;
        var cached = content.GetRoi(requestedRoi);
        Assert.Same(extracted, cached);
        Assert.False(roiCaptureCalled);
        Assert.False(frameFactoryCalled);
    }

    [Fact]
    public void CaptureContent_Frame_EvaluatesLazilyWhenExplicitlyRequested()
    {
        bool frameFactoryCalled = false;
        using var fullMat = new Mat(1440, 2560, MatType.CV_8UC3, Scalar.All(50));

        using var content = new CaptureContent(
            frameFactory: () =>
            {
                frameFactoryCalled = true;
                return new GameCaptureFrame(fullMat.Clone());
            },
            roiCaptureFunc: null,
            hwnd: IntPtr.Zero,
            clientRect: new Rectangle(0, 0, 2560, 1440),
            windowSize: new OpenCvSharp.Size(2560, 1440));

        Assert.False(frameFactoryCalled);
        var frame = content.Frame;
        Assert.NotNull(frame);
        Assert.True(frameFactoryCalled);
        Assert.Equal(2560, frame.Width);
        Assert.Equal(1440, frame.Height);
    }

    [Fact]
    public void MultiConsumerSharing_StoppingMusic_DoesNotStopAutoPick()
    {
        var config = new ConfigService();
        var process = new GameProcessDetector(config);
        var dispatcher = new TaskTriggerDispatcher();
        using var manager = new GameTaskManager(config, process, dispatcher);

        // AutoPick registers session
        manager.AcquireSession("AutoPick");
        Assert.True(manager.IsConsumerActive("AutoPick"));

        // Music registers session
        manager.AcquireSession("Music");
        Assert.True(manager.IsConsumerActive("Music"));

        // Music stops / finishes
        manager.ReleaseSession("Music");
        Assert.False(manager.IsConsumerActive("Music"));

        // AutoPick session MUST remain active and undisturbed!
        Assert.True(manager.IsConsumerActive("AutoPick"));

        // When AutoPick releases, both are inactive
        manager.ReleaseSession("AutoPick");
        Assert.False(manager.IsConsumerActive("AutoPick"));
    }
}
