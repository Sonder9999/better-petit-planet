using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BetterPetitPlanet.GameTask.Music.Model;

namespace BetterPetitPlanet.GameTask.Music.Rendering;

/// <summary>
/// Strictly isolated synthetic preview data source for the in-app settings dialog.
/// Generates a cyclic demonstration chart showcasing 7-lane vertical flow, tap notes,
/// long holds, and multi-lane chords.
///
/// STRICT PHYSICAL ISOLATION GUARANTEES:
/// 1. Holds no reference to IMusicInputTransport or KeyInputTransports.
/// 2. Never calls Windows SendInput, keybd_event, or low-level keyboard drivers.
/// 3. Never installs or listens to keyboard hooks.
/// 4. Never touches or activates game or host windows.
/// 5. Operates purely in-memory using an independent monotonic loop clock.
/// </summary>
public sealed class MusicPreviewSource : IDisposable
{
    private readonly object _lock = new();
    private readonly Func<double> _monotonicClock;
    private readonly string _sessionId;

    private bool _isPlaying;
    private bool _disposed;
    private double _anchorMonotonicMs;
    private double _positionOffsetMs;
    private long _revision;

    public PerformanceTimeline Timeline { get; }
    public MusicChartIndex ChartIndex { get; }
    public double LoopDurationMs { get; }
    public double PlaybackSpeed { get; set; } = 1.0;
    public string SessionId => _sessionId;
    public bool IsPlaying { get { lock (_lock) return _isPlaying; } }

