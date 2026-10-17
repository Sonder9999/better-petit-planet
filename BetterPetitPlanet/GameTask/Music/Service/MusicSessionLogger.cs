using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BetterPetitPlanet.GameTask.Music.Model;
using Microsoft.Extensions.Logging;

namespace BetterPetitPlanet.GameTask.Music.Service;

/// <summary>One sink consumer, bounded ordinary messages, fixed slots for critical diagnostics.</summary>
internal sealed class MusicSessionLogger : ILogger<MusicPlaybackService>, IDisposable
{
    public const int SchedulingEvent = 1;
    public const int ReleaseEventBase = 2;
    private sealed record CriticalLog(Action Write, LogLevel Level, long Occurrences);
    private readonly object _gate = new();
    private readonly ILogger<MusicPlaybackService> _sink;
    private readonly int _capacity;
    private readonly Queue<Action> _ordinary = new();
    // Slot zero is notification failure, slot one scheduling failure, then one per lane.
    private readonly CriticalLog?[] _critical = new CriticalLog?[ReleaseEventBase + Enum.GetValues<MusicLane>().Length];
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(1);
    private Task? _worker;
    private bool _finishWhenEmpty;
    private long _droppedCount;
    private long _sinkFailureCount;
    public long DroppedCount => Interlocked.Read(ref _droppedCount);
    public long SinkFailureCount => Interlocked.Read(ref _sinkFailureCount);
    public Task Completion { get { lock (_gate) return _worker ?? Task.CompletedTask; } }

    public MusicSessionLogger(ILogger<MusicPlaybackService> sink, int capacity)
    {
        if (capacity is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(capacity));
        _sink = sink;
        _capacity = capacity;
        _worker = Task.Run(ConsumeAsync);
    }

    // Never call provider code, including IsEnabled/BeginScope, on the producer thread.
    public bool IsEnabled(LogLevel logLevel) => true;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Action write = () =>
        {
            if (_sink.IsEnabled(level)) _sink.Log(level, eventId, state, exception, formatter);
        };
        lock (_gate)
        {
            if (level >= LogLevel.Error)
            {
                int slot = (uint)eventId.Id < _critical.Length ? eventId.Id : 0;
                _critical[slot] = new(write, level, (_critical[slot]?.Occurrences ?? 0) + 1);
            }
            else
            {
                if (_ordinary.Count == _capacity) { _ordinary.Dequeue(); Interlocked.Increment(ref _droppedCount); }
                _ordinary.Enqueue(write);
            }
            // Post-disposal explicit Stop recovery may still need to report an owned key.
            _worker ??= Task.Run(ConsumeAsync);
            _wake.Writer.TryWrite(true);
        }
    }

    private async Task ConsumeAsync()
    {
        await foreach (var _ in _wake.Reader.ReadAllAsync())
        {
            while (true)
            {
                Action? write;
                lock (_gate)
                {
                    write = TakeNext();
                    if (write == null && _finishWhenEmpty) { _worker = null; return; }
                }
                if (write == null) break;
                try { write(); }
                catch (Exception) { Interlocked.Increment(ref _sinkFailureCount); }
            }
        }
    }

    private Action? TakeNext()
    {
        for (int i = 0; i < _critical.Length; i++)
        {
            if (_critical[i] is not { } entry) continue;
            _critical[i] = null;
            int eventId = i;
            return () =>
            {
                entry.Write();
                if (entry.Occurrences > 1)
                    _sink.Log(entry.Level, new EventId(eventId),
                        "Music critical diagnostics coalesced {Occurrences} occurrences for event {EventId}",
                        entry.Occurrences, eventId);
            };
        }
        return _ordinary.TryDequeue(out var next) ? next : null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _finishWhenEmpty = true;
            _wake.Writer.TryWrite(true);
        }
    }
}
