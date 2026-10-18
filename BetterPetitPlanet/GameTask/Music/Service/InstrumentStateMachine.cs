using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BetterPetitPlanet.GameTask.Music.Service;

public enum InstrumentLifecycleState
{
    /// <summary>
    /// 非演奏页面或无演奏需求，休眠或保持低频探测（默认 10s），避免无谓的 CPU/GPU 占用。
    /// </summary>
    Idle,

    /// <summary>
    /// 浮窗激活、切换曲谱、点击播放或按下快捷键时，即时发起高频定向探测（默认 200ms）。
    /// </summary>
    Probing,

    /// <summary>
    /// 乐器已确认就绪，降低检测频率（默认 1000ms）或轻量保活。
    /// </summary>
    Confirmed,

    /// <summary>
    /// 正在播放演奏中，保持锁定，采用低开销探测（默认 800ms）保证播放连贯。
    /// </summary>
    Active,

    /// <summary>
    /// 乐器退出或连续未检出，进入丢失状态，并通知会话协调器挂起。
    /// </summary>
    Lost
}

public sealed record InstrumentStateMachineOptions
{
    public TimeSpan IdleInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ProbingInterval { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan ConfirmedInterval { get; init; } = TimeSpan.FromMilliseconds(1000);
    public TimeSpan ActiveInterval { get; init; } = TimeSpan.FromMilliseconds(800);
    public TimeSpan LostInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan ProbingTimeout { get; init; } = TimeSpan.FromSeconds(8);
    public int RequiredSuccessCount { get; init; } = 2;
    public int RequiredFailureCount { get; init; } = 3;
    public TimeSpan ActiveLeaseDuration { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// 乐器检测生命周期事件驱动状态机 (Idle -> Probing -> Confirmed -> Active -> Lost)。
/// 负责协调按需检测频率调度、乐器就绪判定与播放安全租约生命周期。
/// </summary>
public sealed class InstrumentStateMachine : IDisposable
{
    private readonly InstrumentStateMachineOptions _options;
    private readonly ILogger<InstrumentStateMachine>? _logger;
    private readonly object _stateLock = new();

    private InstrumentLifecycleState _state = InstrumentLifecycleState.Idle;
    private int _consecutiveSuccesses;
    private int _consecutiveFailures;
    private double _probingStartTimeMs;
    private double _lastProcessTimestampMs = double.NegativeInfinity;
    private bool _forceNextProbe;
    private bool _disposed;

    public InstrumentLifecycleState CurrentState
    {
        get
        {
            lock (_stateLock) return _state;
        }
    }

    public InstrumentStateMachineOptions Options => _options;
    public string? ConfirmedInstrumentId { get; private set; }
    public string? ConfirmedDisplayName { get; private set; }
    public double LeaseExpirationTimestampMs { get; private set; } = double.NegativeInfinity;

    public bool IsInstrumentReady
    {
        get
        {
            lock (_stateLock)
            {
                return _state is InstrumentLifecycleState.Confirmed or InstrumentLifecycleState.Active;
            }
        }
    }

    public TimeSpan CurrentInterval
    {
        get
        {
            lock (_stateLock)
            {
                return _state switch
                {
                    InstrumentLifecycleState.Idle => _options.IdleInterval,
                    InstrumentLifecycleState.Probing => _options.ProbingInterval,
                    InstrumentLifecycleState.Confirmed => _options.ConfirmedInterval,
                    InstrumentLifecycleState.Active => _options.ActiveInterval,
                    InstrumentLifecycleState.Lost => _options.LostInterval,
                    _ => _options.IdleInterval
                };
            }
        }
    }

    public event Action<InstrumentLifecycleState, InstrumentLifecycleState>? StateChanged;
    public event Action<string>? InstrumentConfirmed;
    public event Action<string>? InstrumentLost;

    public InstrumentStateMachine(
        InstrumentStateMachineOptions? options = null,
        ILogger<InstrumentStateMachine>? logger = null)
    {
        _options = options ?? new InstrumentStateMachineOptions();
        _logger = logger;
    }

    /// <summary>
    /// 触发定向高频探测（例如浮窗激活、切换歌曲、点击播放或按下快捷键）。
    /// </summary>
    public void TriggerProbe(string reason, double? nowMs = null)
    {
        lock (_stateLock)
        {
            if (_disposed) return;

            double now = nowMs ?? MonotonicNowMs();
            _forceNextProbe = true;
            _probingStartTimeMs = now;

            _logger?.LogDebug("TriggerProbe: reason={Reason}, currentState={State}", reason, _state);

            if (_state is InstrumentLifecycleState.Idle or InstrumentLifecycleState.Lost)
            {
                TransitionTo(InstrumentLifecycleState.Probing, reason);
            }
        }
    }

    public void NotifyPlaybackStarted(double? nowMs = null)
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            double now = nowMs ?? MonotonicNowMs();
            LeaseExpirationTimestampMs = now + _options.ActiveLeaseDuration.TotalMilliseconds;
            if (_state != InstrumentLifecycleState.Active)
            {
                TransitionTo(InstrumentLifecycleState.Active, "演奏开始");
            }
        }
    }

    public void NotifyPlaybackStopped()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            if (_state == InstrumentLifecycleState.Active)
            {
                TransitionTo(InstrumentLifecycleState.Confirmed, "演奏停止");
            }
        }
    }

    public void NotifyOverlayOpened(double? nowMs = null)
    {
        TriggerProbe("浮窗打开", nowMs);
    }

    public void NotifyOverlayClosed()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            if (_state != InstrumentLifecycleState.Active)
            {
                ConfirmedInstrumentId = null;
                ConfirmedDisplayName = null;
                LeaseExpirationTimestampMs = double.NegativeInfinity;
                TransitionTo(InstrumentLifecycleState.Idle, "浮窗关闭");
            }
        }
    }

    /// <summary>
    /// 检查当前帧是否应该由触发器执行检测（根据状态机当前频率节流控制）。
    /// </summary>
    public bool ShouldProcessCapture(double nowMs)
    {
        lock (_stateLock)
        {
            if (_disposed) return false;

            if (_forceNextProbe)
            {
                _forceNextProbe = false;
                return true;
            }

            double intervalMs = CurrentInterval.TotalMilliseconds;
            return (nowMs - _lastProcessTimestampMs) >= intervalMs;
        }
    }

    /// <summary>
    /// 由检测引擎反馈最新识别结果，驱动状态机流转。
    /// </summary>
    public void ReportDetectionResult(InstrumentDetectionResult result, double nowMs)
    {
        InstrumentLifecycleState oldState;
        InstrumentLifecycleState newState;
        bool confirmed = false;
        bool lost = false;
        string? instrumentName = null;

        lock (_stateLock)
        {
            if (_disposed) return;

            _lastProcessTimestampMs = nowMs;
            oldState = _state;

            bool isDetected = result.IsDetected || result.State == InstrumentDetectionState.Confirmed;

            if (isDetected)
            {
                _consecutiveSuccesses++;
                _consecutiveFailures = 0;
                ConfirmedInstrumentId = result.InstrumentId;
                ConfirmedDisplayName = result.DisplayName;
                instrumentName = result.DisplayName ?? result.InstrumentId ?? "乐器";


                // 刷新有效租约
                LeaseExpirationTimestampMs = nowMs + _options.ActiveLeaseDuration.TotalMilliseconds;

                if (_state is InstrumentLifecycleState.Probing or InstrumentLifecycleState.Lost or InstrumentLifecycleState.Idle)
                {
                    if (_consecutiveSuccesses >= _options.RequiredSuccessCount)
                    {
                        _state = InstrumentLifecycleState.Confirmed;
                        confirmed = true;
                    }
                }
            }
            else
            {
                _consecutiveSuccesses = 0;
                _consecutiveFailures++;

                if (_state == InstrumentLifecycleState.Probing)
                {
                    if (nowMs - _probingStartTimeMs > _options.ProbingTimeout.TotalMilliseconds)
                    {
                        _state = InstrumentLifecycleState.Lost;
                        lost = true;
                        LeaseExpirationTimestampMs = double.NegativeInfinity;
                    }
                }
                else if (_state is InstrumentLifecycleState.Confirmed or InstrumentLifecycleState.Active)
                {
                    if (_consecutiveFailures >= _options.RequiredFailureCount)
                    {
                        _state = InstrumentLifecycleState.Lost;
                        lost = true;
                        ConfirmedInstrumentId = null;
                        ConfirmedDisplayName = null;
                        LeaseExpirationTimestampMs = double.NegativeInfinity;
                    }
                }
            }

            newState = _state;
        }

        // 在锁外触发事件，防止死锁
        if (oldState != newState)
        {
            _logger?.LogInformation("InstrumentStateMachine: {OldState} -> {NewState}", oldState, newState);
            StateChanged?.Invoke(oldState, newState);
        }

        if (confirmed && instrumentName != null)
        {
            InstrumentConfirmed?.Invoke(instrumentName);
        }

        if (lost)
        {
            InstrumentLost?.Invoke("乐器未检测到或已退出");
        }
    }

    /// <summary>
    /// 判断是否拥有处于有效期内的演奏就绪租约（用于 MusicReadinessGate 平滑门控）。
    /// </summary>
    public bool HasValidLease(double nowMs)
    {
        lock (_stateLock)
        {
            if (_disposed || _state == InstrumentLifecycleState.Lost)
            {
                return false;
            }
            return (_state is InstrumentLifecycleState.Confirmed or InstrumentLifecycleState.Active)
                   && nowMs <= LeaseExpirationTimestampMs;
        }
    }

    public void Reset()
    {
        lock (_stateLock)
        {
            _consecutiveSuccesses = 0;
            _consecutiveFailures = 0;
            _forceNextProbe = false;
            ConfirmedInstrumentId = null;
            ConfirmedDisplayName = null;
            LeaseExpirationTimestampMs = double.NegativeInfinity;
            TransitionTo(InstrumentLifecycleState.Idle, "重置");
        }
    }

    private void TransitionTo(InstrumentLifecycleState targetState, string reason)
    {
        if (_state == targetState) return;
        var old = _state;
        _state = targetState;
        _consecutiveSuccesses = 0;
        _consecutiveFailures = 0;
        _logger?.LogInformation("InstrumentStateMachine: {OldState} -> {NewState} (reason: {Reason})", old, targetState, reason);
        StateChanged?.Invoke(old, targetState);
    }

    private static double MonotonicNowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            LeaseExpirationTimestampMs = double.NegativeInfinity;
            _state = InstrumentLifecycleState.Idle;
        }
    }
}
