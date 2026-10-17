using System;
using System.Threading;

namespace BetterPetitPlanet.Core.Process;

internal sealed record GameWindowDiscoverySnapshot(int ProcessId, IntPtr MainWindowHandle)
{
    public static GameWindowDiscoverySnapshot Empty { get; } = new(0, IntPtr.Zero);

    public bool IsRunning => ProcessId > 0 || MainWindowHandle != IntPtr.Zero;
}

/// <summary>
/// Keeps window discovery off high-frequency UI polling paths. A caller can read the current
/// immutable snapshot without waiting for an in-flight scan; missed or invalid snapshots trigger
/// a rate-limited background refresh. Explicit user actions can request a synchronous refresh.
/// </summary>
internal sealed class GameWindowDiscoveryCache
{
    private sealed record State(
        long Generation,
        GameWindowDiscoverySnapshot Snapshot,
        long ActiveSynchronousRefreshGeneration);

    private readonly Func<GameWindowDiscoverySnapshot> _discover;
    private readonly Func<GameWindowDiscoverySnapshot, bool> _isSnapshotValid;
    private readonly Action<Action> _queueWork;
    private readonly Func<long> _getTimestampMs;
    private readonly long _refreshIntervalMs;

    private State _state = new(0, GameWindowDiscoverySnapshot.Empty, 0);
    private long _nextRefreshAtMs;
    private int _refreshInProgress;

    internal GameWindowDiscoveryCache(
        Func<GameWindowDiscoverySnapshot> discover,
        Func<GameWindowDiscoverySnapshot, bool> isSnapshotValid,
        Action<Action> queueWork,
        Func<long> getTimestampMs,
        long refreshIntervalMs)
    {
        _discover = discover ?? throw new ArgumentNullException(nameof(discover));
        _isSnapshotValid = isSnapshotValid ?? throw new ArgumentNullException(nameof(isSnapshotValid));
        _queueWork = queueWork ?? throw new ArgumentNullException(nameof(queueWork));
        _getTimestampMs = getTimestampMs ?? throw new ArgumentNullException(nameof(getTimestampMs));
        if (refreshIntervalMs < 0) throw new ArgumentOutOfRangeException(nameof(refreshIntervalMs));
        _refreshIntervalMs = refreshIntervalMs;
    }

    internal GameWindowDiscoverySnapshot GetCurrent()
    {
        while (true)
        {
            var observed = Volatile.Read(ref _state);
            bool valid;
            try
            {
                valid = _isSnapshotValid(observed.Snapshot);
            }
            catch
            {
                // Fail closed on validation errors so input safety never trusts an HWND that
                // could not be checked. Clear only the exact state read; a newer refresh wins.
                var invalidated = new State(
                    observed.Generation + 1,
                    GameWindowDiscoverySnapshot.Empty,
                    observed.ActiveSynchronousRefreshGeneration);
                if (ReferenceEquals(Interlocked.CompareExchange(ref _state, invalidated, observed), observed))
                {
                    Interlocked.Exchange(ref _nextRefreshAtMs, 0);
                    RequestBackgroundRefresh();
                    return invalidated.Snapshot;
                }
                continue;
            }

            if (valid)
            {
                if (!ReferenceEquals(Volatile.Read(ref _state), observed)) continue;
                RequestBackgroundRefresh();
                return observed.Snapshot;
            }

            var empty = new State(
                observed.Generation + 1,
                GameWindowDiscoverySnapshot.Empty,
                observed.ActiveSynchronousRefreshGeneration);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, empty, observed), observed))
            {
                // A stale HWND should be replaced promptly, independent of the ordinary miss
                // backoff. The generation change also prevents older queued work publishing.
                Interlocked.Exchange(ref _nextRefreshAtMs, 0);
                RequestBackgroundRefresh();
                return empty.Snapshot;
            }
        }
    }

    internal GameWindowDiscoverySnapshot RefreshSynchronously()
    {
        var generation = BeginSynchronousRefresh();
        try
        {
            var snapshot = _discover();
            if (TryPublishSynchronous(generation, snapshot))
                return GetCurrent();

            // A newer explicit refresh may still be pending. Do not return an older cached
            // HWND as though this rejected discovery had established the current target.
            var current = Volatile.Read(ref _state);
            return current.ActiveSynchronousRefreshGeneration != 0
                ? GameWindowDiscoverySnapshot.Empty
                : GetCurrent();
        }
        finally
        {
            ClearSynchronousRefresh(generation);
        }
    }

    private void RequestBackgroundRefresh()
    {
        if (Interlocked.CompareExchange(ref _refreshInProgress, 1, 0) != 0) return;

        long now;
        try
        {
            now = _getTimestampMs();
        }
        catch
        {
            Volatile.Write(ref _refreshInProgress, 0);
            return;
        }

        long next = Volatile.Read(ref _nextRefreshAtMs);
        if (now < next)
        {
            Volatile.Write(ref _refreshInProgress, 0);
            return;
        }

        Interlocked.Exchange(ref _nextRefreshAtMs, SaturatingAdd(now, _refreshIntervalMs));
        // Queueing a best-effort scan must not invalidate an explicit refresh already in
        // progress. Its captured generation makes it stale if a synchronous refresh starts
        // before the work actually runs or publishes.
        long generation = Volatile.Read(ref _state).Generation;
        try
        {
            _queueWork(() => RunBackgroundRefresh(generation));
        }
        catch
        {
            // A failed queue operation must release single-flight. Keep the throttle so repeated
            // UI ticks cannot spin on a permanently unavailable thread pool.
            Volatile.Write(ref _refreshInProgress, 0);
        }
    }

    private void RunBackgroundRefresh(long generation)
    {
        try
        {
            var observed = Volatile.Read(ref _state);
            if (observed.Generation != generation || observed.ActiveSynchronousRefreshGeneration != 0)
                return;

            var snapshot = _discover();
            TryPublishBackground(generation, snapshot);
        }
        catch
        {
            // Discovery is best effort. The next poll after the bounded backoff can retry.
        }
        finally
        {
            Volatile.Write(ref _refreshInProgress, 0);
        }
    }

    private long BeginSynchronousRefresh()
    {
        while (true)
        {
            var observed = Volatile.Read(ref _state);
            var generation = observed.Generation + 1;
            var updated = new State(generation, observed.Snapshot, generation);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, updated, observed), observed))
            {
                return generation;
            }
        }
    }

    private bool TryPublishSynchronous(long generation, GameWindowDiscoverySnapshot snapshot)
    {
        while (true)
        {
            var observed = Volatile.Read(ref _state);
            if (observed.Generation != generation || observed.ActiveSynchronousRefreshGeneration != generation)
                return false;

            var updated = new State(generation + 1, snapshot ?? GameWindowDiscoverySnapshot.Empty, 0);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, updated, observed), observed)) return true;
        }
    }

    private void ClearSynchronousRefresh(long generation)
    {
        while (true)
        {
            var observed = Volatile.Read(ref _state);
            if (observed.ActiveSynchronousRefreshGeneration != generation) return;

            var cleared = new State(observed.Generation + 1, observed.Snapshot, 0);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, cleared, observed), observed)) return;
        }
    }

    private void TryPublishBackground(long generation, GameWindowDiscoverySnapshot snapshot)
    {
        while (true)
        {
            var observed = Volatile.Read(ref _state);
            if (observed.Generation != generation || observed.ActiveSynchronousRefreshGeneration != 0) return;

            var updated = new State(generation + 1, snapshot ?? GameWindowDiscoverySnapshot.Empty, 0);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, updated, observed), observed)) return;
        }
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;
}
