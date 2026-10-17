using System;
using System.Diagnostics;

namespace BetterPetitPlanet.GameTask.Music.Service;

public sealed class MusicSessionClock
{
    private readonly Func<double> _now;
    private double _anchorMs;
    private double _positionMs;
    public bool IsRunning { get; private set; }
    public double Speed { get; private set; } = 1;
    public double HostMonotonicMs => _now();
    public double PositionMs => _positionMs + (IsRunning ? (HostMonotonicMs - _anchorMs) * Speed : 0);

    public MusicSessionClock(Func<double>? monotonicMilliseconds = null)
        => _now = monotonicMilliseconds ?? (() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

    public void Start()
    {
        if (IsRunning) return;
        _anchorMs = HostMonotonicMs;
        IsRunning = true;
    }

    public void Pause()
    {
        _positionMs = PositionMs;
        IsRunning = false;
    }

    public void Seek(double positionMs)
    {
        if (!double.IsFinite(positionMs)) throw new ArgumentOutOfRangeException(nameof(positionMs));
        _positionMs = positionMs;
        _anchorMs = HostMonotonicMs;
    }

    public void SetSpeed(double speed)
    {
        if (!double.IsFinite(speed) || speed < 0.2 || speed > 5) throw new ArgumentOutOfRangeException(nameof(speed));
        Seek(PositionMs);
        Speed = speed;
    }
}
