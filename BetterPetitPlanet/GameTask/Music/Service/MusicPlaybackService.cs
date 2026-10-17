using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask.Music.Model;
using Microsoft.Extensions.Logging;

namespace BetterPetitPlanet.GameTask.Music.Service;

public enum PlayerState { Idle, Playing, Paused, Stopped }

public sealed partial class MusicPlaybackService : IDisposable
{
    private readonly object _stateLock = new();
    private readonly MusicSessionLogger? _logger;
    private readonly IConfigService _configService;
    private readonly GameProcessDetector _processDetector;
    private readonly MusicSessionClock _clock;
    private readonly MusicNoteScheduler _scheduler;
    private readonly MusicNotificationQueue _notifications;
    private readonly MusicPlaybackOptions _options;
    private readonly MusicGameTarget? _gameTarget;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _playbackTask;
    private PerformanceTimeline? _currentTimeline;
    private PlayerState _state;
    private bool _automatic = true;
    private bool _ready = true;
    private bool _mouseCompatible = true;
    private bool _positionSelected;
    private bool _disposed;
    private int _preparationMs = 3000;
    private long _revision;
    private IntPtr _target;
    private string _sessionId = Guid.NewGuid().ToString("N");

    public PlayerState State { get { lock (_stateLock) return _state; } }
    public PerformanceTimeline? CurrentTimeline { get { lock (_stateLock) return _currentTimeline; } }
    public double PlaybackSpeed { get { lock (_stateLock) return _clock.Speed; } }
    public double CurrentPositionMs { get { lock (_stateLock) return _clock.PositionMs; } }
    public string SessionId { get { lock (_stateLock) return _sessionId; } }
    public bool AutomaticPlaybackEnabled { get { lock (_stateLock) return _automatic; } }
    public bool IsReady { get { lock (_stateLock) return _ready; } }
    public string ReadinessReason { get; private set; } = "就绪";
    public string? LastInputError { get; private set; }
    public event Action<PlayerState>? StateChanged;
    public event Action<double, double>? PositionChanged;
    public event Action<int, int, double, double>? NotePositionChanged;
    public event Action<PerformanceEvent, int, int>? NotePlayed;
    public event Action? PlaybackCompleted;
    public event Action? NextTrackRequested;
    public event Action? PreviousTrackRequested;
    public event Action<MusicSessionSnapshot>? SessionChanged;
    public event Action<MusicInputEvent>? InputChanged;
    public event Action<string>? PlaybackError;

    public MusicPlaybackService(IConfigService configService, GameProcessDetector processDetector,
        KeyInputTransports transports, InstrumentDetector? instrumentDetector = null,
        ILogger<MusicPlaybackService>? logger = null, MusicSessionClock? clock = null,
        IMusicInputTransport? inputTransport = null, MusicPlaybackOptions? options = null)
    {
        _configService = configService;
        _processDetector = processDetector;
        _options = options ?? new MusicPlaybackOptions();
        _options.Validate();
        _logger = logger == null ? null : new MusicSessionLogger(logger, _options.NotificationCapacity);
        _clock = clock ?? new MusicSessionClock();
        _clock.SetSpeed(double.IsFinite(configService.Config.Music.PlaybackSpeed)
            ? Math.Clamp(configService.Config.Music.PlaybackSpeed, 0.2, 5) : 1);
        _notifications = new MusicNotificationQueue(ex => _logger?.LogError(ex, "Music notification handler failed"),
            _options.NotificationCapacity, OnInputNotificationOverflow);
        _scheduler = new MusicNoteScheduler(inputTransport ?? transports, _options, () => _clock.HostMonotonicMs);
        _scheduler.Submitted += OnSubmitted;
        _scheduler.Failed += ReportError;
        _scheduler.ReleaseExhausted += key => _logger?.LogError(
            new EventId(MusicSessionLogger.ReleaseEventBase + (int)Enum.Parse<MusicLane>(key)),
            "Music release retries exhausted after {Attempts} attempts; session {SessionId}; key {Key}",
            _options.ReleaseRetryAttempts, _sessionId, key);
        // A supplied transport owns its environment guard; production uses cached window identity.
        if (inputTransport == null) _gameTarget = new MusicGameTarget(processDetector);
    }

    public void RequestNextTrack() => NextTrackRequested?.Invoke();
    public void RequestPreviousTrack() => PreviousTrackRequested?.Invoke();

    public void ConfigurePreparation(int preparationMs)
    {
        if (preparationMs < 0 || preparationMs > 10000) throw new ArgumentOutOfRangeException(nameof(preparationMs));
        lock (_stateLock) _preparationMs = preparationMs;
    }

