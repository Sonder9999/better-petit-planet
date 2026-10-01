using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask.Music.Model;
using Microsoft.Extensions.Logging;

namespace BetterPetitPlanet.GameTask.Music.Service;

public enum PlayerState
{
    Idle,
    Playing,
    Paused,
    Stopped
}

public sealed class MusicPlaybackService : IDisposable
{
    private readonly ILogger<MusicPlaybackService>? _logger;
    private readonly IConfigService _configService;
    private readonly GameProcessDetector _processDetector;
    private readonly KeyInputTransports _transports;

    private PerformanceTimeline? _currentTimeline;
    private CancellationTokenSource? _cts;
    private Task? _playbackTask;
    private readonly object _stateLock = new();

    private PlayerState _state = PlayerState.Idle;
    private double _playbackSpeed = 1.0;
    private int _currentEventIndex;
    private double _pausedElapsedMs;
    private long _startTimestamp;
    private bool _hasGameBeenFocused;

    public PlayerState State => _state;
    public PerformanceTimeline? CurrentTimeline => _currentTimeline;
    public double PlaybackSpeed => _playbackSpeed;

    public event Action<PlayerState>? StateChanged;
    public event Action<double, double>? PositionChanged;
    public event Action<PerformanceEvent, int, int>? NotePlayed;
    public event Action? PlaybackCompleted;

    public event Action? NextTrackRequested;
    public event Action? PreviousTrackRequested;
    public event Action? SwitchInstrumentRequested;

    private readonly InstrumentDetector? _instrumentDetector;

    public MusicPlaybackService(
        IConfigService configService,
        GameProcessDetector processDetector,
        KeyInputTransports transports,
        InstrumentDetector? instrumentDetector = null,
        ILogger<MusicPlaybackService>? logger = null)
    {
        _configService = configService;
        _processDetector = processDetector;
        _transports = transports;
        _instrumentDetector = instrumentDetector;
        _logger = logger;
        _playbackSpeed = _configService.Config.Music.PlaybackSpeed;
    }

    public void RequestNextTrack() => NextTrackRequested?.Invoke();
    public void RequestPreviousTrack() => PreviousTrackRequested?.Invoke();
    public void RequestSwitchInstrument() => SwitchInstrumentRequested?.Invoke();

    public void LoadTimeline(PerformanceTimeline timeline)
    {
        Stop();
        lock (_stateLock)
        {
            _currentTimeline = timeline;
            _currentEventIndex = 0;
            _pausedElapsedMs = 0;
            SetState(PlayerState.Idle);
            PositionChanged?.Invoke(0, timeline.TotalDurationMs / 1000.0);
            _logger?.LogInformation("Loaded score: {Title} ({Events} notes)", timeline.Metadata.Title, timeline.Events.Count);
        }
    }

    public void Play()
    {
        lock (_stateLock)
        {
            if (_currentTimeline == null || _currentTimeline.Events.Count == 0) return;
            if (_state == PlayerState.Playing) return;

            // Activate game window if present
            _processDetector.ActivateGameWindow();
            _hasGameBeenFocused = false;

            if (_state == PlayerState.Paused)
            {
                _startTimestamp = Stopwatch.GetTimestamp() - (long)(_pausedElapsedMs * Stopwatch.Frequency / 1000.0);
                SetState(PlayerState.Playing);
                _logger?.LogInformation("Playback resumed at {Ms} ms", _pausedElapsedMs);
                return;
            }

            _cts = new CancellationTokenSource();
            _currentEventIndex = 0;
            _pausedElapsedMs = 0;
            _startTimestamp = Stopwatch.GetTimestamp();
            SetState(PlayerState.Playing);

            _playbackTask = Task.Run(() => PlaybackLoop(_cts.Token));
            _logger?.LogInformation("Playback started for {Title}", _currentTimeline.Metadata.Title);
        }
    }