    public MusicPreviewSource(Func<double>? monotonicClock = null, double loopDurationMs = 8000.0)
    {
        _monotonicClock = monotonicClock ?? (() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        _sessionId = $"preview-{Guid.NewGuid():N}";
        LoopDurationMs = loopDurationMs > 1000.0 ? loopDurationMs : 8000.0;
        _positionOffsetMs = 0.0;

        Timeline = CreateSyntheticDemoTimeline((int)LoopDurationMs);
        ChartIndex = new MusicChartIndex(Timeline);
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || _isPlaying) return;
            _anchorMonotonicMs = _monotonicClock();
            _isPlaying = true;
            _revision++;
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (!_isPlaying) return;
            _positionOffsetMs = GetCurrentPositionInternal();
            _isPlaying = false;
            _revision++;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _positionOffsetMs = 0;
            _anchorMonotonicMs = _monotonicClock();
            _revision++;
        }
    }

    public void Seek(double positionMs)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _positionOffsetMs = positionMs % LoopDurationMs;
            if (_positionOffsetMs < 0) _positionOffsetMs += LoopDurationMs;
            _anchorMonotonicMs = _monotonicClock();
            _revision++;
        }
    }

    public double GetCurrentPositionMs()
    {
        lock (_lock)
        {
            return GetCurrentPositionInternal();
        }
    }

    private double GetCurrentPositionInternal()
    {
        if (!_isPlaying) return _positionOffsetMs % LoopDurationMs;

        double now = _monotonicClock();
        double elapsed = (now - _anchorMonotonicMs) * Math.Clamp(PlaybackSpeed, 0.2, 5.0);
        double pos = (_positionOffsetMs + elapsed) % LoopDurationMs;
        if (pos < 0) pos += LoopDurationMs;
        return pos;
    }

    /// <summary>
    /// Captures a thread-safe, strictly synthetic MusicRenderSnapshot.
    /// Simulates authentic hit bursts and hold glows for notes currently crossing the judgement line,
    /// purely as visual state without any real keyboard actuation.
    /// </summary>
    public MusicRenderSnapshot CaptureSnapshot(double? customMonotonicMs = null)
    {
        lock (_lock)
        {
            double now = customMonotonicMs ?? _monotonicClock();
            double pos = GetCurrentPositionInternal();

            // Simulate active visual feedback for notes currently crossing the judgement line
            var simulatedActiveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < Timeline.Events.Count; i++)
            {
                var note = Timeline.Events[i];
                int duration = MusicChartIndex.GetNoteDuration(note);

                if (duration > 0)
                {
                    // Hold note active while pos is within [note.TimeMs, note.TimeMs + duration]
                    if (pos >= note.TimeMs && pos <= note.TimeMs + duration)
                    {
                        simulatedActiveKeys.Add(note.Key);
                    }
                }
                else
                {
                    // Tap note brief hit pulse starting upon reaching judgement line
                    if (pos >= note.TimeMs && pos <= note.TimeMs + 120.0)
                    {
                        simulatedActiveKeys.Add(note.Key);
                    }
                }
            }

            var activeList = simulatedActiveKeys
                .OrderBy(k => MusicPlayfieldGeometry.GetLaneIndex(k))
                .ToList();

            return new MusicRenderSnapshot(
                SessionId: _sessionId,
                Revision: _revision,
                PositionMs: pos,
                HostMonotonicMs: now,
                MonotonicTimestamp: (long)now,
                PlaybackSpeed: PlaybackSpeed,
                IsPlaying: _isPlaying,
                IsPreparing: false,
                NoteCount: Timeline.Events.Count,
                ActiveAutomaticKeys: activeList,
                ActiveManualKeys: Array.Empty<string>(),
                ActiveKeys: activeList);
        }
    }

    /// <summary>
    /// Checks whether a given lane key is in continuous holding state at the given preview position.
    /// </summary>
    public bool IsLaneHolding(string key, double? customPos = null)
    {
        double pos = customPos ?? GetCurrentPositionMs();
        for (int i = 0; i < Timeline.Events.Count; i++)
        {
            var note = Timeline.Events[i];
            int duration = MusicChartIndex.GetNoteDuration(note);
            if (duration > 0 && string.Equals(note.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                if (pos >= note.TimeMs && pos <= note.TimeMs + duration)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Gets continuous holding duration (ageMs) for a lane key at the given preview position.
    /// </summary>
    public double GetLaneHoldAgeMs(string key, double? customPos = null)
    {
        double pos = customPos ?? GetCurrentPositionMs();
        for (int i = 0; i < Timeline.Events.Count; i++)
        {
            var note = Timeline.Events[i];
            int duration = MusicChartIndex.GetNoteDuration(note);
            if (duration > 0 && string.Equals(note.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                if (pos >= note.TimeMs && pos <= note.TimeMs + duration)
                {
                    return pos - note.TimeMs;
                }
            }
        }
        return 0.0;
    }

    /// <summary>
    /// Gets elapsed hit burst animation time for a tap note at the given preview position.
    /// Returns -1 when outside the 64-frame animation window.
    /// </summary>
    public double GetHitBurstElapsedMs(string key, double? customPos = null)
    {
        double pos = customPos ?? GetCurrentPositionMs();
        for (int i = 0; i < Timeline.Events.Count; i++)
        {
            var note = Timeline.Events[i];
            int duration = MusicChartIndex.GetNoteDuration(note);
            if (duration == 0 && string.Equals(note.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                if (pos >= note.TimeMs && pos <= note.TimeMs + HitBurstEffectRenderer.TotalDurationMs)
                {
                    return pos - note.TimeMs;
                }
            }
        }
        return -1.0;
    }

    /// <summary>
    /// Synthesizes a clean 7-track demonstration chart matching music-overlay-integration exactly:
    /// - Opening chord on A, S, D at 3000ms (A hold, S tap, D hold, connected by ribbon)
    /// - Clean ascending staircase ladder on F (tap 4700), J (hold 5100), K (tap 5500), L (hold 5900)
    /// - Loops smoothly across 9000ms duration.
    /// </summary>
    public static PerformanceTimeline CreateSyntheticDemoTimeline(int durationMs = 9000)
    {
        var events = new List<PerformanceEvent>
        {
            // 1. Classical Opening: Dual Hold on A and D with S tap note chord connection
            new() { NoteId = "preview-0", Key = "A", TimeMs = 3000, Kind = MusicNoteKind.Hold, DurationMs = 1600, SyncGroupId = "preview-chord" },
            new() { NoteId = "preview-1", Key = "S", TimeMs = 3000, Kind = MusicNoteKind.Tap, DurationMs = 45, SyncGroupId = "preview-chord" },
            new() { NoteId = "preview-2", Key = "D", TimeMs = 3000, Kind = MusicNoteKind.Hold, DurationMs = 1600, SyncGroupId = "preview-chord" },

            // 2. Ascending ladder notes
            new() { NoteId = "preview-3", Key = "F", TimeMs = 4700, Kind = MusicNoteKind.Tap, DurationMs = 45 },
            new() { NoteId = "preview-4", Key = "J", TimeMs = 5100, Kind = MusicNoteKind.Hold, DurationMs = 1600 },
            new() { NoteId = "preview-5", Key = "K", TimeMs = 5500, Kind = MusicNoteKind.Tap, DurationMs = 45 },
            new() { NoteId = "preview-6", Key = "L", TimeMs = 5900, Kind = MusicNoteKind.Hold, DurationMs = 1600 },
        };

        return new PerformanceTimeline
        {
            Metadata = new PetitSongMetadata
            {
                Title = "七键 / 长按 / 同时按键预览",
                Artist = "静思浮梦 · 纳西妲",
                Bpm = 120.0,
                Instrument = "recorder",
                Key = "C"
            },
            Events = events.OrderBy(e => e.TimeMs).ToList(),
            TotalDurationMs = durationMs,
            FilePath = "synthetic://nahida-preview-demo.json"
        };
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            Pause();
            _disposed = true;
        }
    }
}