    public void LoadTimeline(PerformanceTimeline timeline)
    {
        var normalized = ScoreNormalizer.CopyTimeline(timeline);
        lock (_stateLock)
        {
            StopCore(restartExhausted: false);
            if (_scheduler.ActiveKeys.Length > 0) { ReportError("按键释放失败，请稍后重试加载曲谱。"); return; }
            _currentTimeline = normalized;
            _mouseCompatible = !normalized.Events.Any(n => n.Kind == MusicNoteKind.Hold) &&
                normalized.Events.GroupBy(n => n.TimeMs).All(group => group.Count() == 1);
            _scheduler.Load(normalized.Events);
            _sessionId = Guid.NewGuid().ToString("N");
            _revision++;
            _clock.Seek(0);
            _positionSelected = false;
            SetState(PlayerState.Idle);
            Publish();
            _logger?.LogInformation("Loaded score {Title} with {Count} notes", normalized.Metadata.Title, normalized.Events.Count);
        }
    }

    public void Play()
    {
        lock (_stateLock)
        {
            if (_disposed || !_ready || _state == PlayerState.Playing || _currentTimeline?.Events.Count is not > 0) return;
            if (_automatic && !CanAutomaticallyPlay()) return;
            if (_configService.Config.Music.AutoFocusGameOnPlayback && _gameTarget != null && !_gameTarget.IsForeground)
                _processDetector.ActivateGameWindow();
            if (_state != PlayerState.Paused && !_positionSelected)
            {
                _clock.Seek(-_preparationMs);
                _scheduler.Seek(-_preparationMs, _target, _configService.Config.Music.InputMode);
            }
            _positionSelected = false;
            _clock.Start();
            SetState(PlayerState.Playing);
            Tick();
            EnsureWorker();
            Publish();
        }
    }

    public void StartFollowAlong(int preparationMs = 3000)
    {
        ConfigurePreparation(preparationMs);
        SetAutomaticPlayback(false);
        Play();
    }

    public void Pause()
    {
        lock (_stateLock)
        {
            if (_state != PlayerState.Playing) return;
            _clock.Pause();
            _scheduler.ReleaseAll(_target, _configService.Config.Music.InputMode);
            HardwareInputSimulator.ReleaseAllHeldKeys();
            SetState(PlayerState.Paused);
            _revision++;
            Publish();
        }
    }

    public void Stop() => StopCore(restartExhausted: true);

    private void StopCore(bool restartExhausted)
    {
        lock (_stateLock)
        {
            _clock.Pause();
            _scheduler.Seek(0, _target, _configService.Config.Music.InputMode, restartExhausted);
            HardwareInputSimulator.ReleaseAllHeldKeys();
            _clock.Seek(0);
            _positionSelected = false;
            _revision++;
            SetState(PlayerState.Stopped);
            Publish();
            if (_scheduler.HasPendingReleases) EnsureWorker();
        }
    }

    public void TogglePlayPause() { if (State == PlayerState.Playing) Pause(); else Play(); }

    public void SetAutomaticPlayback(bool enabled)
    {
        lock (_stateLock)
        {
            if (_automatic == enabled) return;
            if (enabled && !CanAutomaticallyPlay()) return;
            _scheduler.ReleaseAll(_target, _configService.Config.Music.InputMode);
            HardwareInputSimulator.ReleaseAllHeldKeys();
            // An off/on transition never catches up notes already passed while manual.
            if (enabled && _clock.IsRunning) _scheduler.SkipThrough(_clock.PositionMs);
            _automatic = enabled;
            Publish();
        }
    }

    public void SetReadiness(bool ready, string reason)
    {
        lock (_stateLock)
        {
            if (_ready == ready && ReadinessReason == reason) return;
            _ready = ready;
            ReadinessReason = reason;
            if (!ready)
            {
                if (_state == PlayerState.Playing) Pause();
                else
                {
                    _scheduler.ReleaseAll(_target, _configService.Config.Music.InputMode);
                    HardwareInputSimulator.ReleaseAllHeldKeys();
                }
            }
            Publish();
        }
    }

    public void SetSpeed(double speed)
    {
        if (!double.IsFinite(speed)) throw new ArgumentOutOfRangeException(nameof(speed));
        lock (_stateLock)
        {
            _clock.SetSpeed(Math.Clamp(speed, 0.2, 5));
            _configService.Config.Music.PlaybackSpeed = _clock.Speed;
            Publish();
        }
        _notifications.Post(_configService.Save);
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            StopCore(restartExhausted: false);
            HardwareInputSimulator.ReleaseAllHeldKeys();
            _disposed = true;
            _lifetime.Cancel();
            // Failed releases are retained and retried by the worker's finalizer.
            if (_playbackTask == null) { _notifications.Dispose(); _logger?.Dispose(); _lifetime.Dispose(); }
        }
    }
}
