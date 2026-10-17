namespace BetterPetitPlanet.GameTask.Music.Model;

public sealed record MusicInputEvent(
    string LaneId, string Action, string Source, double ChartTimeMs,
    double HostMonotonicMs, string? NoteId, long Revision);
