using BetterPetitPlanet.GameTask.Music.Service;

namespace BetterPetitPlanet.Test;

public sealed class MusicNotificationQueueTest
{
    [Fact]
    public async Task Saturation_ReconcilesInputCoalescesProgressAndPreservesControls()
    {
        using var blocker = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int overflows = 0, progress = -1, control = 0, inputs = 0;
        using var queue = new MusicNotificationQueue(_ => { }, 2, () => Interlocked.Increment(ref overflows));
        queue.Post(() => { entered.SetResult(); blocker.Wait(); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            for (int i = 0; i < 100; i++)
            {
                int current = i;
                queue.PostInput(() => Interlocked.Increment(ref inputs));
                queue.PostProgress(() => progress = current);
            }
            queue.Post(() => control++); // PlaybackCompleted / config save remain reliable.
            Assert.True(overflows > 0);
        }
        finally { blocker.Set(); queue.Dispose(); }
        await queue.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, control);
        Assert.Equal(99, progress);
        Assert.InRange(inputs, 0, 2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100001)]
    public void Options_InvalidNotificationCapacityIsRejected(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MusicPlaybackOptions { NotificationCapacity = capacity }.Validate());
    }
}
