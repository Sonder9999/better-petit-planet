using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("BetterPetitPlanet.Test")]

namespace BetterPetitPlanet.GameTask.Music.Service;

/// <summary>UI or bridge subscribers cannot block the input scheduling thread.</summary>
internal sealed class MusicNotificationQueue : IDisposable
{
    private readonly Channel<Action> _input;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(1);
    private readonly ConcurrentQueue<Action> _controls = new();
    private readonly Action? _onInputOverflow;
    private readonly int _capacity;
    private Action? _progress;
    private int _completed;
    public Task Completion { get; }
    public MusicNotificationQueue(Action<Exception> onError, int capacity = 256, Action? onInputOverflow = null)
    {
        if (capacity is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(capacity));
        _input = Channel.CreateBounded<Action>(capacity);
        _capacity = capacity;
        _onInputOverflow = onInputOverflow;
        Completion = Task.Run(async () =>
        {
            void Invoke(Action callback)
            {
                try { callback(); }
                catch (Exception ex) { onError(ex); }
            }
            await foreach (var _ in _wake.Reader.ReadAllAsync())
            {
                while (_controls.TryDequeue(out var control)) Invoke(control);
                for (int i = 0; i < _capacity && _input.Reader.TryRead(out var input); i++) Invoke(input);
                if (Interlocked.Exchange(ref _progress, null) is { } progress) Invoke(progress);
            }
        });
    }

    // Sparse business controls (completion, saved config, state) must remain reliable.
    public void Post(Action callback)
    {
        if (Volatile.Read(ref _completed) != 0) return;
        _controls.Enqueue(callback);
        _wake.Writer.TryWrite(true);
    }

    public void PostInput(Action callback)
    {
        if (Volatile.Read(ref _completed) != 0) return;
        if (!_input.Writer.TryWrite(callback))
        {
            // A stalled observer cannot grow memory or block the input clock. Historical
            // effects are discarded; the service advances revision and posts current state.
            while (_input.Reader.TryRead(out _)) { }
            _onInputOverflow?.Invoke();
        }
        _wake.Writer.TryWrite(true);
    }

    public void PostProgress(Action callback)
    {
        if (Volatile.Read(ref _completed) != 0) return;
        Interlocked.Exchange(ref _progress, callback);
        _wake.Writer.TryWrite(true);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        _input.Writer.TryComplete();
        _wake.Writer.TryComplete();
    }
}
