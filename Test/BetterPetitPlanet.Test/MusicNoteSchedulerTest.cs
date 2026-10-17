using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Service;

namespace BetterPetitPlanet.Test;

public sealed class MusicNoteSchedulerTest
{
    private sealed class Input : IMusicInputTransport
    {
        public List<MusicKeyCommand[]> Batches { get; } = [];
        public List<(IntPtr Target, MusicInputMode Mode)> Routes { get; } = [];
        public int SuccessfulPrefix { get; set; } = int.MaxValue;
        public bool Fail { get; set; }
        public IReadOnlyList<MusicInputResult> Submit(IReadOnlyList<MusicKeyCommand> commands, IntPtr target, MusicInputMode mode)
        {
            Batches.Add(commands.ToArray());
            Routes.Add((target, mode));
            return commands.Select((c, i) => new MusicInputResult(c, !Fail && i < SuccessfulPrefix, "blocked")).ToArray();
        }
    }

    private static PerformanceEvent Note(string key, int start, int duration = 45, bool hold = false) => new()
    { NoteId = key + start, Key = key, TimeMs = start, DurationMs = duration, Kind = hold ? MusicNoteKind.Hold : MusicNoteKind.Tap };

    [Fact]
    public void SimultaneousNotes_SubmitOneBatchAndReleaseIndependently()
    {
        var input = new Input();
        var scheduler = new MusicNoteScheduler(input);
        scheduler.Load([Note("A", 0, 2500, true), Note("F", 0), Note("L", 0)]);
        scheduler.Advance(0, true, IntPtr.Zero, MusicInputMode.Keyboard);
        Assert.Equal(3, Assert.Single(input.Batches).Length);
        scheduler.Advance(45, true, IntPtr.Zero, MusicInputMode.Keyboard);
        Assert.Equal(["A"], scheduler.ActiveKeys);
        scheduler.Advance(2499, true, IntPtr.Zero, MusicInputMode.Keyboard);
        Assert.Equal(["A"], scheduler.ActiveKeys);
        scheduler.Advance(2500, true, IntPtr.Zero, MusicInputMode.Keyboard);
        Assert.Empty(scheduler.ActiveKeys);
    }

    [Fact]
    public void SeekIntoHold_DoesNotReplayPastNotes()
    {
        var input = new Input();
        var scheduler = new MusicNoteScheduler(input);
        scheduler.Load([Note("A", 0, 2500, true), Note("F", 1000)]);
        scheduler.Seek(500, IntPtr.Zero, MusicInputMode.Keyboard);
        scheduler.Advance(500, true, IntPtr.Zero, MusicInputMode.Keyboard);
        Assert.Empty(input.Batches);
        scheduler.Advance(1000, true, IntPtr.Zero, MusicInputMode.Keyboard);
        Assert.Equal("F", Assert.Single(Assert.Single(input.Batches)).Note.Key);
    }

    [Fact]
    public void AutomaticOff_ConsumesPastNotesAndReleasesHeldKeys()
    {
        var input = new Input();
        var scheduler = new MusicNoteScheduler(input);
        scheduler.Load([Note("A", 0, 2500, true), Note("F", 100), Note("L", 500)]);
        scheduler.Advance(0, true, IntPtr.Zero, MusicInputMode.Keyboard);
        scheduler.Advance(100, false, IntPtr.Zero, MusicInputMode.Keyboard);
        Assert.Empty(scheduler.ActiveKeys);
        scheduler.Advance(500, true, IntPtr.Zero, MusicInputMode.Keyboard);
        Assert.Equal(["A", "L"], input.Batches.SelectMany(x => x).Where(x => x.IsDown).Select(x => x.Note.Key));
    }

    [Fact]
    public void FailedSubmission_DoesNotOwnKeyOrEmitSuccessfulInput()
    {
        var input = new Input { Fail = true };
        var scheduler = new MusicNoteScheduler(input);
        scheduler.Load([Note("A", 0)]);
        var successful = new List<MusicKeyCommand>();
        scheduler.Submitted += successful.Add;
        scheduler.Advance(0, true, IntPtr.Zero, MusicInputMode.Keyboard);
        Assert.Empty(successful);
        Assert.Empty(scheduler.ActiveKeys);
        Assert.Equal("blocked", scheduler.LastError);
    }

    [Fact]
    public void ReleaseAll_RetriesFailedReleaseWithoutReplayingDown()
    {
        double now = 0;
        var input = new Input();
        var scheduler = new MusicNoteScheduler(input, monotonicMilliseconds: () => now);
        scheduler.Load([Note("A", 0, 2500, true)]);
        scheduler.Advance(0, true, IntPtr.Zero, MusicInputMode.Keyboard);
        input.Fail = true;
        scheduler.ReleaseAll(IntPtr.Zero, MusicInputMode.Keyboard);
        Assert.Equal(["A"], scheduler.ActiveKeys);
        input.Fail = false;
        now = 10;
        scheduler.RetryPendingReleases();
        Assert.Empty(scheduler.ActiveKeys);
        Assert.Single(input.Batches.SelectMany(x => x).Where(x => x.IsDown));
    }

    [Fact]
    public void ChangedRoute_ReleasesUsingOriginalTargetAndMode()
    {
        var input = new Input();
        var scheduler = new MusicNoteScheduler(input);
        scheduler.Load([Note("A", 0, 2500, true), Note("F", 100)]);
        scheduler.Advance(0, true, (IntPtr)1, MusicInputMode.Keyboard);
        scheduler.Advance(100, true, (IntPtr)2, MusicInputMode.Mouse);
        Assert.Equal(((IntPtr)1, MusicInputMode.Keyboard), input.Routes[1]);
        Assert.False(Assert.Single(input.Batches[1]).IsDown);
        Assert.Equal(((IntPtr)2, MusicInputMode.Mouse), input.Routes[2]);
    }

    [Fact]
    public void PartialBatch_OwnsAndReleasesOnlySuccessfulPrefix()
    {
        var input = new Input { SuccessfulPrefix = 2 };
        var scheduler = new MusicNoteScheduler(input);
        scheduler.Load([Note("A", 0), Note("F", 0), Note("L", 0)]);
        scheduler.Advance(0, true, (IntPtr)1, MusicInputMode.Keyboard);
        Assert.Equal(["A", "F"], scheduler.ActiveKeys);
        scheduler.ReleaseAll((IntPtr)2, MusicInputMode.Mouse);
        Assert.Equal(["A", "F"], input.Batches[1].Select(c => c.Note.Key));
        Assert.Equal(((IntPtr)1, MusicInputMode.Keyboard), input.Routes[1]);
        Assert.Empty(scheduler.ActiveKeys);
    }

    [Fact]
    public void OverlappingHold_DoesNotStealUnreleasedTapOwnership()
    {
        var input = new Input();
        var scheduler = new MusicNoteScheduler(input);
        scheduler.Load([Note("A", 0, 500), Note("A", 100, 2500, true)]);
        scheduler.Advance(0, true, IntPtr.Zero, MusicInputMode.Keyboard);
        scheduler.Advance(100, true, IntPtr.Zero, MusicInputMode.Keyboard);
        Assert.Single(input.Batches);
        Assert.Contains("overlap", scheduler.LastError, StringComparison.OrdinalIgnoreCase);
        scheduler.Advance(500, true, IntPtr.Zero, MusicInputMode.Keyboard);
        Assert.Empty(scheduler.ActiveKeys);
    }
}
