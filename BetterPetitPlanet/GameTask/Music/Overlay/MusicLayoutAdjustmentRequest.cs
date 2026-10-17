using System;
using System.Threading.Tasks;

namespace BetterPetitPlanet.GameTask.Music.Overlay;

public sealed class MusicLayoutAdjustmentRequest(
    Func<Task> prepare,
    Func<bool, Task> setAdjusting,
    Func<bool> isAdjusting)
{
    private bool _closed;
    private bool _entering;
    private long _revision;

    public async Task<bool> ToggleAsync()
    {
        if (_closed) return false;
        bool enter = !_entering && !isAdjusting();
        long revision = ++_revision;
        _entering = enter;
        bool Current() => !_closed && revision == _revision;
        try
        {
            if (enter)
            {
                await prepare();
                if (!Current()) return false;
                await setAdjusting(true);
            }
            else if (isAdjusting())
            {
                await setAdjusting(false);
            }
            return Current();
        }
        finally
        {
            if (revision == _revision) _entering = false;
        }
    }

    public async Task CloseAsync()
    {
        _closed = true;
        _revision++;
        _entering = false;
        if (isAdjusting())
        {
            await setAdjusting(false);
        }
    }
}
