using BetterPetitPlanet.GameTask.Music.Service;
using BetterPetitPlanet.GameTask.Music.Model;

namespace BetterPetitPlanet.Test;

public sealed class MusicScoreValidationTest
{
    private static async Task<BetterPetitPlanet.GameTask.Music.Model.PerformanceTimeline?> Parse(string sequence)
    {
        var path = Path.Combine("Validation", "music", Guid.NewGuid() + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await File.WriteAllTextAsync(path, "{\"sequence\":[" + sequence + "]}");
            return await new PetitScoreParser().ParseFileAsync(path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ExplicitHold_PreservesFullDuration()
    {
        var timeline = await Parse("""{"time_ms":0,"key":"F","type":"hold","duration_ms":2500}""");
        Assert.NotNull(timeline);
        Assert.Equal(2500, timeline.Events[0].DurationMs);
    }

    [Theory]
    [InlineData("""{"time_ms":-1,"key":"A"}""")]
    [InlineData("""{"time_ms":0,"key":"Z"}""")]
    [InlineData("""{"time_ms":0,"key":"A","action":"hold","duration_ms":0}""")]
    [InlineData("""{"time_ms":0,"key":"A"},{"time_ms":0,"key":"A"}""")]
    [InlineData("""{"time_ms":0,"key":"A","type":"hold","duration_ms":2500},{"time_ms":1000,"key":"A"}""")]
    public async Task InvalidNotes_RejectWholeScore(string sequence)
    {
        Assert.Null(await Parse(sequence));
    }

    [Theory]
    [InlineData("press")]
    [InlineData("hold")]
    public async Task MissingType_IsLegacyTapEvenWithLongDuration(string action)
    {
        var timeline = await Parse($$"""{"time_ms":0,"key":"A","action":"{{action}}","duration_ms":800}""");
        Assert.NotNull(timeline);
        Assert.Equal(MusicNoteKind.Tap, timeline.Events[0].Kind);
        Assert.Equal(500, timeline.Events[0].DurationMs);
    }

    [Fact]
    public void TimelineCopy_RejectsDuplicateExplicitNoteIds()
    {
        var timeline = new PerformanceTimeline
        {
            Events = [new() { NoteId = "same", Key = "A" }, new() { NoteId = "same", Key = "F" }]
        };
        Assert.Throws<InvalidDataException>(() => ScoreNormalizer.CopyTimeline(timeline));
    }

    [Fact]
    public void TimelineCopy_IsolatesMetadataAndPreservesExplicitIds()
    {
        var source = new PerformanceTimeline
        {
            Metadata = new() { Title = "original" },
            Events = [new() { NoteId = "explicit", Key = "A", TimeMs = 100 }]
        };
        var copy = ScoreNormalizer.CopyTimeline(source);
        copy.Metadata.Title = "edited";
        Assert.Equal("original", source.Metadata.Title);
        Assert.Equal("explicit", copy.Events[0].NoteId);
        Assert.Equal(100, source.Events[0].TimeMs);
    }

    [Theory]
    [InlineData(1, 20)]
    [InlineData(800, 500)]
    public void TimelineCopy_LegacyTapRetainsPulseBounds(int duration, int expected)
    {
        var source = new PerformanceTimeline { Events = [new() { Key = "A", DurationMs = duration }] };
        var copy = ScoreNormalizer.CopyTimeline(source);
        Assert.Equal(MusicNoteKind.Tap, copy.Events[0].Kind);
        Assert.Equal(expected, copy.Events[0].DurationMs);
        Assert.Equal(duration, source.Events[0].DurationMs);
    }

    [Fact]
    public void TimelineCopy_MissingIdsAvoidAllReservedExplicitIdsDeterministically()
    {
        var source = new PerformanceTimeline
        {
            Events = [new() { Key = "A" }, new() { Key = "F", TimeMs = 100, NoteId = "note-0" },
                new() { Key = "L", TimeMs = 200, NoteId = "note-1" }, new() { Key = "J", TimeMs = 300 }]
        };
        var copy = ScoreNormalizer.CopyTimeline(source);
        Assert.Equal(["note-2", "note-0", "note-1", "note-3"], copy.Events.Select(note => note.NoteId));
        Assert.Equal(copy.Events.Select(note => note.NoteId), ScoreNormalizer.CopyTimeline(source).Events.Select(note => note.NoteId));
        Assert.Empty(source.Events[0].NoteId);
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(499, false)]
    [InlineData(500, true)]
    public void HoldAfterMultipleTaps_ChecksEveryEarlierInterval(int holdStart, bool accepted)
    {
        var notes = new PerformanceEvent[]
        {
            new() { Key = "A", TimeMs = 0, DurationMs = 500 },
            new() { Key = "A", TimeMs = 100, DurationMs = 20 },
            new() { Key = "A", TimeMs = holdStart, DurationMs = 100, Kind = MusicNoteKind.Hold }
        };
        if (accepted) ScoreNormalizer.Validate(notes);
        else Assert.Throws<InvalidDataException>(() => ScoreNormalizer.Validate(notes));
    }
}
