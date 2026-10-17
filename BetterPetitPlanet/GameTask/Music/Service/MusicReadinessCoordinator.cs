using System;

namespace BetterPetitPlanet.GameTask.Music.Service;

public sealed class MusicReadinessCoordinator
{
    private readonly Action<bool, string> _setReadiness;
    private readonly Action _clearManual;
    private readonly Action? _onUnsafe;

    public bool Ready { get; private set; }

    public MusicReadinessCoordinator(Action<bool, string> setReadiness, Action clearManual, Action? onUnsafe = null)
    {
        _setReadiness = setReadiness ?? throw new ArgumentNullException(nameof(setReadiness));
        _clearManual = clearManual ?? throw new ArgumentNullException(nameof(clearManual));
        _onUnsafe = onUnsafe;
    }

    public void Apply(MusicReadiness readiness, bool adjusting = false)
    {
        if (adjusting)
        {
            Ready = false;
            _setReadiness(false, "调整布局");
            _clearManual();
            _onUnsafe?.Invoke();
            return;
        }

        if (readiness.Ready)
        {
            Ready = true;
            _setReadiness(true, readiness.Reason);
        }
        else
        {
            Suspend(adjusting ? "调整布局" : readiness.Reason);
        }
    }

    public void Suspend(string reason)
    {
        Ready = false;
        _setReadiness(false, reason);
        _clearManual();
        _onUnsafe?.Invoke();
    }
}