    public void Pause()
    {
        lock (_stateLock)
        {
            if (_state != PlayerState.Playing) return;
            long now = Stopwatch.GetTimestamp();
            _pausedElapsedMs = (double)(now - _startTimestamp) * 1000 / Stopwatch.Frequency;
            SetState(PlayerState.Paused);
            _logger?.LogInformation("Playback paused at {Ms} ms", _pausedElapsedMs);
        }
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            if (_state == PlayerState.Stopped || _state == PlayerState.Idle) return;
            _cts?.Cancel();
            _currentEventIndex = 0;
            _pausedElapsedMs = 0;
            SetState(PlayerState.Stopped);
            _logger?.LogInformation("Playback stopped");
        }
    }

    public void Seek(double targetSeconds)
    {
        lock (_stateLock)
        {
            if (_currentTimeline == null || _currentTimeline.Events.Count == 0) return;

            double targetMs = Math.Clamp(targetSeconds * 1000.0, 0, _currentTimeline.TotalDurationMs);
            var events = _currentTimeline.Events;

            int newIndex = 0;
            while (newIndex < events.Count && events[newIndex].TimeMs < targetMs)
            {
                newIndex++;
            }
            _currentEventIndex = newIndex;

            if (_state == PlayerState.Playing)
            {
                _startTimestamp = Stopwatch.GetTimestamp() - (long)(targetMs / _playbackSpeed * Stopwatch.Frequency / 1000.0);
            }
            else
            {
                _pausedElapsedMs = targetMs / _playbackSpeed;
            }

            PositionChanged?.Invoke(targetMs / 1000.0, _currentTimeline.TotalDurationMs / 1000.0);
            _logger?.LogInformation("Playback seeked to {Sec:F2}s (event {Idx}/{Total})", targetSeconds, newIndex, events.Count);
        }
    }

    public void TogglePlayPause()
    {
        if (_state == PlayerState.Playing) Pause();
        else Play();
    }

    public void SetSpeed(double speed)
    {
        _playbackSpeed = Math.Clamp(speed, 0.2, 5.0);
        _configService.Config.Music.PlaybackSpeed = _playbackSpeed;
        _configService.Save();
    }

    private void SetState(PlayerState newState)
    {
        _state = newState;
        StateChanged?.Invoke(newState);
    }

    private async Task PlaybackLoop(CancellationToken token)
    {
        var timeline = _currentTimeline;
        if (timeline == null) return;

        var events = timeline.Events;
        int totalEvents = events.Count;

        long lastInstrumentCheckTs = Stopwatch.GetTimestamp();
        while (!token.IsCancellationRequested && _currentEventIndex < totalEvents)
        {
            while (_state == PlayerState.Paused && !token.IsCancellationRequested)
            {
                await Task.Delay(20, token);
            }
            if (token.IsCancellationRequested || _state != PlayerState.Playing) break;

            var hwnd = _processDetector.FindMainWindowHandle();

            // 周期性检查游戏内乐器演奏界面（每 1200ms 检测一次）
            long nowTs = Stopwatch.GetTimestamp();
            if ((nowTs - lastInstrumentCheckTs) * 1000.0 / Stopwatch.Frequency >= 1200)
            {
                lastInstrumentCheckTs = nowTs;
                if (_instrumentDetector != null && _instrumentDetector.IsTemplateLoaded && hwnd != IntPtr.Zero)
                {
                    if (!_instrumentDetector.CheckGameWindow(hwnd, out _))
                    {
                        _logger?.LogInformation("检测到游戏内竖笛界面已关闭，自动暂停演奏。");
                        Pause();
                        continue;
                    }
                }
            }

            // Check foreground if configured: only pause after the game has gained focus at least once
            if (_configService.Config.Music.AutoPauseOnFocusLost
                && _configService.Config.Music.InputMode == MusicInputMode.Keyboard)
            {
                bool isGameFg = _processDetector.IsGameForeground();
                if (isGameFg)
                {
                    _hasGameBeenFocused = true;
                }
                else if (_hasGameBeenFocused && _currentEventIndex > 2)
                {
                    _logger?.LogInformation("Game window lost focus, auto-pausing playback");
                    Pause();
                    continue;
                }
            }

            var nextEvent = events[_currentEventIndex];
            double targetTimeMs = nextEvent.TimeMs / _playbackSpeed;

            // High precision wait
            while (!token.IsCancellationRequested && _state == PlayerState.Playing)
            {
                long now = Stopwatch.GetTimestamp();
                double elapsedMs = (double)(now - _startTimestamp) * 1000 / Stopwatch.Frequency;
                double remainMs = targetTimeMs - elapsedMs;

                PositionChanged?.Invoke(elapsedMs / 1000.0, timeline.TotalDurationMs / 1000.0);

                if (remainMs <= 1.0) break;
                if (remainMs > 15.0) await Task.Delay((int)remainMs - 10, token);
                else Thread.SpinWait(100);
            }

            if (token.IsCancellationRequested || _state != PlayerState.Playing) break;

            // Inject keystroke
            _transports.SendKey(nextEvent.Key, nextEvent.DurationMs, hwnd, _configService.Config.Music.InputMode);
            NotePlayed?.Invoke(nextEvent, _currentEventIndex + 1, totalEvents);

            _currentEventIndex++;
        }

        if (!token.IsCancellationRequested && _currentEventIndex >= totalEvents)
        {
            _logger?.LogInformation("Song finished: {Title}", timeline.Metadata.Title);
            SetState(PlayerState.Idle);
            PlaybackCompleted?.Invoke();
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
