using System;
using System.Collections.Generic;
using BetterPetitPlanet.GameTask.Music.Service;
using Xunit;

namespace BetterPetitPlanet.Test;

public class InstrumentStateMachineTest : IDisposable
{
    private readonly InstrumentStateMachine _sm;
    private readonly List<(InstrumentLifecycleState Old, InstrumentLifecycleState New)> _transitions = [];
    private readonly List<string> _confirmed = [];
    private readonly List<string> _lost = [];

    public InstrumentStateMachineTest()
    {
        _sm = new InstrumentStateMachine(new InstrumentStateMachineOptions
        {
            IdleInterval = TimeSpan.FromMilliseconds(10000),
            ProbingInterval = TimeSpan.FromMilliseconds(200),
            ConfirmedInterval = TimeSpan.FromMilliseconds(1000),
            ActiveInterval = TimeSpan.FromMilliseconds(800),
            LostInterval = TimeSpan.FromMilliseconds(500),
            ProbingTimeout = TimeSpan.FromMilliseconds(2000),
            RequiredSuccessCount = 2,
            RequiredFailureCount = 3,
            ActiveLeaseDuration = TimeSpan.FromMilliseconds(3000)
        });

        _sm.StateChanged += (o, n) => _transitions.Add((o, n));
        _sm.InstrumentConfirmed += name => _confirmed.Add(name);
        _sm.InstrumentLost += reason => _lost.Add(reason);
    }

    public void Dispose()
    {
        _sm.Dispose();
    }

    [Fact]
    public void InitialState_IsIdle_AndThrottlesToIdleInterval()
    {
        Assert.Equal(InstrumentLifecycleState.Idle, _sm.CurrentState);
        Assert.False(_sm.IsInstrumentReady);
        Assert.Equal(10000, _sm.CurrentInterval.TotalMilliseconds);

        // At 0ms, should process (first time)
        Assert.True(_sm.ShouldProcessCapture(0));
        _sm.ReportDetectionResult(new InstrumentDetectionResult { State = InstrumentDetectionState.None }, 0);

        // At 100ms, should throttle
        Assert.False(_sm.ShouldProcessCapture(100));

        // At 10001ms, should process
        Assert.True(_sm.ShouldProcessCapture(10001));
    }

    [Fact]
    public void TriggerProbe_TransitionsIdleToProbing_AndBypassesThrottle()
    {
        _sm.TriggerProbe("User opened overlay");
        Assert.Equal(InstrumentLifecycleState.Probing, _sm.CurrentState);
        Assert.Equal(200, _sm.CurrentInterval.TotalMilliseconds);

        // Immediate check is allowed due to force probe flag
        Assert.True(_sm.ShouldProcessCapture(10));
        _sm.ReportDetectionResult(new InstrumentDetectionResult { State = InstrumentDetectionState.None }, 10);

        // At 100ms, throttled because 200ms interval hasn't elapsed
        Assert.False(_sm.ShouldProcessCapture(100));

        // At 215ms, allowed
        Assert.True(_sm.ShouldProcessCapture(215));
    }

    [Fact]
    public void TwoSuccessiveDetections_TransitionProbingToConfirmed_WithLease()
    {
        _sm.TriggerProbe("Song loaded", 0);
        Assert.Equal(InstrumentLifecycleState.Probing, _sm.CurrentState);

        // 1st success
        _sm.ReportDetectionResult(new InstrumentDetectionResult
        {
            InstrumentId = "recorder",
            DisplayName = "竖笛",
            State = InstrumentDetectionState.Confirmed
        }, 100);
        Assert.Equal(InstrumentLifecycleState.Probing, _sm.CurrentState);
        Assert.False(_sm.IsInstrumentReady);

        // 2nd success
        _sm.ReportDetectionResult(new InstrumentDetectionResult
        {
            InstrumentId = "recorder",
            DisplayName = "竖笛",
            State = InstrumentDetectionState.Confirmed
        }, 300);

        Assert.Equal(InstrumentLifecycleState.Confirmed, _sm.CurrentState);
        Assert.True(_sm.IsInstrumentReady);
        Assert.Equal("recorder", _sm.ConfirmedInstrumentId);
        Assert.Equal("竖笛", _sm.ConfirmedDisplayName);
        Assert.Single(_confirmed);
        Assert.Equal("竖笛", _confirmed[0]);

        // Lease valid for 3000ms from 300ms = 3300ms
        Assert.True(_sm.HasValidLease(300));
        Assert.True(_sm.HasValidLease(3299));
        Assert.False(_sm.HasValidLease(3301));
    }

