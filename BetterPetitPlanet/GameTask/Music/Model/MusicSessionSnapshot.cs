namespace BetterPetitPlanet.GameTask.Music.Model;

public sealed record MusicSessionSnapshot(
    string SessionId, string State, double PositionMs, double DurationMs,
    double PlaybackSpeed, double HostMonotonicMs,
    bool AutomaticPlaybackEnabled, long Revision, string[] ActiveKeys);
