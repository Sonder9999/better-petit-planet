using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Service;

namespace BetterPetitPlanet.Test;

public sealed class MusicSessionTest
{
    private sealed class TestConfig : IConfigService
    {
        public AppConfig Config { get; } = new();
        public void Save() { }
        public void Reload() { }
    }
    private sealed class Input : IMusicInputTransport
    {
        public readonly List<MusicKeyCommand> Commands = [];
        public int FailUpCount { get; set; }
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<MusicInputResult> Submit(IReadOnlyList<MusicKeyCommand> commands, IntPtr target, MusicInputMode mode)
        {
            lock (Commands)
            {
                Commands.AddRange(commands);
                return commands.Select(c =>
                {
                    bool success = c.IsDown || FailUpCount-- <= 0;
                    if (!c.IsDown && success) Released.TrySetResult();
                    return new MusicInputResult(c, success, "release failed");
                }).ToArray();
            }
        }
    }
    private static PerformanceTimeline Timeline() => new()
    {
        Events = [new() { Key = "A", TimeMs = 0, DurationMs = 2500, Kind = MusicNoteKind.Hold },
            new() { Key = "F", TimeMs = 4000 }], TotalDurationMs = 4045
    };
    private static MusicPlaybackService Create(Func<double> now, Input input, MusicPlaybackOptions? options = null)
    {
        var config = new TestConfig();
        config.Config.Music.AutoFocusGameOnPlayback = false;
        var service = new MusicPlaybackService(config, new GameProcessDetector(config), new KeyInputTransports(),
            clock: new MusicSessionClock(now), inputTransport: input, options: options);
        service.ConfigurePreparation(0);
        service.LoadTimeline(Timeline());
        return service;
    }

    [Fact]
    public void AutomaticOff_KeepsClockRunningAndEnablingDoesNotCatchUp()
    {
        double now = 0;
        var input = new Input();
        using var service = Create(() => now, input);
        service.Play();
        Assert.Contains(input.Commands, c => c.IsDown && c.Note.Key == "A");
        service.SetAutomaticPlayback(false);
        Assert.Contains(input.Commands, c => !c.IsDown && c.Note.Key == "A");
        now = 1000;
        Assert.Equal(1000, service.CurrentPositionMs);
        Assert.Equal(PlayerState.Playing, service.State);
        service.SetAutomaticPlayback(true);
        Assert.Single(input.Commands.Where(c => c.IsDown));
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("stop")]
    [InlineData("focus")]
    [InlineData("dispose")]
    public void Interruptions_ReleaseOwnedHold(string action)
    {
        var input = new Input();
        using var service = Create(() => 0, input);
        service.Play();
        switch (action)
        {
            case "pause": service.Pause(); break;
            case "stop": service.Stop(); break;
            case "focus": service.SetReadiness(false, "失焦"); break;
            case "dispose": service.Dispose(); break;
        }
        Assert.Equal([true, false], input.Commands.Select(c => c.IsDown));
        Assert.Empty(service.CaptureSnapshot().ActiveKeys);
    }

    [Fact]
    public void SpeedAndSeek_PreserveScoreTimeAndDoNotMutateCaller()
    {
        double now = 0;
        using var service = Create(() => now, new Input());
        service.SetAutomaticPlayback(false);
        service.Play();
        now = 500;
        service.SetSpeed(2);
        Assert.Equal(500, service.CurrentPositionMs);
        now = 1000;
        Assert.Equal(1500, service.CurrentPositionMs);
        service.Seek(1.25);
        Assert.Equal(1250, service.CurrentPositionMs);
        service.Pause();
        now = 9999;
        Assert.Equal(1250, service.CurrentPositionMs);
        var timeline = Timeline();
        timeline.Events[0].TimeMs = 100;
        service.LoadTimeline(timeline);
        Assert.Equal(100, timeline.Events[0].TimeMs);
        Assert.Equal(0, service.CurrentTimeline!.Events[0].TimeMs);
    }

    [Fact]
    public void ReadinessRecovery_DoesNotResumeAndManualStartUsesPreparation()
    {
        using var service = Create(() => 0, new Input());
        service.SetReadiness(false, "失焦");
        service.Play();
        Assert.NotEqual(PlayerState.Playing, service.State);
        service.SetReadiness(true, "就绪");
        service.StartFollowAlong(3000);
        Assert.False(service.AutomaticPlaybackEnabled);
        Assert.Equal("preparing", service.CaptureSnapshot().State);
        Assert.Equal(-3000, service.CurrentPositionMs);
        service.SetReadiness(false, "失焦");
        service.SetReadiness(true, "就绪");
        Assert.Equal(PlayerState.Paused, service.State);
    }

    [Fact]
    public async Task Dispose_RetriesFailedReleaseWithoutAnotherDown()
    {
        double now = 0;
        var input = new Input { FailUpCount = 1 };
        var service = Create(() => Volatile.Read(ref now), input, new() { ReleaseRetryIntervalMs = 1 });
        service.Play();
        service.Dispose();
        Volatile.Write(ref now, 1);
        await input.Released.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(service.CaptureSnapshot().ActiveKeys);
        lock (input.Commands) Assert.Single(input.Commands.Where(command => command.IsDown));
    }

    [Fact]
    public async Task SlowInputObserver_OverflowAdvancesRevisionAndReconstructsReleasedState()
    {
        using var blocker = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource<MusicSessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = Create(() => 0, new Input(), new() { NotificationCapacity = 1 });
        int observed = 0;
        service.InputChanged += _ =>
        {
            if (Interlocked.Increment(ref observed) != 1) return;
            entered.SetResult();
            blocker.Wait();
        };
        service.Play();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            service.Pause();
            service.Seek(0);
            long revision = service.CaptureSnapshot().Revision;
            await Task.Run(service.Play).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(service.CaptureSnapshot().Revision > revision);
            service.SetAutomaticPlayback(false);
            service.SessionChanged += snapshot => recovered.TrySetResult(snapshot);
        }
        finally { blocker.Set(); }
        var snapshot = await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(snapshot.ActiveKeys);
        Assert.Equal(service.CaptureSnapshot().Revision, snapshot.Revision);
    }
}

