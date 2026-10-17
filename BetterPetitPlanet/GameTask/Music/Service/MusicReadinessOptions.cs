using System;

namespace BetterPetitPlanet.GameTask.Music.Service;

public sealed record MusicReadinessOptions
{
    public TimeSpan ScreenshotMaxAge { get; init; } = TimeSpan.FromMilliseconds(2000);
    public TimeSpan DetectionMaxAge { get; init; } = TimeSpan.FromMilliseconds(2000);
    public TimeSpan HysteresisDuration { get; init; } = TimeSpan.FromMilliseconds(600);
    public int ReadySuccessCount { get; init; } = 2;
    public int ReadyFailureCount { get; init; } = 3;
    public int SnapshotIntervalMs { get; init; } = 50;
    public int WindowPollIntervalMs { get; init; } = 100;

    public int CaptureFreshnessMs => (int)ScreenshotMaxAge.TotalMilliseconds;
    public int DetectionFreshnessMs => (int)DetectionMaxAge.TotalMilliseconds;
    public int HysteresisDurationMs => (int)HysteresisDuration.TotalMilliseconds;

    public void Validate()
    {
        if (ScreenshotMaxAge <= TimeSpan.Zero || ScreenshotMaxAge > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(ScreenshotMaxAge));
        if (DetectionMaxAge <= TimeSpan.Zero || DetectionMaxAge > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(DetectionMaxAge));
        if (HysteresisDuration < TimeSpan.Zero || HysteresisDuration > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(HysteresisDuration));
        if (ReadySuccessCount < 1 || ReadySuccessCount > 20)
            throw new ArgumentOutOfRangeException(nameof(ReadySuccessCount));
        if (ReadyFailureCount < 1 || ReadyFailureCount > 20)
            throw new ArgumentOutOfRangeException(nameof(ReadyFailureCount));
        if (SnapshotIntervalMs < 10 || SnapshotIntervalMs > 1000)
            throw new ArgumentOutOfRangeException(nameof(SnapshotIntervalMs));
        if (WindowPollIntervalMs < 10 || WindowPollIntervalMs > 1000)
            throw new ArgumentOutOfRangeException(nameof(WindowPollIntervalMs));
    }
}
