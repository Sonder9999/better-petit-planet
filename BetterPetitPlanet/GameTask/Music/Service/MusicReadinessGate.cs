using System;
using System.Linq;
using BetterPetitPlanet.GameTask.Music.Instrument;

namespace BetterPetitPlanet.GameTask.Music.Service;

public sealed record MusicReadiness(bool Ready, string Reason);

public sealed class MusicReadinessGate
{
    private readonly MusicReadinessOptions _options;
    private int _successes, _failures;
    private bool _stable;
    private double _capture = double.NegativeInfinity, _detection = double.NegativeInfinity;
    private bool _error;

    public string? TargetInstrument { get; set; }
    public string? DetectedInstrumentId { get; private set; }
    public string? DetectedDisplayName { get; private set; }

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
        DetectedInstrumentId = null;
        DetectedDisplayName = null;
        _capture = double.NegativeInfinity;
        _detection = double.NegativeInfinity;
    }

    public void ObserveCapture(double now) => _capture = now;

    public void ObserveDetection(bool detected, double now, string? instrumentId, string? displayName, bool error = false)
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
            DetectedInstrumentId = null;
            DetectedDisplayName = null;
            return;
        }

        if (detected)
        {
            if (instrumentId != null) DetectedInstrumentId = instrumentId;
            if (displayName != null) DetectedDisplayName = displayName;
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
                DetectedInstrumentId = null;
                DetectedDisplayName = null;
            }
        }
    }

    public void ObserveDetection(bool detected, double now, bool error = false)
        => ObserveDetection(detected, now, DetectedInstrumentId, DetectedDisplayName, error);

    public void ObserveDetection(InstrumentDetectionResult result, double now)
        => ObserveDetection(result.IsDetected, now, result.InstrumentId, result.DisplayName, result.StatusSummary?.Contains("异常") ?? false);

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

        if (!_stable)
        {
            string waitTarget = string.IsNullOrWhiteSpace(TargetInstrument) || IsAllInstruments(TargetInstrument)
                ? "乐器"
                : TargetInstrument;
            return new(false, $"等待{waitTarget}");
        }

        if (!string.IsNullOrWhiteSpace(TargetInstrument) && !IsAllInstruments(TargetInstrument))
        {
            if (MatchesInstrument(TargetInstrument, DetectedInstrumentId, DetectedDisplayName))
            {
                return new(true, $"{DetectedDisplayName ?? TargetInstrument}已就绪");
            }
            else
            {
                string detectedDesc = !string.IsNullOrWhiteSpace(DetectedDisplayName)
                    ? DetectedDisplayName
                    : (!string.IsNullOrWhiteSpace(DetectedInstrumentId) ? DetectedInstrumentId : "其他乐器");
                return Deny($"乐器不匹配：当前曲谱为{TargetInstrument}，画面检测为{detectedDesc}");
            }
        }
        else
        {
            return new(true, $"{DetectedDisplayName ?? "乐器"}已就绪");
        }
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

    public static bool MatchesInstrument(string target, string? detectedId, string? detectedDisplayName)
    {
        if (string.IsNullOrWhiteSpace(target) || IsAllInstruments(target))
            return true;

        if (!string.IsNullOrWhiteSpace(detectedId) && string.Equals(target.Trim(), detectedId.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(detectedDisplayName) && string.Equals(target.Trim(), detectedDisplayName.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var registry = InstrumentRegistry.Default;
            if (registry.TryGetProfile(target, out var targetProfile) && targetProfile != null)
            {
                if (!string.IsNullOrWhiteSpace(detectedId) && registry.TryGetProfile(detectedId, out var detectedProfile1))
                {
                    if (string.Equals(targetProfile.Id, detectedProfile1?.Id, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                if (!string.IsNullOrWhiteSpace(detectedDisplayName) && registry.TryGetProfile(detectedDisplayName, out var detectedProfile2))
                {
                    if (string.Equals(targetProfile.Id, detectedProfile2?.Id, StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                if (!string.IsNullOrWhiteSpace(detectedId) && targetProfile.Aliases.Any(a => string.Equals(a, detectedId.Trim(), StringComparison.OrdinalIgnoreCase)))
                    return true;

                if (!string.IsNullOrWhiteSpace(detectedDisplayName) && targetProfile.Aliases.Any(a => string.Equals(a, detectedDisplayName.Trim(), StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        catch
        {
            // Ignore registry lookup exceptions
        }

        if (IsRecorder(target) && (IsRecorder(detectedId) || IsRecorder(detectedDisplayName)))
            return true;

        if (IsGuitar(target) && (IsGuitar(detectedId) || IsGuitar(detectedDisplayName)))
            return true;

        return false;
    }

    private static bool IsAllInstruments(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        var trimmed = name.Trim();
        return string.Equals(trimmed, "全部乐器", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "全部", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "all", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "any", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRecorder(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var trimmed = name.Trim();
        return string.Equals(trimmed, "recorder", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "竖笛", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "flute", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGuitar(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var trimmed = name.Trim();
        return string.Equals(trimmed, "guitar", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "吉他", StringComparison.OrdinalIgnoreCase);
    }
}
