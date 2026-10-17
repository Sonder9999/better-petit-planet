using System;
using BetterPetitPlanet.GameTask.Music.Rendering;
using System.Threading;
using System.Threading.Tasks;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Model;
using Microsoft.Extensions.Logging;

namespace BetterPetitPlanet.GameTask.Music.Service;

public sealed partial class MusicPlaybackService
{
    public void SeekNote(int noteIndex)
    {
        lock (_stateLock)
        {
            if (_currentTimeline?.Events.Count is not > 0) return;
            int index = Math.Clamp(noteIndex, 0, _currentTimeline.Events.Count - 1);
            SeekPosition(_currentTimeline.Events[index].TimeMs);
        }
    }

    public void Seek(double targetSeconds)
    {
        if (!double.IsFinite(targetSeconds)) throw new ArgumentOutOfRangeException(nameof(targetSeconds));
        lock (_stateLock)
        {
            if (_currentTimeline?.Events.Count is not > 0) return;
            // Preserve legacy beyond-end seek to the final note; in-range seeks are exact.
            double position = targetSeconds * 1000;
            if (position >= _currentTimeline.TotalDurationMs) position = _currentTimeline.Events[^1].TimeMs;
            SeekPosition(Math.Max(0, position));
        }
    }

    private void SeekPosition(double position)
    {
        _scheduler.Seek(position, _target, _configService.Config.Music.InputMode);
        _clock.Seek(position);
        _positionSelected = true;
        _revision++;
        Publish();
    }

    public MusicSessionSnapshot CaptureSnapshot()
    {
        lock (_stateLock)
        {
            double position = _clock.PositionMs;
            var state = _state == PlayerState.Playing && position < 0 ? "preparing" : _state.ToString().ToLowerInvariant();
            return new(_sessionId, state, position, _currentTimeline?.TotalDurationMs ?? 0,
                _clock.Speed, _clock.HostMonotonicMs, _automatic, _revision, _scheduler.ActiveKeys);
        }
    }

    private void Tick()
    {
        if (_state != PlayerState.Playing)
        {
            _scheduler.RetryPendingReleases();
            return;
        }
        if (_gameTarget != null)
        {
            _target = _gameTarget.CurrentHandle;
            if (!_gameTarget.IsForeground)
            {
                SetReadiness(false, "游戏失去前台焦点");
                return;
            }
        }
        if (!_ready || _currentTimeline == null) return;
        if (_automatic && !CanAutomaticallyPlay()) { Pause(); return; }
        double position = _clock.PositionMs;
        _scheduler.Advance(position, _automatic, _target, _configService.Config.Music.InputMode);
        if (position < _currentTimeline.TotalDurationMs || _scheduler.ActiveKeys.Length != 0) return;
        _clock.Pause();
        _clock.Seek(0);
        _scheduler.Seek(0, _target, _configService.Config.Music.InputMode);
        _positionSelected = false;
        _revision++;
        SetState(PlayerState.Idle);
        Publish();
        _notifications.Post(() => PlaybackCompleted?.Invoke());
    }

    private void EnsureWorker()
    {
        if (_playbackTask != null) return;
        // Explicit Stop can recover retained ownership even after disposal completed.
        var token = _disposed ? new CancellationToken(true) : _lifetime.Token;
        _playbackTask = Task.Run(() => PlaybackLoop(token));
    }

    private async Task PlaybackLoop(CancellationToken token)
    {
        double lastPublish = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                lock (_stateLock)
                {
                    Tick();
                    if (_clock.HostMonotonicMs - lastPublish >= _options.SnapshotIntervalMs)
                    {
                        lastPublish = _clock.HostMonotonicMs;
                        Publish();
                    }
                }
                await Task.Delay(_options.SchedulerIntervalMs, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger?.LogError(new EventId(MusicSessionLogger.SchedulingEvent), ex,
                "Music scheduling failed for session {SessionId}", SessionId);
            lock (_stateLock) { Pause(); ReportError("演奏调度失败，请暂停后重试。"); }
        }
        finally
        {
            lock (_stateLock) _scheduler.ReleaseAll(_target, _configService.Config.Music.InputMode, restartExhausted: false);
            while (true)
            {
                lock (_stateLock)
                {
                    if (!_scheduler.HasPendingReleases)
                    {
                        if (_disposed) { _notifications.Dispose(); _logger?.Dispose(); _lifetime.Dispose(); }
                        _playbackTask = null;
                        if (!_disposed && _state == PlayerState.Playing) EnsureWorker();
                        break;
                    }
                    _scheduler.RetryPendingReleases();
                }
                await Task.Delay(_options.ReleaseRetryIntervalMs).ConfigureAwait(false);
            }
        }
    }

    private bool CanAutomaticallyPlay()
    {
        if (_configService.Config.Music.InputMode != MusicInputMode.Mouse || _currentTimeline == null) return true;
        if (!_mouseCompatible)
        {
            ReportError("此曲谱包含长按或同时按键，请切换到键盘输入模式。");
            return false;
        }
        return true;
    }

    private void OnSubmitted(MusicKeyCommand command)
    {
        if (MusicOverlayDiagnostics.Enabled)
        {
            double expected = command.Note.TimeMs + (command.IsDown ? 0 : command.Note.DurationMs);
            MusicOverlayDiagnostics.Record(command.IsDown ? MusicOverlayDiagnostics.Metric.InputDownLateSongMs : MusicOverlayDiagnostics.Metric.InputUpLateSongMs,
                _clock.PositionMs - expected, expected);
        }
        var input = new MusicInputEvent(command.Note.Key, command.IsDown ? "down" : "up", "automatic",
            _clock.PositionMs, _clock.HostMonotonicMs, command.Note.NoteId, _revision);
        int index = _currentTimeline?.Events.IndexOf(command.Note) + 1 ?? 0;
        int total = _currentTimeline?.Events.Count ?? 0;
        _notifications.PostInput(() =>
        {
            InputChanged?.Invoke(input);
            if (command.IsDown) NotePlayed?.Invoke(command.Note, index, total);
        });
    }

    private void SetState(PlayerState state)
    {
        if (_state == state) return;
        _state = state;
        _notifications.Post(() => StateChanged?.Invoke(state));
    }

    private void Publish()
    {
        var snapshot = CaptureSnapshot();
        int index = _scheduler.NextIndex;
        int total = _currentTimeline?.Events.Count ?? 0;
        _notifications.PostProgress(() =>
        {
            SessionChanged?.Invoke(snapshot);
            NotifyPosition(index, total, snapshot.PositionMs / 1000, snapshot.DurationMs / 1000);
        });
    }

    private void NotifyPosition(int index, int total, double currentSeconds, double totalSeconds)
    {
        PositionChanged?.Invoke(currentSeconds, totalSeconds);
        NotePositionChanged?.Invoke(index, total, currentSeconds, totalSeconds);
    }

    private void ReportError(string message)
    {
        if (LastInputError == message) return;
        LastInputError = message;
        _logger?.LogWarning("Music input failed: {Reason}; session {SessionId}", message, _sessionId);
        _notifications.Post(() => PlaybackError?.Invoke(message));
    }

    private void OnInputNotificationOverflow()
    {
        lock (_stateLock)
        {
            _revision++;
            _logger?.LogWarning("Music input observer overflow; rebuilding snapshot for session {SessionId}, revision {Revision}",
                _sessionId, _revision);
            Publish();
        }
    }
}
