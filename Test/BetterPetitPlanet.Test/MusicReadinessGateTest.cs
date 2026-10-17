using BetterPetitPlanet.GameTask.Music.Service;
using Xunit;

namespace BetterPetitPlanet.Test;

public class MusicReadinessGateTest
{
    [Fact]
    public void CaptureRestartNeedsNewCaptureAndTwoNewDetections()
    {
        var gate = new MusicReadinessGate(new MusicReadinessOptions());
        gate.ObserveCapture(0);
        gate.ObserveDetection(true, 0);
        gate.ObserveDetection(true, 100);
        Assert.True(gate.Evaluate(100, true, true, true).Ready);
        gate.Invalidate();
        Assert.False(gate.Evaluate(200, true, true, true).Ready);
        gate.ObserveCapture(200);
        gate.ObserveDetection(true, 200);
        Assert.False(gate.Evaluate(200, true, true, true).Ready);
        gate.ObserveDetection(true, 300);
        Assert.True(gate.Evaluate(300, true, true, true).Ready);
    }

    [Fact]
    public void DebounceRequiresTwoSuccessesAndThreeFailures()
    {
        var gate = new MusicReadinessGate(new MusicReadinessOptions());
        gate.ObserveCapture(0);
        gate.ObserveDetection(true, 0);
        Assert.False(gate.Evaluate(0, true, true, true).Ready);
        gate.ObserveDetection(true, 400);
        Assert.True(gate.Evaluate(400, true, true, true).Ready);
        gate.ObserveDetection(false, 800);
        gate.ObserveDetection(false, 1200);
        Assert.True(gate.Evaluate(1200, true, true, true).Ready);
        gate.ObserveDetection(false, 1600);
        Assert.False(gate.Evaluate(1600, true, true, true).Ready);
    }

    [Theory]
    [InlineData(false, true, true, "游戏未运行")]
    [InlineData(true, false, true, "游戏失焦或最小化")]
    [InlineData(true, true, false, "截图已停止")]
    public void SafetyConditionsOverrideStableOcr(bool exists, bool foreground, bool capture, string reason)
    {
        var gate = new MusicReadinessGate(new MusicReadinessOptions());
        gate.ObserveCapture(100);
        gate.ObserveDetection(true, 100);
        gate.ObserveDetection(true, 200);
        var result = gate.Evaluate(200, exists, foreground, capture);
        Assert.False(result.Ready);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void FreshCaptureDoesNotRefreshOldOcr()
    {
        var gate = new MusicReadinessGate(new MusicReadinessOptions());
        gate.ObserveDetection(true, 0);
        gate.ObserveDetection(true, 100);
        gate.ObserveCapture(2400);
        Assert.Equal("检测结果已过期", gate.Evaluate(2400, true, true, true).Reason);
        gate.ObserveDetection(true, 2500);
        Assert.False(gate.Evaluate(2500, true, true, true).Ready);
    }

    [Fact]
    public void CapturesMustBeFreshEvenIfOcrIsFresh()
    {
        var gate = new MusicReadinessGate(new MusicReadinessOptions());
        gate.ObserveCapture(0);
        gate.ObserveDetection(true, 2000);
        gate.ObserveDetection(true, 2300);
        Assert.Equal("截图已过期", gate.Evaluate(2300, true, true, true).Reason);
    }
}