    [Fact]
    public void ConfirmedToActive_MaintainsLeaseAndTransitionsSmoothly()
    {
        _sm.TriggerProbe("Play", 0);
        _sm.ReportDetectionResult(new InstrumentDetectionResult { InstrumentId = "guitar", DisplayName = "吉他", State = InstrumentDetectionState.Confirmed }, 100);
        _sm.ReportDetectionResult(new InstrumentDetectionResult { InstrumentId = "guitar", DisplayName = "吉他", State = InstrumentDetectionState.Confirmed }, 300);
        Assert.Equal(InstrumentLifecycleState.Confirmed, _sm.CurrentState);

        // Playback starts
        _sm.NotifyPlaybackStarted(300);
        Assert.Equal(InstrumentLifecycleState.Active, _sm.CurrentState);
        Assert.True(_sm.IsInstrumentReady);
        Assert.Equal(800, _sm.CurrentInterval.TotalMilliseconds);

        // Successful detection during active renews lease
        _sm.ReportDetectionResult(new InstrumentDetectionResult { InstrumentId = "guitar", DisplayName = "吉他", State = InstrumentDetectionState.Confirmed }, 1100);
        Assert.True(_sm.HasValidLease(4000));

        // Playback stops
        _sm.NotifyPlaybackStopped();
        Assert.Equal(InstrumentLifecycleState.Confirmed, _sm.CurrentState);
    }

    [Fact]
    public void ThreeConsecutiveFailures_TransitionsActiveToLost_AndRevokesLease()
    {
        _sm.TriggerProbe("Play", 0);
        _sm.ReportDetectionResult(new InstrumentDetectionResult { InstrumentId = "guitar", DisplayName = "吉他", State = InstrumentDetectionState.Confirmed }, 100);
        _sm.ReportDetectionResult(new InstrumentDetectionResult { InstrumentId = "guitar", DisplayName = "吉他", State = InstrumentDetectionState.Confirmed }, 300);
        _sm.NotifyPlaybackStarted(300);
        Assert.Equal(InstrumentLifecycleState.Active, _sm.CurrentState);

        // 1st failure (e.g. transient occlusion)
        _sm.ReportDetectionResult(new InstrumentDetectionResult { State = InstrumentDetectionState.None }, 1100);
        Assert.Equal(InstrumentLifecycleState.Active, _sm.CurrentState);
        Assert.True(_sm.HasValidLease(1100)); // Lease protects playback!

        // 2nd failure
        _sm.ReportDetectionResult(new InstrumentDetectionResult { State = InstrumentDetectionState.None }, 1900);
        Assert.Equal(InstrumentLifecycleState.Active, _sm.CurrentState);
        Assert.True(_sm.HasValidLease(1900));

        // 3rd failure -> user put away instrument or pressed Esc
        _sm.ReportDetectionResult(new InstrumentDetectionResult { State = InstrumentDetectionState.None }, 2700);
        Assert.Equal(InstrumentLifecycleState.Lost, _sm.CurrentState);
        Assert.False(_sm.IsInstrumentReady);
        Assert.False(_sm.HasValidLease(2700)); // Lease revoked immediately!
        Assert.Single(_lost);
    }

    [Fact]
    public void ProbingTimeoutWithoutDetections_TransitionsToLost()
    {
        _sm.TriggerProbe("User clicked play", 0);
        Assert.Equal(InstrumentLifecycleState.Probing, _sm.CurrentState);

        // Report failures within timeout
        _sm.ReportDetectionResult(new InstrumentDetectionResult { State = InstrumentDetectionState.None }, 500);
        Assert.Equal(InstrumentLifecycleState.Probing, _sm.CurrentState);

        // Report failure after ProbingTimeout (2000ms)
        _sm.ReportDetectionResult(new InstrumentDetectionResult { State = InstrumentDetectionState.None }, 2600);
        Assert.Equal(InstrumentLifecycleState.Lost, _sm.CurrentState);
        Assert.Single(_lost);
    }

    [Fact]
    public void OverlayClosed_ReturnsToIdle()
    {
        _sm.TriggerProbe("Open", 0);
        _sm.ReportDetectionResult(new InstrumentDetectionResult { InstrumentId = "guitar", DisplayName = "吉他", State = InstrumentDetectionState.Confirmed }, 100);
        _sm.ReportDetectionResult(new InstrumentDetectionResult { InstrumentId = "guitar", DisplayName = "吉他", State = InstrumentDetectionState.Confirmed }, 300);
        Assert.Equal(InstrumentLifecycleState.Confirmed, _sm.CurrentState);


        _sm.NotifyOverlayClosed();
        Assert.Equal(InstrumentLifecycleState.Idle, _sm.CurrentState);
        Assert.False(_sm.IsInstrumentReady);
    }
}
