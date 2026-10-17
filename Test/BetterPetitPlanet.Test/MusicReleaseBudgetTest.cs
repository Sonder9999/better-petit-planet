using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Service;

namespace BetterPetitPlanet.Test;

public sealed class MusicReleaseBudgetTest
{
    private sealed class TestConfig : IConfigService
    {
        public AppConfig Config { get; } = new();
        public void Save() { }
        public void Reload() { }
    }

    private sealed class Input : IMusicInputTransport
    {
        private int _ups;
        public bool FailRelease { get; set; } = true;
        public int Ups => Volatile.Read(ref _ups);
        public TaskCompletionSource SecondUp { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<MusicInputResult> Submit(IReadOnlyList<MusicKeyCommand> commands, IntPtr target, MusicInputMode mode)
            => commands.Select(command =>
            {
                if (!command.IsDown && Interlocked.Increment(ref _ups) == 2) SecondUp.TrySetResult();
                return new MusicInputResult(command, command.IsDown || !FailRelease, "release failed");
            }).ToArray();
    }

    private static PerformanceTimeline Timeline() => new()
    {
        Events = [new() { Key = "A", Kind = MusicNoteKind.Hold, DurationMs = 2500 }]
    };

    private static MusicPlaybackService Create(Input input, Func<double> now)
    {
        var config = new TestConfig();
        var service = new MusicPlaybackService(config, new GameProcessDetector(config), new KeyInputTransports(),
            clock: new MusicSessionClock(now), inputTransport: input,
            options: new() { ReleaseRetryAttempts = 1, ReleaseRetryIntervalMs = 100 });
        service.ConfigurePreparation(0);
        service.LoadTimeline(Timeline());
        service.Play();
        service.Pause();
        Assert.Equal(1, input.Ups);
        return service;
    }

    [Theory]
    [InlineData("readiness")]
    [InlineData("automatic")]
    [InlineData("seek")]
    [InlineData("seek-note")]
    [InlineData("load")]
    [InlineData("dispose")]
    [InlineData("pause")]
    public async Task OrdinaryLifecycleChanges_DoNotRestartExhaustedRelease(string action)
    {
        double now = 0;
        var input = new Input();
        using var service = Create(input, () => Volatile.Read(ref now));
        for (int i = 0; i < 8; i++)
        {
            switch (action)
            {
                case "readiness": service.SetReadiness(false, "focus lost " + i); break;
                case "automatic": service.SetAutomaticPlayback(false); service.SetAutomaticPlayback(true); break;
                case "seek": service.Seek(0); break;
                case "seek-note": service.SeekNote(0); break;
                case "load": service.LoadTimeline(Timeline()); break;
                case "dispose": service.Dispose(); break;
                case "pause": service.Play(); service.Pause(); break;
            }
        }
        Assert.Equal(1, input.Ups);
        Volatile.Write(ref now, 1000);
        await Task.Delay(30);
        Assert.Equal(1, input.Ups);
        Assert.Equal(["A"], service.CaptureSnapshot().ActiveKeys);
    }

    [Fact]
    public async Task ExplicitStop_RestartsBudgetButPreservesNextAllowedReleaseTime()
    {
        double now = 0;
        var input = new Input();
        using var service = Create(input, () => Volatile.Read(ref now));
        service.Stop();
        for (int i = 0; i < 8; i++) service.SetReadiness(false, "focus lost " + i);
        Assert.Equal(1, input.Ups);
        Volatile.Write(ref now, 99);
        await Task.Delay(30);
        Assert.Equal(1, input.Ups);
        input.FailRelease = false;
        Volatile.Write(ref now, 100);
        await input.SecondUp.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(service.CaptureSnapshot().ActiveKeys);
    }
}
