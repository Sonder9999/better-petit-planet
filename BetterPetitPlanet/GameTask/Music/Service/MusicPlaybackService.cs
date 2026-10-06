using System;
using System.Diagnostics;
using System.Linq;
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
    private double _initialStartMs;
    private volatile bool _seekOccurred;
    private long _startTimestamp;

    public PlayerState State => _state;
    public PerformanceTimeline? CurrentTimeline => _currentTimeline;
    public double PlaybackSpeed => _playbackSpeed;

    public event Action<PlayerState>? StateChanged;
    public event Action<double, double>? PositionChanged;
    public event Action<int, int, double, double>? NotePositionChanged;
    public event Action<PerformanceEvent, int, int>? NotePlayed;
    public event Action? PlaybackCompleted;

    public event Action? NextTrackRequested;
    public event Action? PreviousTrackRequested;

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

    public void LoadTimeline(PerformanceTimeline timeline)
    {
        Stop();
        lock (_stateLock)
        {
            if (timeline.Events.Count > 0 && timeline.Events[0].TimeMs > 0)
            {
                int leadIn = timeline.Events[0].TimeMs;
                foreach (var ev in timeline.Events)
                {
                    ev.TimeMs -= leadIn;
                }
                foreach (var sec in timeline.Sections)
                {
                    sec.TimeMs = Math.Max(0, sec.TimeMs - leadIn);
                }
                timeline.TotalDurationMs = timeline.Events.Max(e => e.TimeMs + e.DurationMs);
            }

            if (timeline.Sections.Count == 0 && timeline.Events.Count > 0)
            {
                timeline.Sections = PetitScoreParser.ExtractSections(timeline.Events, timeline.TotalDurationMs);
            }

            _currentTimeline = timeline;
            _currentEventIndex = 0;
            _pausedElapsedMs = 0;
            _initialStartMs = 0;
            _seekOccurred = false;
            SetState(PlayerState.Idle);
            NotifyPosition(0, timeline.Events.Count, 0, timeline.TotalDurationMs / 1000.0);
            _logger?.LogInformation("Loaded score: {Title} ({Events} notes)", timeline.Metadata.Title, timeline.Events.Count);
        }
    }

    public void Play()
    {
        lock (_stateLock)
        {
            if (_currentTimeline == null || _currentTimeline.Events.Count == 0) return;
            if (_state == PlayerState.Playing) return;

            if (_configService.Config.Music.AutoFocusGameOnPlayback)
            {
                _processDetector.ActivateGameWindow();
            }

            if (_state == PlayerState.Paused)
            {
                _startTimestamp = Stopwatch.GetTimestamp() - (long)(_pausedElapsedMs * Stopwatch.Frequency / 1000.0);
                SetState(PlayerState.Playing);
                _logger?.LogInformation("Playback resumed at {Ms} ms", _pausedElapsedMs);
                return;
            }

            _cts = new CancellationTokenSource();
            _seekOccurred = false;

            if (_initialStartMs > 0 || _currentEventIndex > 0)
            {
                double startMs = _currentTimeline.Events[_currentEventIndex].TimeMs;
                _pausedElapsedMs = startMs / _playbackSpeed;
                _startTimestamp = Stopwatch.GetTimestamp() - (long)(_pausedElapsedMs * Stopwatch.Frequency / 1000.0);
                _initialStartMs = 0;
            }
            else
            {
                _currentEventIndex = 0;
                _pausedElapsedMs = 0;
                _startTimestamp = Stopwatch.GetTimestamp();
            }

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
            _initialStartMs = 0;
            _seekOccurred = false;
            SetState(PlayerState.Stopped);
            _logger?.LogInformation("Playback stopped");
        }
    }

    public void SeekNote(int noteIndex)
    {
        lock (_stateLock)
        {
            if (_currentTimeline == null || _currentTimeline.Events.Count == 0) return;

            int clampedIndex = Math.Clamp(noteIndex, 0, _currentTimeline.Events.Count - 1);
            _currentEventIndex = clampedIndex;
            var targetEvent = _currentTimeline.Events[clampedIndex];
            double targetMs = targetEvent.TimeMs;

            if (_state == PlayerState.Playing)
            {
                _startTimestamp = Stopwatch.GetTimestamp() - (long)(targetMs / _playbackSpeed * Stopwatch.Frequency / 1000.0);
                _seekOccurred = true;
            }
            else if (_state == PlayerState.Paused)
            {
                _pausedElapsedMs = targetMs / _playbackSpeed;
            }
            else
            {
                _initialStartMs = targetMs;
            }

            NotifyPosition(clampedIndex, _currentTimeline.Events.Count, targetMs / 1000.0, _currentTimeline.TotalDurationMs / 1000.0);
            _logger?.LogInformation("Playback seeked to note #{Idx}/{Total} (Time {Sec:F2}s)", clampedIndex, _currentTimeline.Events.Count, targetMs / 1000.0);
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
            SeekNote(newIndex);
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

    private void NotifyPosition(int currentNote, int totalNotes, double currentSec, double totalSec)
    {
        PositionChanged?.Invoke(currentSec, totalSec);
        NotePositionChanged?.Invoke(currentNote, totalNotes, currentSec, totalSec);
    }

    private async Task PlaybackLoop(CancellationToken token)
    {
        var timeline = _currentTimeline;
        if (timeline == null) return;

        var events = timeline.Events;
        int totalEvents = events.Count;

        long lastInstrumentCheckTs = Stopwatch.GetTimestamp();
        long lastPositionNotifyTs = Stopwatch.GetTimestamp();

        while (!token.IsCancellationRequested && _currentEventIndex < totalEvents)
        {
            while (_state == PlayerState.Paused && !token.IsCancellationRequested)
            {
                await Task.Delay(20, token);
            }
            if (token.IsCancellationRequested || _state != PlayerState.Playing) break;

            var hwnd = _processDetector.FindMainWindowHandle();

            // 1. 严格前台焦点硬守护：当且仅当前台窗口是游戏窗口时才演奏，失焦时安全挂起等待
            if (_configService.Config.Music.InputMode == MusicInputMode.Keyboard && !_processDetector.IsGameForeground())
            {
                _logger?.LogInformation("游戏失去前台焦点，演奏已自动挂起等待...");
                while (!token.IsCancellationRequested && _state == PlayerState.Playing && !_processDetector.IsGameForeground())
                {
                    if (_seekOccurred)
                    {
                        break;
                    }
                    await Task.Delay(50, token);
                }
                if (token.IsCancellationRequested || _state != PlayerState.Playing) break;

                // 重新聚焦后，重新同步 _startTimestamp，使音符不发生抢弹或错乱
                if (_currentEventIndex < totalEvents)
                {
                    double curTargetMs = events[_currentEventIndex].TimeMs / _playbackSpeed;
                    _startTimestamp = Stopwatch.GetTimestamp() - (long)(curTargetMs * Stopwatch.Frequency / 1000.0);
                    _logger?.LogInformation("游戏重新获得前台焦点，继续演奏音符 #{Idx}。", _currentEventIndex);
                }
            }

            // 2. 周期性检查游戏内乐器演奏界面（每 1200ms 检测一次）
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

            var nextEvent = events[_currentEventIndex];
            double targetTimeMs = nextEvent.TimeMs / _playbackSpeed;

            bool wasSeeked = false;
            // High precision wait
            while (!token.IsCancellationRequested && _state == PlayerState.Playing)
            {
                if (_seekOccurred)
                {
                    _seekOccurred = false;
                    wasSeeked = true;
                    break;
                }

                // 等待期间失焦保护：跳出循环并进入外层挂起等待
                if (_configService.Config.Music.InputMode == MusicInputMode.Keyboard && !_processDetector.IsGameForeground())
                {
                    break;
                }

                long now = Stopwatch.GetTimestamp();
                double elapsedMs = (double)(now - _startTimestamp) * 1000 / Stopwatch.Frequency;
                double remainMs = targetTimeMs - elapsedMs;

                // 节流分发进度变更通知（约每 40ms 一次，25fps）
                if ((now - lastPositionNotifyTs) * 1000.0 / Stopwatch.Frequency >= 40)
                {
                    lastPositionNotifyTs = now;
                    NotifyPosition(_currentEventIndex, totalEvents, elapsedMs / 1000.0, timeline.TotalDurationMs / 1000.0);
                }

                if (remainMs <= 1.0) break;
                if (remainMs > 15.0)
                {
                    int delayMs = Math.Min((int)remainMs - 10, 30);
                    await Task.Delay(delayMs, token);
                }
                else
                {
                    Thread.SpinWait(100);
                }
            }

            if (wasSeeked)
            {
                continue;
            }

            if (token.IsCancellationRequested || _state != PlayerState.Playing) break;

            // 如果当前失焦，不发键并重新循环等待
            if (_configService.Config.Music.InputMode == MusicInputMode.Keyboard && !_processDetector.IsGameForeground())
            {
                continue;
            }

            // 发射按键
            _transports.SendKey(nextEvent.Key, nextEvent.DurationMs, hwnd, _configService.Config.Music.InputMode);
            NotePlayed?.Invoke(nextEvent, _currentEventIndex + 1, totalEvents);

            _currentEventIndex++;
            NotifyPosition(_currentEventIndex, totalEvents, targetTimeMs / 1000.0, timeline.TotalDurationMs / 1000.0);
        }

        if (!token.IsCancellationRequested && _currentEventIndex >= totalEvents)
        {
            _logger?.LogInformation("Song finished: {Title}", timeline.Metadata.Title);
            _initialStartMs = 0;
            _currentEventIndex = 0;
            _pausedElapsedMs = 0;
            _seekOccurred = false;
            SetState(PlayerState.Idle);
            NotifyPosition(0, totalEvents, 0, timeline.TotalDurationMs / 1000.0);
            PlaybackCompleted?.Invoke();
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
