using System;

namespace BetterPetitPlanet.Core.Config;

public sealed class MusicPlaybackTimingConfig
{
    public int SchedulerIntervalMs { get; set; } = 5;
    public int SnapshotIntervalMs { get; set; } = 40;
    public int ReleaseRetryIntervalMs { get; set; } = 10;
    public int ReleaseRetryAttempts { get; set; } = 3;
    public int NotificationCapacity { get; set; } = 256;

    public void Validate()
    {
        Check(SchedulerIntervalMs, 1, 1000);
        Check(SnapshotIntervalMs, 1, 10000);
        Check(ReleaseRetryIntervalMs, 1, 1000);
        Check(ReleaseRetryAttempts, 1, 100);
        Check(NotificationCapacity, 1, 100000);
    }

    private static void Check(int value, int min, int max)
    {
        if (value < min || value > max) throw new ArgumentOutOfRangeException(nameof(value));
    }
}
