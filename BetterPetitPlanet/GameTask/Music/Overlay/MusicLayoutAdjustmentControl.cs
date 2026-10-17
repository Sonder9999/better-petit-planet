using System;
using System.Threading.Tasks;

namespace BetterPetitPlanet.GameTask.Music.Overlay;

public sealed class MusicLayoutAdjustmentControl(
    Action pauseAndRelease,
    Action activate,
    Func<Task> ensureWindow,
    Action<bool> setInteractive,
    Func<Task> save) : IDisposable
{
    private bool _disposed;
    private long _revision;
    public bool Adjusting { get; private set; }

    public async Task SetAsync(bool adjusting)
    {
        if (_disposed) return;
        long revision = ++_revision;
        pauseAndRelease();
        Adjusting = adjusting;
        if (adjusting)
        {
            activate();
            await ensureWindow();
        }
        if (_disposed || revision != _revision) return;
        setInteractive(Adjusting);
        await save();
    }

    public Task ToggleAsync() => SetAsync(!Adjusting);
    public Task CloseAsync() => SetAsync(false);

    public void Dispose()
    {
        _disposed = true;
        _revision++;
        Adjusting = false;
    }
}
