using System;
using BetterPetitPlanet.GameTask.Music.Rendering;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask.AutoPick;
using BetterPetitPlanet.GameTask.Music.Model;
using Microsoft.Extensions.Logging;

namespace BetterPetitPlanet.GameTask.Music.Service;

public sealed class MusicSessionCoordinator : IDisposable
{
    private readonly IConfigService _config;
    private readonly MusicPlaybackService _playback;
    private readonly InstrumentDetector _detector;
    private readonly GameProcessDetector _process;
    private readonly GameTaskManager _capture;
    private readonly AutoPickTrigger _pickup;
    private readonly MusicCaptureObserver _frames;
    private readonly ILogger<MusicSessionCoordinator>? _logger;
    private readonly MusicInputObserver _input;
    private readonly MusicReadinessGate _gate;
    private readonly MusicReadinessCoordinator _safety;
    private readonly MusicReadinessOptions _options;
    private readonly MusicTimelineStartControl _starts;
    private readonly Dispatcher? _dispatcher;
    private readonly DispatcherTimer? _timer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<bool> _isGameRunning;
    private readonly Func<bool> _isGameForeground;
    private readonly Func<bool> _isCaptureRunning;
    private readonly Func<double> _clockNow;

    private bool _disposed;
    private bool _started;
    private bool _adjusting;
    private MusicSessionSnapshot? _lastNotified;

    public bool Ready => _safety.Ready;
    public bool Adjusting => _adjusting;
    public string Status { get; private set; } = "等待竖笛";
    public MusicPlaybackService Playback => _playback;
    public MusicInputObserver InputObserver => _input;
    public MusicReadinessGate ReadinessGate => _gate;
    public MusicReadinessOptions ReadinessOptions => _options;
    public MusicTimelineStartControl StartControl => _starts;

    public event Action? Changed;
    public event Action<MusicInputEvent>? InputObserved;
    public event Action<MusicSessionSnapshot>? SessionChanged;

