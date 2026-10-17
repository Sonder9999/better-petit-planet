using BetterPetitPlanet.Core.Process;

namespace BetterPetitPlanet.Test;

public sealed class GameWindowDiscoveryCacheTest
{
    private sealed class Harness
    {
        public long NowMs;
        public readonly List<Action> QueuedWork = [];
        public GameWindowDiscoverySnapshot NextResult = GameWindowDiscoverySnapshot.Empty;
        public bool WindowValid = true;
        public bool DiscoverThrows;
        public int DiscoveryCount;

        public GameWindowDiscoveryCache Create(long refreshIntervalMs = 500) => new(
            discover: () =>
            {
                DiscoveryCount++;
                if (DiscoverThrows) throw new InvalidOperationException("diagnostic scan failed");
                return NextResult;
            },
            isSnapshotValid: snapshot => snapshot.MainWindowHandle == IntPtr.Zero || WindowValid,
            queueWork: work => QueuedWork.Add(work),
            getTimestampMs: () => NowMs,
            refreshIntervalMs: refreshIntervalMs);
    }

    [Fact]
    public void RepeatedPollsForMissingGame_AreBoundedAndRateLimited()
    {
        var harness = new Harness();
        var cache = harness.Create();

        for (int i = 0; i < 25; i++)
        {
            Assert.False(cache.GetCurrent().IsRunning);
        }

        Assert.Single(harness.QueuedWork);
        harness.QueuedWork[0]();
        Assert.Equal(1, harness.DiscoveryCount);

        for (int i = 0; i < 25; i++)
        {
            Assert.False(cache.GetCurrent().IsRunning);
        }

        Assert.Single(harness.QueuedWork);
        harness.NowMs = 500;
        cache.GetCurrent();
        Assert.Equal(2, harness.QueuedWork.Count);
    }

    [Fact]
    public void InvalidCachedWindow_IsClearedAndRestartDiscoveryFindsNewProcessWindow()
    {
        var harness = new Harness
        {
            NextResult = new GameWindowDiscoverySnapshot(101, new IntPtr(1001))
        };
        var cache = harness.Create();

        cache.GetCurrent();
        harness.QueuedWork[0]();
        Assert.Equal(new IntPtr(1001), cache.GetCurrent().MainWindowHandle);

        harness.WindowValid = false;
        Assert.False(cache.GetCurrent().IsRunning);
        Assert.Equal(2, harness.QueuedWork.Count);

        harness.NextResult = new GameWindowDiscoverySnapshot(202, new IntPtr(2002));
        harness.WindowValid = true;
        harness.QueuedWork[1]();

        var restarted = cache.GetCurrent();
        Assert.Equal(202, restarted.ProcessId);
        Assert.Equal(new IntPtr(2002), restarted.MainWindowHandle);
    }

    [Fact]
    public void OlderQueuedDiscovery_CannotOverwriteNewerForcedRefresh()
    {
        var harness = new Harness();
        var cache = harness.Create();

        cache.GetCurrent();
        Assert.Single(harness.QueuedWork);

        harness.NextResult = new GameWindowDiscoverySnapshot(202, new IntPtr(2002));
        Assert.Equal(new IntPtr(2002), cache.RefreshSynchronously().MainWindowHandle);

        harness.NextResult = new GameWindowDiscoverySnapshot(101, new IntPtr(1001));
        harness.QueuedWork[0]();

        var current = cache.GetCurrent();
        Assert.Equal(202, current.ProcessId);
        Assert.Equal(new IntPtr(2002), current.MainWindowHandle);
    }

    [Fact]
    public void BackgroundDiscoveryException_ReleasesSingleFlightAndHonorsBackoff()
    {
        var harness = new Harness();
        var cache = harness.Create();

        cache.GetCurrent();
        Assert.Single(harness.QueuedWork);

        harness.DiscoverThrows = true;
        harness.QueuedWork[0]();
        Assert.Equal(1, harness.DiscoveryCount);

        for (int i = 0; i < 25; i++)
        {
            Assert.False(cache.GetCurrent().IsRunning);
        }

        Assert.Single(harness.QueuedWork);
        harness.NowMs = 500;
        cache.GetCurrent();
        Assert.Equal(2, harness.QueuedWork.Count);
    }

    [Fact]
    public void ForegroundMismatch_DoesNotRescanOrDiscardValidWindow()
    {
        var harness = new Harness
        {
            NextResult = new GameWindowDiscoverySnapshot(101, new IntPtr(1001))
        };
        var cache = harness.Create();
        cache.RefreshSynchronously();

        var detector = new GameProcessDetector(
            cache,
            getForegroundWindow: () => new IntPtr(3003),
            getRootWindow: handle => handle,
            getWindowProcessId: _ => 303);

        for (int i = 0; i < 25; i++)
        {
            Assert.False(detector.IsGameForegroundCached());
        }

        Assert.Equal(1, harness.DiscoveryCount);
        Assert.Equal(new IntPtr(1001), cache.GetCurrent().MainWindowHandle);
    }

