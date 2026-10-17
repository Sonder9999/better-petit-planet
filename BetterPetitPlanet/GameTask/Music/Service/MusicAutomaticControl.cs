using System;
using System.Threading.Tasks;

namespace BetterPetitPlanet.GameTask.Music.Service;

public static class MusicAutomaticControl
{
    public static async Task ToggleAsync(MusicPlaybackService playback, Func<Task> enableAutomatic, Func<Task> startTimeline)
    {
        if (playback.State == PlayerState.Playing && playback.AutomaticPlaybackEnabled)
        {
            playback.Pause();
            return;
        }

        await enableAutomatic();
        if (playback.State != PlayerState.Playing)
            await startTimeline();
    }
}
