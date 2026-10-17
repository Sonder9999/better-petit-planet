using BetterPetitPlanet.GameTask.Music.Service;

namespace BetterPetitPlanet.Test;

public sealed class MusicClockTest
{
    [Fact]
    public void SpeedChange_PreservesPositionAndScalesOnlyFutureTime()
    {
        double now = 0;
        var clock = new MusicSessionClock(() => now);
        clock.Start();
        now = 1000;
        clock.SetSpeed(2);
        Assert.Equal(1000, clock.PositionMs);
        now = 1500;
        Assert.Equal(2000, clock.PositionMs);
        clock.Pause();
        now = 3000;
        Assert.Equal(2000, clock.PositionMs);
        clock.Start();
        now = 3500;
        Assert.Equal(3000, clock.PositionMs);
    }

    [Fact]
    public void PreparationAndSeek_UseChartMilliseconds()
    {
        double now = 0;
        var clock = new MusicSessionClock(() => now);
        clock.Seek(-3000);
        clock.Start();
        now = 500;
        Assert.Equal(-2500, clock.PositionMs);
        clock.Seek(1234);
        now = 600;
        Assert.Equal(1334, clock.PositionMs);
    }
}