    [Fact]
    public void Invalidation_ClearsOnlySnapshotThatWasActuallyRead()
    {
        var harness = new Harness
        {
            NextResult = new GameWindowDiscoverySnapshot(101, new IntPtr(1001))
        };
        GameWindowDiscoveryCache? cache = null;
        bool invalidate = false;
        bool forceNewSnapshotDuringValidation = false;
        cache = new GameWindowDiscoveryCache(
            discover: () =>
            {
                harness.DiscoveryCount++;
                return harness.NextResult;
            },
            isSnapshotValid: snapshot =>
            {
                if (invalidate && !forceNewSnapshotDuringValidation && snapshot.MainWindowHandle != IntPtr.Zero)
                {
                    forceNewSnapshotDuringValidation = true;
                    harness.NextResult = new GameWindowDiscoverySnapshot(202, new IntPtr(2002));
                    cache!.RefreshSynchronously();
                    return false;
                }
                return true;
            },
            queueWork: work => harness.QueuedWork.Add(work),
            getTimestampMs: () => harness.NowMs,
            refreshIntervalMs: 500);

        cache.RefreshSynchronously();
        invalidate = true;

        var current = cache.GetCurrent();

        Assert.Equal(202, current.ProcessId);
        Assert.Equal(new IntPtr(2002), current.MainWindowHandle);
    }

    [Fact]
    public void ValidationFailure_FailsClosedAndQueuesReplacement()
    {
        var harness = new Harness
        {
            NextResult = new GameWindowDiscoverySnapshot(101, new IntPtr(1001))
        };
        var shouldThrow = false;
        var cache = new GameWindowDiscoveryCache(
            discover: () => harness.NextResult,
            isSnapshotValid: snapshot =>
            {
                if (shouldThrow && snapshot.MainWindowHandle != IntPtr.Zero)
                    throw new InvalidOperationException("window validation failed");
                return true;
            },
            queueWork: work => harness.QueuedWork.Add(work),
            getTimestampMs: () => harness.NowMs,
            refreshIntervalMs: 500);

        cache.RefreshSynchronously();
        shouldThrow = true;

        Assert.False(cache.GetCurrent().IsRunning);
        Assert.Single(harness.QueuedWork);
    }

