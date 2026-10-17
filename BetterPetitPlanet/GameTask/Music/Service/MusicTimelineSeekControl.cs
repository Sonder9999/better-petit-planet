using System;
using System.Threading.Tasks;

namespace BetterPetitPlanet.GameTask.Music.Service;

public static class MusicTimelineSeekControl
{
    public static async Task SeekAsync(MusicPlaybackService playback, int noteIndex, Func<Task> activateAndStart)
    {
        if (playback.CurrentTimeline == null) return;
        // Freeze/release before moving the clock; only the guarded start may resume it.
        playback.Pause();
        playback.SeekNote(noteIndex);
        await activateAndStart();
    }

    public static async Task SeekMsAsync(MusicPlaybackService playback, int positionMs, Func<Task> activateAndStart)
    {
        if (playback.CurrentTimeline == null) return;
        playback.Pause();
        playback.Seek(positionMs);
        await activateAndStart();
    }
}
