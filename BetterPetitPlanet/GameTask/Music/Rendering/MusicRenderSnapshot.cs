using System;
using System.Collections.Generic;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

/// <summary>
/// Immutable, read-only snapshot of current music session display state for native rendering.
/// </summary>
public sealed record MusicRenderSnapshot(
    string SessionId,
    long Revision,
    double PositionMs,
    double HostMonotonicMs,
    long MonotonicTimestamp,
    double PlaybackSpeed,
    bool IsPlaying,
    bool IsPreparing,
    int NoteCount,
    IReadOnlyList<string> ActiveAutomaticKeys,
    IReadOnlyList<string> ActiveManualKeys,
    IReadOnlyList<string> ActiveKeys)
{
    public int TotalNotes => NoteCount;

    public static MusicRenderSnapshot Empty { get; } = new(
        SessionId: string.Empty,
        Revision: 0,
        PositionMs: 0,
        HostMonotonicMs: 0,
        MonotonicTimestamp: 0,
        PlaybackSpeed: 1.0,
        IsPlaying: false,
        IsPreparing: false,
        NoteCount: 0,
        ActiveAutomaticKeys: Array.Empty<string>(),
        ActiveManualKeys: Array.Empty<string>(),
        ActiveKeys: Array.Empty<string>()
    );

    /// <summary>
    /// Computes deterministic display time in milliseconds at given monotonic time.
    /// Extrapolates based on elapsed monotonic clock and playback speed only when playing or preparing.
    /// Stays fixed when paused or stopped.
    /// Positive visualOffsetMs advances display time (forward in song progress).
    /// </summary>
    public double ComputeCurrentDisplayTimeMs(double currentMonotonicMs, int visualOffsetMs)
    {
        double pos;
        if (IsPlaying || IsPreparing)
        {
            double elapsed = (currentMonotonicMs - HostMonotonicMs) * PlaybackSpeed;
            pos = PositionMs + elapsed;
        }
        else
        {
            pos = PositionMs;
        }

        return pos + visualOffsetMs;
    }

    /// <summary>
    /// Overload accepting long currentMonotonicMs.
    /// </summary>
    public double ComputeCurrentDisplayTimeMs(long currentMonotonicMs, int visualOffsetMs)
        => ComputeCurrentDisplayTimeMs((double)currentMonotonicMs, visualOffsetMs);
}
