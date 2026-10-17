using System;
using BetterPetitPlanet.GameTask.Music.Rendering;
using Xunit;

namespace BetterPetitPlanet.Test;

public class MusicRenderClockTest
{
    [Fact]
    public void MonotonicClock_ExtrapolatesPositionWhenPlaying()
    {
        var snapshot = new MusicRenderSnapshot(
            SessionId: "session-1",
            Revision: 1,
            PositionMs: 5000.0,
            HostMonotonicMs: 10000.0,
            MonotonicTimestamp: 10000,
            PlaybackSpeed: 1.0,
            IsPlaying: true,
            IsPreparing: false,
            NoteCount: 100,
            ActiveAutomaticKeys: [],
            ActiveManualKeys: [],
            ActiveKeys: []
        );

        // 1. Exactly at host monotonic anchor
        Assert.Equal(5000.0, snapshot.ComputeCurrentDisplayTimeMs(10000.0, visualOffsetMs: 0));

        // 2. 500ms monotonic elapsed -> position advances by 500ms
        Assert.Equal(5500.0, snapshot.ComputeCurrentDisplayTimeMs(10500.0, visualOffsetMs: 0));

        // 3. Positive visual offset advances display time
        Assert.Equal(5650.0, snapshot.ComputeCurrentDisplayTimeMs(10500.0, visualOffsetMs: 150));

        // 4. Negative visual offset delays display time
        Assert.Equal(5350.0, snapshot.ComputeCurrentDisplayTimeMs(10500.0, visualOffsetMs: -150));
    }

    [Fact]
    public void PlaybackSpeed_ExtrapolatesProportionally()
    {
        var snapshot15x = new MusicRenderSnapshot(
            SessionId: "session-speed",
            Revision: 1,
            PositionMs: 2000.0,
            HostMonotonicMs: 10000.0,
            MonotonicTimestamp: 10000,
            PlaybackSpeed: 1.5,
            IsPlaying: true,
            IsPreparing: false,
            NoteCount: 50,
            ActiveAutomaticKeys: [],
            ActiveManualKeys: [],
            ActiveKeys: []
        );

        // 1000ms real time at 1.5x speed advances 1500ms song time: 2000 + 1500 = 3500
        Assert.Equal(3500.0, snapshot15x.ComputeCurrentDisplayTimeMs(11000.0, visualOffsetMs: 0));

        var snapshot05x = snapshot15x with { PlaybackSpeed = 0.5 };
        // 1000ms real time at 0.5x speed advances 500ms song time: 2000 + 500 = 2500
        Assert.Equal(2500.0, snapshot05x.ComputeCurrentDisplayTimeMs(11000.0, visualOffsetMs: 0));
    }

    [Fact]
    public void PausedOrStoppedState_LocksPosition()
    {
        var pausedSnapshot = new MusicRenderSnapshot(
            SessionId: "session-paused",
            Revision: 2,
            PositionMs: 4200.0,
            HostMonotonicMs: 10000.0,
            MonotonicTimestamp: 10000,
            PlaybackSpeed: 1.25,
            IsPlaying: false,
            IsPreparing: false,
            NoteCount: 80,
            ActiveAutomaticKeys: [],
            ActiveManualKeys: [],
            ActiveKeys: []
        );

        // Clock advances by 5000ms in host time, but position remains strictly locked at 4200ms
        Assert.Equal(4200.0, pausedSnapshot.ComputeCurrentDisplayTimeMs(15000.0, visualOffsetMs: 0));
        Assert.Equal(4300.0, pausedSnapshot.ComputeCurrentDisplayTimeMs(15000.0, visualOffsetMs: 100));

        // Stopped / idle state
        var stoppedSnapshot = pausedSnapshot with { PositionMs = 0 };
        Assert.Equal(0.0, stoppedSnapshot.ComputeCurrentDisplayTimeMs(99999.0, visualOffsetMs: 0));
    }

    [Fact]
    public void Countdown_NegativeTime_ExtrapolatesAcrossZero()
    {
        // 3-second preparation countdown starting at -3000ms
        var countdownSnapshot = new MusicRenderSnapshot(
            SessionId: "session-countdown",
            Revision: 1,
            PositionMs: -3000.0,
            HostMonotonicMs: 20000.0,
            MonotonicTimestamp: 20000,
            PlaybackSpeed: 1.0,
            IsPlaying: false,
            IsPreparing: true,
            NoteCount: 150,
            ActiveAutomaticKeys: [],
            ActiveManualKeys: [],
            ActiveKeys: []
        );

        // 1. Initial anchor (-3000ms)
        Assert.Equal(-3000.0, countdownSnapshot.ComputeCurrentDisplayTimeMs(20000.0, visualOffsetMs: 0));

        // 2. 1000ms elapsed -> -2000ms
        Assert.Equal(-2000.0, countdownSnapshot.ComputeCurrentDisplayTimeMs(21000.0, visualOffsetMs: 0));

        // 3. 2500ms elapsed -> -500ms
        Assert.Equal(-500.0, countdownSnapshot.ComputeCurrentDisplayTimeMs(22500.0, visualOffsetMs: 0));

        // 4. 3000ms elapsed -> reaches precisely 0ms (start of music)
        Assert.Equal(0.0, countdownSnapshot.ComputeCurrentDisplayTimeMs(23000.0, visualOffsetMs: 0));

        // 5. 3500ms elapsed -> 500ms into the song
        Assert.Equal(500.0, countdownSnapshot.ComputeCurrentDisplayTimeMs(23500.0, visualOffsetMs: 0));
    }
}
