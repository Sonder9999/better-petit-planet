using System;

namespace BetterPetitPlanet.GameTask.Music.Service;

/// <summary>Host maps these defaults from persisted music configuration.</summary>
public sealed record MusicPlaybackOptions
{
    public int SchedulerIntervalMs { get; init; } = 5;
    public int SnapshotIntervalMs { get; init; } = 40;
    public int ReleaseRetryIntervalMs { get; init; } = 10;
    public int ReleaseRetryAttempts { get; init; } = 3;
    public int NotificationCapacity { get; init; } = 256;
    public void Validate()
    {
        Check(SchedulerIntervalMs, 1, 1000, nameof(SchedulerIntervalMs));
        Check(SnapshotIntervalMs, 1, 10000, nameof(SnapshotIntervalMs));
        Check(ReleaseRetryIntervalMs, 1, 1000, nameof(ReleaseRetryIntervalMs));
        Check(ReleaseRetryAttempts, 1, 100, nameof(ReleaseRetryAttempts));
        Check(NotificationCapacity, 1, 100000, nameof(NotificationCapacity));
    }

    private static void Check(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum) throw new ArgumentOutOfRangeException(name);
    }
}
