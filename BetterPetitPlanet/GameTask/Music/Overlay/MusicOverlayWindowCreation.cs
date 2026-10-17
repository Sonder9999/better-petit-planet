using System;
using System.Threading.Tasks;

namespace BetterPetitPlanet.GameTask.Music.Overlay;

public sealed class MusicOverlayWindowCreation(Func<Task> create)
{
    private Task? _pending;
    public bool IsPending => _pending is { IsCompleted: false };

    public Task EnsureAsync()
    {
        if (IsPending) return _pending!;
        return _pending = create();
    }
}