    [Fact]
    public async Task SynchronousRefreshInvalidatedWhileDiscovering_ReturnsWinningSafeSnapshot()
    {
        var warmedSnapshot = new GameWindowDiscoverySnapshot(101, new IntPtr(1001));
        var staleRefreshResult = new GameWindowDiscoverySnapshot(101, new IntPtr(1001));
        var queuedWork = new List<Action>();
        using var discoveryEntered = new ManualResetEventSlim();
        using var releaseDiscovery = new ManualResetEventSlim();
        int discoveryCount = 0;
        bool windowValid = true;
        var cache = new GameWindowDiscoveryCache(
            discover: () =>
            {
                int call = Interlocked.Increment(ref discoveryCount);
                if (call == 2)
                {
                    discoveryEntered.Set();
                    if (!releaseDiscovery.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Test did not release synchronous discovery.");
                    return staleRefreshResult;
                }
                return warmedSnapshot;
            },
            isSnapshotValid: snapshot => snapshot.MainWindowHandle == IntPtr.Zero || Volatile.Read(ref windowValid),
            queueWork: work => queuedWork.Add(work),
            getTimestampMs: () => 0,
            refreshIntervalMs: 500);

        cache.RefreshSynchronously();
        var refreshTask = Task.Run(cache.RefreshSynchronously);
        Assert.True(discoveryEntered.Wait(TimeSpan.FromSeconds(5)), "Synchronous discovery should be blocked in the test delegate.");

        Volatile.Write(ref windowValid, false);
        var invalidated = cache.GetCurrent();
        Assert.Equal(GameWindowDiscoverySnapshot.Empty, invalidated);

        releaseDiscovery.Set();
        var refreshResult = await refreshTask;

        Assert.Equal(GameWindowDiscoverySnapshot.Empty, refreshResult);
        Assert.Equal(GameWindowDiscoverySnapshot.Empty, cache.GetCurrent());
        Assert.Single(queuedWork);

        // The queued work from before the in-flight refresh is stale, but must release its
        // single-flight slot so invalidation can schedule a fresh discovery afterward.
        queuedWork[0]();
        Volatile.Write(ref windowValid, true);
        cache.GetCurrent();
        Assert.Equal(2, queuedWork.Count);
        queuedWork[1]();
        Assert.Equal(warmedSnapshot, cache.GetCurrent());
    }

    [Fact]
    public async Task OlderSynchronousRefresh_ReturnsNewerExplicitRefreshResult()
    {
        var warmedSnapshot = new GameWindowDiscoverySnapshot(101, new IntPtr(1001));
        var staleRefreshResult = new GameWindowDiscoverySnapshot(202, new IntPtr(2002));
        var winningRefreshResult = new GameWindowDiscoverySnapshot(303, new IntPtr(3003));
        using var olderDiscoveryEntered = new ManualResetEventSlim();
        using var releaseOlderDiscovery = new ManualResetEventSlim();
        int discoveryCount = 0;
        var cache = new GameWindowDiscoveryCache(
            discover: () =>
            {
                int call = Interlocked.Increment(ref discoveryCount);
                if (call == 2)
                {
                    olderDiscoveryEntered.Set();
                    if (!releaseOlderDiscovery.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Test did not release older synchronous discovery.");
                    return staleRefreshResult;
                }
                return call == 3 ? winningRefreshResult : warmedSnapshot;
            },
            isSnapshotValid: _ => true,
            queueWork: _ => { },
            getTimestampMs: () => 0,
            refreshIntervalMs: 500);

        cache.RefreshSynchronously();
        var olderRefresh = Task.Run(cache.RefreshSynchronously);
        Assert.True(olderDiscoveryEntered.Wait(TimeSpan.FromSeconds(5)), "Older discovery should block while the newer refresh runs.");

        var newerResult = cache.RefreshSynchronously();
        releaseOlderDiscovery.Set();
        var olderResult = await olderRefresh;

        Assert.Equal(winningRefreshResult, newerResult);
        Assert.Equal(winningRefreshResult, olderResult);
        Assert.Equal(winningRefreshResult, cache.GetCurrent());
    }

    [Fact]
    public async Task BackgroundQueuedDuringSynchronousRefresh_DoesNotSupersedeExplicitResult()
    {
        var warmedSnapshot = new GameWindowDiscoverySnapshot(101, new IntPtr(1001));
        var freshSnapshot = new GameWindowDiscoverySnapshot(202, new IntPtr(2002));
        var queuedWork = new List<Action>();
        using var discoveryEntered = new ManualResetEventSlim();
        using var releaseDiscovery = new ManualResetEventSlim();
        int discoveryCount = 0;
        long nowMs = 0;
        var cache = new GameWindowDiscoveryCache(
            discover: () =>
            {
                int call = Interlocked.Increment(ref discoveryCount);
                if (call == 1) return warmedSnapshot;
                if (call == 2)
                {
                    discoveryEntered.Set();
                    if (!releaseDiscovery.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Test did not release synchronous discovery.");
                    return freshSnapshot;
                }
                return warmedSnapshot;
            },
            isSnapshotValid: _ => true,
            queueWork: work => queuedWork.Add(work),
            getTimestampMs: () => Volatile.Read(ref nowMs),
            refreshIntervalMs: 500);

        cache.GetCurrent();
        queuedWork[0]();
        Assert.Equal(warmedSnapshot, cache.GetCurrent());
        Volatile.Write(ref nowMs, 500);

        var refreshTask = Task.Run(cache.RefreshSynchronously);
        Assert.True(discoveryEntered.Wait(TimeSpan.FromSeconds(5)), "Synchronous discovery should be blocked in the test delegate.");

        Assert.Equal(warmedSnapshot, cache.GetCurrent());
        Assert.Equal(2, queuedWork.Count);

        releaseDiscovery.Set();
        var refreshResult = await refreshTask;

        Assert.Equal(freshSnapshot, refreshResult);
        Assert.Equal(freshSnapshot, cache.GetCurrent());
        queuedWork[1]();
        Assert.Equal(2, discoveryCount);
        Assert.Equal(freshSnapshot, cache.GetCurrent());
    }

    [Fact]
    public async Task OlderSynchronousRefresh_ReturnsEmptyWhileNewerRefreshIsPending()
    {
        var warmedSnapshot = new GameWindowDiscoverySnapshot(101, new IntPtr(1001));
        var olderSnapshot = new GameWindowDiscoverySnapshot(202, new IntPtr(2002));
        var newerSnapshot = new GameWindowDiscoverySnapshot(303, new IntPtr(3003));
        using var olderEntered = new ManualResetEventSlim();
        using var releaseOlder = new ManualResetEventSlim();
        using var newerEntered = new ManualResetEventSlim();
        using var releaseNewer = new ManualResetEventSlim();
        int discoveryCount = 0;
        var cache = new GameWindowDiscoveryCache(
            discover: () =>
            {
                int call = Interlocked.Increment(ref discoveryCount);
                if (call == 2)
                {
                    olderEntered.Set();
                    if (!releaseOlder.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Test did not release older synchronous discovery.");
                    return olderSnapshot;
                }
                if (call == 3)
                {
                    newerEntered.Set();
                    if (!releaseNewer.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Test did not release newer synchronous discovery.");
                    return newerSnapshot;
                }
                return warmedSnapshot;
            },
            isSnapshotValid: _ => true,
            queueWork: _ => { },
            getTimestampMs: () => 0,
            refreshIntervalMs: 500);

        cache.RefreshSynchronously();
        var olderRefresh = Task.Run(cache.RefreshSynchronously);
        Assert.True(olderEntered.Wait(TimeSpan.FromSeconds(5)), "Older discovery should block while the newer refresh starts.");
        var newerRefresh = Task.Run(cache.RefreshSynchronously);
        Assert.True(newerEntered.Wait(TimeSpan.FromSeconds(5)), "Newer discovery should remain pending while the older refresh completes.");

        releaseOlder.Set();
        var olderResult = await olderRefresh;

        Assert.Equal(GameWindowDiscoverySnapshot.Empty, olderResult);

        releaseNewer.Set();
        var newerResult = await newerRefresh;

        Assert.Equal(newerSnapshot, newerResult);
        Assert.Equal(newerSnapshot, cache.GetCurrent());
    }

    [Fact]
    public void SynchronousRefreshException_ClearsItsPendingTokenAndAllowsBackgroundRecovery()
    {
        var warmedSnapshot = new GameWindowDiscoverySnapshot(101, new IntPtr(1001));
        var recoveredSnapshot = new GameWindowDiscoverySnapshot(202, new IntPtr(2002));
        var queuedWork = new List<Action>();
        long nowMs = 0;
        int discoveryCount = 0;
        bool throwNextDiscovery = false;
        var cache = new GameWindowDiscoveryCache(
            discover: () =>
            {
                int call = Interlocked.Increment(ref discoveryCount);
                if (throwNextDiscovery)
                {
                    throwNextDiscovery = false;
                    throw new InvalidOperationException("explicit discovery failed");
                }
                return call >= 3 ? recoveredSnapshot : warmedSnapshot;
            },
            isSnapshotValid: _ => true,
            queueWork: work => queuedWork.Add(work),
            getTimestampMs: () => Volatile.Read(ref nowMs),
            refreshIntervalMs: 500);

        cache.GetCurrent();
        queuedWork[0]();
        Assert.Equal(warmedSnapshot, cache.GetCurrent());

        throwNextDiscovery = true;
        Assert.Throws<InvalidOperationException>(() => cache.RefreshSynchronously());
        Assert.Equal(warmedSnapshot, cache.GetCurrent());

        Volatile.Write(ref nowMs, 500);
        cache.GetCurrent();
        Assert.Equal(2, queuedWork.Count);
        queuedWork[1]();

        Assert.Equal(recoveredSnapshot, cache.GetCurrent());
        Assert.Equal(3, discoveryCount);
    }

    [Fact]
    public async Task OlderSynchronousRefreshException_DoesNotClearNewerPendingToken()
    {
        var warmedSnapshot = new GameWindowDiscoverySnapshot(101, new IntPtr(1001));
        var newerSnapshot = new GameWindowDiscoverySnapshot(303, new IntPtr(3003));
        using var olderEntered = new ManualResetEventSlim();
        using var releaseOlder = new ManualResetEventSlim();
        using var newerEntered = new ManualResetEventSlim();
        using var releaseNewer = new ManualResetEventSlim();
        int discoveryCount = 0;
        var cache = new GameWindowDiscoveryCache(
            discover: () =>
            {
                int call = Interlocked.Increment(ref discoveryCount);
                if (call == 2)
                {
                    olderEntered.Set();
                    if (!releaseOlder.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Test did not release older synchronous discovery.");
                    throw new InvalidOperationException("older explicit discovery failed");
                }
                if (call == 3)
                {
                    newerEntered.Set();
                    if (!releaseNewer.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Test did not release newer synchronous discovery.");
                    return newerSnapshot;
                }
                return warmedSnapshot;
            },
            isSnapshotValid: _ => true,
            queueWork: _ => { },
            getTimestampMs: () => 0,
            refreshIntervalMs: 500);

        cache.RefreshSynchronously();
        var olderRefresh = Task.Run(cache.RefreshSynchronously);
        Assert.True(olderEntered.Wait(TimeSpan.FromSeconds(5)), "Older discovery should block before the newer refresh starts.");
        var newerRefresh = Task.Run(cache.RefreshSynchronously);
        Assert.True(newerEntered.Wait(TimeSpan.FromSeconds(5)), "Newer discovery should remain pending while the older refresh fails.");

        releaseOlder.Set();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await olderRefresh);

        releaseNewer.Set();
        var newerResult = await newerRefresh;

        Assert.Equal(newerSnapshot, newerResult);
        Assert.Equal(newerSnapshot, cache.GetCurrent());
    }

}
