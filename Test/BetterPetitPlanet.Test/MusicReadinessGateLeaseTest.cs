using System;
using BetterPetitPlanet.GameTask.Music.Service;
using Xunit;

namespace BetterPetitPlanet.Test;

public class MusicReadinessGateLeaseTest
{
    [Fact]
    public void AttachedStateMachine_ValidLeaseExemptsDetectionAgeDuringActivePerformance()
    {
        var options = new MusicReadinessOptions
        {
            ScreenshotMaxAge = TimeSpan.FromMilliseconds(2000),
            DetectionMaxAge = TimeSpan.FromMilliseconds(2000)
        };
        var sm = new InstrumentStateMachine(new InstrumentStateMachineOptions
        {
            ActiveLeaseDuration = TimeSpan.FromMilliseconds(3000),
            RequiredSuccessCount = 2
        });
        var gate = new MusicReadinessGate(options) { StateMachine = sm, TargetInstrument = "吉他" };

        double now = 1000;
        gate.ObserveCapture(now);
        gate.ObserveDetection(true, now, "guitar", "吉他");
        gate.ObserveDetection(true, now + 200, "guitar", "吉他");

        // Confirmed state
        var eval1 = gate.Evaluate(now + 200, true, true, true);
        Assert.True(eval1.Ready);
        Assert.Equal("吉他已就绪", eval1.Reason);

        // Notify playback started -> Active state
        sm.NotifyPlaybackStarted(now + 200);
        Assert.Equal(InstrumentLifecycleState.Active, sm.CurrentState);

        // Fast-forward by 2500ms (detection age > 2000ms detection freshness threshold)
        // Without lease, this would fail with "检测结果已过期".
        // With active lease (valid for 3000ms until now + 200 + 3000 = 4200ms), playback continues smoothly!
        double jitterTime = now + 2700;
        gate.ObserveCapture(jitterTime); // capture is kept fresh or within lease
        var eval2 = gate.Evaluate(jitterTime, true, true, true);
        Assert.True(eval2.Ready);
        Assert.Equal("吉他已就绪", eval2.Reason);

        // But beyond lease expiration (e.g. 5000ms) with no renewed detection, lease expires:
        double expiredTime = now + 4500;
        var eval3 = gate.Evaluate(expiredTime, true, true, true);
        Assert.False(eval3.Ready);
        Assert.Equal("检测结果已过期", eval3.Reason);
    }

    [Fact]
    public void InstrumentLost_ImmediatelyRevokesLeaseAndDeniesReadiness()
    {
        var sm = new InstrumentStateMachine(new InstrumentStateMachineOptions
        {
            ActiveLeaseDuration = TimeSpan.FromMilliseconds(3000),
            RequiredSuccessCount = 2,
            RequiredFailureCount = 2
        });
        var gate = new MusicReadinessGate(new MusicReadinessOptions()) { StateMachine = sm, TargetInstrument = "竖笛" };

        double now = 1000;
        gate.ObserveCapture(now);
        gate.ObserveDetection(true, now, "recorder", "竖笛");
        gate.ObserveDetection(true, now + 100, "recorder", "竖笛");
        sm.NotifyPlaybackStarted(now + 100);

        Assert.True(gate.Evaluate(now + 100, true, true, true).Ready);

        // Instrument lost (consecutive non-detections)
        gate.ObserveDetection(false, now + 500);
        gate.ObserveDetection(false, now + 900);

        Assert.Equal(InstrumentLifecycleState.Lost, sm.CurrentState);
        Assert.False(sm.HasValidLease(now + 900));

        var eval = gate.Evaluate(now + 900, true, true, true);
        Assert.False(eval.Ready);
        Assert.Equal("等待竖笛", eval.Reason);
    }
}
