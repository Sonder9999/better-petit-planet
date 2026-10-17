using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterPetitPlanet.GameTask.Music.Service;

public sealed class MusicTimelineStartControl(MusicPlaybackService playback,
    Func<CancellationToken, Task<bool>> waitUntilReady, Action unavailable, CancellationToken lifetime = default)
{
    // Commands and the final Play run on the host dispatcher. Each await rechecks
    // request identity and the session revision before it can advance the request.
    private CancellationTokenSource? _pending;

    public bool CancelPending()
    {
        var pending = _pending;
        _pending = null;
        if (pending == null) return false;
        pending.Cancel();
        return true;
    }

    public async Task ToggleAsync(Func<Task> prepare)
    {
        if (CancelPending()) return;
        if (playback.State == PlayerState.Playing)
        {
            playback.Pause();
            return;
        }
        await StartAsync(prepare);
    }

    public async Task StartAsync(Func<Task> prepare)
    {
        CancelPending();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _pending = request;
        var started = playback.CaptureSnapshot();

        bool IsCurrent()
        {
            var current = playback.CaptureSnapshot();
            return _pending == request && !request.IsCancellationRequested &&
                current.SessionId == started.SessionId && current.Revision == started.Revision;
        }

        try
        {
            request.Token.ThrowIfCancellationRequested();
            await prepare().WaitAsync(request.Token);
            if (!IsCurrent()) return;

            bool ready = await waitUntilReady(request.Token).WaitAsync(request.Token);
            if (!IsCurrent()) return;

            if (ready) playback.Play();
            else unavailable();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        finally
        {
            if (_pending == request) _pending = null;
        }
    }

    public async Task ToggleAutomaticAsync(Func<Task> enableAutomatic)
    {
        if (CancelPending()) return;
        if (playback.State == PlayerState.Playing)
        {
            if (playback.AutomaticPlaybackEnabled) playback.Pause();
            else await enableAutomatic();
            return;
        }
        await StartAsync(enableAutomatic);
    }
}