    public MusicSessionCoordinator(
        IConfigService config,
        MusicPlaybackService playback,
        InstrumentDetector detector,
        GameProcessDetector process,
        GameTaskManager capture,
        AutoPickTrigger pickup,
        MusicCaptureObserver frames,
        ILogger<MusicSessionCoordinator>? logger = null,
        MusicReadinessOptions? options = null,
        MusicInputObserver? inputObserver = null,
        Func<bool>? isGameRunning = null,
        Func<bool>? isGameForeground = null,
        Func<bool>? isCaptureRunning = null,
        Func<double>? clockNow = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _pickup = pickup ?? throw new ArgumentNullException(nameof(pickup));
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _logger = logger;
        _options = options ?? new MusicReadinessOptions();
        _options.Validate();

        _isGameRunning = isGameRunning ?? _process.IsGameRunningCached;
        _isGameForeground = isGameForeground ?? _process.IsGameForegroundCached;
        _isCaptureRunning = isCaptureRunning ?? (() => _capture.IsRunning);
        _clockNow = clockNow ?? (() => MusicCaptureObserver.MonotonicMs);

        _input = inputObserver ?? new MusicInputObserver();
        _gate = new MusicReadinessGate(_options);
        _safety = new MusicReadinessCoordinator(_playback.SetReadiness, _input.Clear);

        HardwareInputSimulator.PhysicalKeyGuard = (scancode, vk) =>
        {
            if (vk >= 'A' && vk <= 'Z')
            {
                string key = ((char)vk).ToString();
                if (_input.IsPhysicalKeyDown(key)) return true;
            }
            return false;
        };

        _starts = new MusicTimelineStartControl(_playback, WaitForReadinessAsync, () =>
        {
            Status = "无法开始：" + Status;
            Changed?.Invoke();
        }, _lifetime.Token);

        _dispatcher = Dispatcher.FromThread(Thread.CurrentThread) ?? Dispatcher.CurrentDispatcher;
        if (_dispatcher != null && !_dispatcher.HasShutdownStarted)
        {
            int interval = Math.Min(_options.WindowPollIntervalMs, _options.SnapshotIntervalMs);
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(interval), DispatcherPriority.Normal, (_, _) => Tick(), _dispatcher);
        }
    }

    public void Start()
    {
        if (_disposed || _started) return;
        _started = true;

        _playback.SetReadiness(false, "等待新鲜截图与竖笛检测");
        _playback.ConfigurePreparation(_config.Config.Music.PreparationMs);
        _playback.SetAutomaticPlayback(_config.Config.Music.AutomaticPlaybackEnabled);

        _detector.DetectionUpdated += OnDetection;
        _frames.FrameObserved += OnFrame;
        _playback.InputChanged += OnAutomaticInput;
        _playback.SessionChanged += OnSession;
        _playback.PlaybackError += OnError;
        _capture.StateChanged += OnCaptureState;

        _input.CanObserve = () => Ready && !Adjusting && _isGameForeground();
        _input.InputObserved += OnManualInput;
        _input.ObservationFailed += ex => _logger?.LogWarning(ex, "Manual keyboard observation failed");

        try
        {
            _input.Start();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Manual music keyboard hook unavailable");
            OnError("手动键盘监听不可用");
        }

        _timer?.Start();
        Tick();
    }

    public void StopTimeline()
    {
        CancelTimelineStart();
        _playback.Stop();
    }

    public void PauseTimeline()
    {
        CancelTimelineStart();
        _playback.Pause();
    }

    public bool CancelTimelineStart() => _starts.CancelPending();

    public Task StartTimelineAsync() => _disposed ? Task.CompletedTask : _starts.StartAsync(PrepareSessionAsync);

    public Task ToggleTimelineAsync(Func<Task>? prepare = null) =>
        _disposed ? Task.CompletedTask : _starts.ToggleAsync(prepare ?? PrepareSessionAsync);

    public Task ToggleAutomaticAsync() => _disposed ? Task.CompletedTask : _starts.ToggleAutomaticAsync(async () =>
    {
        _config.Config.Music.AutomaticPlaybackEnabled = true;
        await PrepareSessionAsync();
    });

    public Task SeekNoteAsync(int noteIndex) =>
        _disposed ? Task.CompletedTask : MusicTimelineSeekControl.SeekAsync(_playback, noteIndex, () => StartTimelineAsync());

    public Task SeekMsAsync(int positionMs) =>
        _disposed ? Task.CompletedTask : MusicTimelineSeekControl.SeekMsAsync(_playback, positionMs, () => StartTimelineAsync());

    public async Task PrepareSessionAsync()
    {
        if (_disposed) return;
        if (_config.Config.Music.PreparationMs < 0 || _config.Config.Music.PreparationMs > 10000)
            throw new ArgumentException("准备时长必须为 0–10000 毫秒");

        _playback.SetAutomaticPlayback(_config.Config.Music.AutomaticPlaybackEnabled);
        _playback.ConfigurePreparation(_config.Config.Music.PreparationMs);
        await _config.SaveAsync();
        Tick();
        Changed?.Invoke();
    }

    public void LoadSong(PetitSongScore score)
    {
        var events = ScoreNormalizer.Normalize(score);
        int totalDuration = events.Count > 0 ? events.Max(e => checked(e.TimeMs + e.DurationMs)) : 0;
        LoadTimeline(new PerformanceTimeline
        {
            Metadata = score.Metadata,
            Events = events,
            TotalDurationMs = totalDuration
        });
    }

    public void LoadTimeline(PerformanceTimeline timeline)
    {
        CancelTimelineStart();
        _playback.Stop();
        _playback.LoadTimeline(timeline);
        Changed?.Invoke();
    }

    public void SetAdjusting(bool adjusting)
    {
        if (_adjusting == adjusting) return;
        _adjusting = adjusting;
        if (adjusting)
        {
            CancelTimelineStart();
            Suspend("调整布局");
            _playback.Pause();
        }
        Tick();
    }

    public async Task<bool> WaitForReadinessAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!_isCaptureRunning()) _capture.Start();
        if (_config.Config.Music.AutoFocusGameOnPlayback && !_isGameForeground())
        {
            _process.ActivateGameWindow();
        }
        Tick();

        double deadline = _clockNow() + _options.DetectionFreshnessMs * 2;
        while (!Ready && _clockNow() < deadline)
        {
            cancellation.ThrowIfCancellationRequested();
            Tick();
            await Task.Delay(_options.SnapshotIntervalMs, cancellation);
        }

        cancellation.ThrowIfCancellationRequested();
        return !_disposed && Ready && _isGameForeground();
    }

    public void Tick()
    {
        using var diagnostic = MusicOverlayDiagnostics.Measure(MusicOverlayDiagnostics.Metric.CoordinatorExecuteMs);
        if (_disposed) return;
        try
        {
            bool exists = _isGameRunning();
            bool foreground = exists && _isGameForeground();
            bool captureRunning = _isCaptureRunning();

            var readiness = _gate.Evaluate(_clockNow(), exists, foreground, captureRunning);
            _safety.Apply(readiness, Adjusting);
            _pickup.MusicSuppressed = _gate.StableInstrument || _playback.State is PlayerState.Playing or PlayerState.Paused;

            string status = readiness.Reason;
            if (Status != status)
            {
                Status = status;
                Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Music session update failed");
            Suspend("会话状态异常");
            Status = "会话状态异常";
            Changed?.Invoke();
        }
    }

    private void Post(Action action, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        if (_disposed) return;
        if (_dispatcher == null || _dispatcher.CheckAccess())
        {
            using var operation = new MusicOverlayDiagnostics.UiOperation(MusicOverlayDiagnostics.Metric.CoordinatorActionMs, "Coordinator", caller, 0);
            action();
            return;
        }

        if (!_dispatcher.HasShutdownStarted)
        {
            long diagnosticPosted = MusicOverlayDiagnostics.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            _dispatcher.BeginInvoke(() =>
            {
                using var operation = new MusicOverlayDiagnostics.UiOperation(MusicOverlayDiagnostics.Metric.CoordinatorActionMs, "Coordinator", caller, diagnosticPosted);
                if (diagnosticPosted != 0) MusicOverlayDiagnostics.Record(MusicOverlayDiagnostics.Metric.CoordinatorQueueMs,
                    (System.Diagnostics.Stopwatch.GetTimestamp() - diagnosticPosted) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                if (!_disposed) action();
            });
        }
    }

    private void OnCaptureState(bool running) => Post(() =>
    {
        if (!running)
        {
            _gate.Invalidate();
            Suspend("截图已停止");
        }
    });

    private void OnFrame(double now)
    {
        Post(() => _gate.ObserveCapture(now));
    }

    private void OnDetection(InstrumentDetectionResult result)
    {
        double received = _clockNow();
        Post(() => _gate.ObserveDetection(result.IsDetected, received, result.StatusSummary.Contains("异常")));
    }

    private void OnSession(MusicSessionSnapshot snapshot) => Post(() =>
    {
        var previous = _lastNotified;
        _lastNotified = snapshot;
        SessionChanged?.Invoke(snapshot);

        if (previous == null || previous.SessionId != snapshot.SessionId || previous.State != snapshot.State ||
            previous.Revision != snapshot.Revision || previous.PlaybackSpeed != snapshot.PlaybackSpeed ||
            previous.AutomaticPlaybackEnabled != snapshot.AutomaticPlaybackEnabled)
        {
            Changed?.Invoke();
        }
    });

    private void OnAutomaticInput(MusicInputEvent input) => Post(() => InputObserved?.Invoke(input));

    private void OnManualInput(string lane, string action, double now)
    {
        var snapshot = _playback.CaptureSnapshot();
        var input = new MusicInputEvent(lane, action, "manual", snapshot.PositionMs, now, null, snapshot.Revision);
        Post(() => InputObserved?.Invoke(input));
    }

    private void OnError(string error) => Post(() =>
    {
        Status = error;
        _logger?.LogWarning("Music session status: {Reason}", error);
        Changed?.Invoke();
    });

    public void Suspend(string reason) => _safety.Suspend(reason);

    public void Dispose()
    {
        if (_disposed) return;
        CancelTimelineStart();
        Suspend("应用退出");

        HardwareInputSimulator.PhysicalKeyGuard = null;
        HardwareInputSimulator.ReleaseAllHeldKeys();

        _disposed = true;
        _lifetime.Cancel();
        _timer?.Stop();
        _input.Dispose();

        _detector.DetectionUpdated -= OnDetection;
        _frames.FrameObserved -= OnFrame;
        _playback.InputChanged -= OnAutomaticInput;
        _playback.SessionChanged -= OnSession;
        _playback.PlaybackError -= OnError;
        _capture.StateChanged -= OnCaptureState;

        _pickup.MusicSuppressed = false;
    }
}
