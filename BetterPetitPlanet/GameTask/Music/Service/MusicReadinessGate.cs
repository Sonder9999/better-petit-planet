namespace BetterPetitPlanet.GameTask.Music.Service;

public sealed record MusicReadiness(bool Ready, string Reason);

public sealed class MusicReadinessGate
{
    private readonly MusicReadinessOptions _options;
    private int _successes, _failures;
    private bool _stable;
    private double _capture = double.NegativeInfinity, _detection = double.NegativeInfinity;
    private bool _error;

    public MusicReadinessGate(MusicReadinessOptions? options = null)
    {
        _options = options ?? new MusicReadinessOptions();
        _options.Validate();
    }

    public MusicReadinessOptions Options => _options;
    public bool StableInstrument => _stable;

    public void Invalidate()
    {
        Reset();
        _capture = double.NegativeInfinity;
        _detection = double.NegativeInfinity;
    }

    public void ObserveCapture(double now) => _capture = now;

    public void ObserveDetection(bool detected, double now, bool error = false)
    {
        if (now - _detection > _options.DetectionFreshnessMs)
        {
            Reset();
        }

        _detection = now;
        _error = error;

        if (error)
        {
            Reset();
            return;
        }

        if (detected)
        {
            _failures = 0;
            if (++_successes >= _options.ReadySuccessCount)
            {
                _stable = true;
            }
        }
        else
        {
            _successes = 0;
            if (++_failures >= _options.ReadyFailureCount)
            {
                _stable = false;
            }
        }
    }

    public MusicReadiness Evaluate(double now, bool exists, bool foreground, bool captureRunning)
        => EvaluateDiagnostic(now, exists, foreground, captureRunning, false);

    // TEMPORARY: caller owns a bounded, ready-at-entry experiment lease.
    // Only detection age is exempted; no timestamp is synthesized or refreshed.
    internal MusicReadiness EvaluateDiagnostic(double now, bool exists, bool foreground, bool captureRunning, bool exemptDetectionAge)
    {
        if (!exists) return Deny("游戏未运行");
        if (!foreground) return Deny("游戏失焦或最小化");
        if (!captureRunning) return Deny("截图已停止");
        if (now - _capture > _options.CaptureFreshnessMs) return Deny("截图已过期");
        if (_error) return Deny("乐器检测异常");
        if (!exemptDetectionAge && now - _detection > _options.DetectionFreshnessMs) return Deny("检测结果已过期");
        return new(_stable, _stable ? "竖笛已就绪" : "等待竖笛");
    }

    private MusicReadiness Deny(string reason)
    {
        Reset();
        return new(false, reason);
    }

    private void Reset()
    {
        _stable = false;
        _successes = 0;
        _failures = 0;
    }
}
