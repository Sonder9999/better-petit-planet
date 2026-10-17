using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Service;

namespace BetterPetitPlanet.Test;

public sealed class SourceChartAdapterTest
{
    private static readonly Dictionary<string, MusicLane> Mapping = new()
    {
        ["Left_Left"] = MusicLane.A, ["Left_Down"] = MusicLane.S, ["Left_Right"] = MusicLane.D,
        ["Right_Left"] = MusicLane.F, ["Right_Down"] = MusicLane.J, ["Right_Right"] = MusicLane.K,
        ["Right_Up"] = MusicLane.L
    };

    [Theory]
    [InlineData("_preview.json", 5, 2, 800)]
    [InlineData("53_Nahida_Normal.json", 136, 4, 2143)]
    public async Task SourceFixtures_PreserveNoteCountsAndFullHolds(string file, int taps, int holds, int longest)
    {
        var json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Music", file));
        var chart = SourceChartAdapter.AdaptJson(json, Mapping, file);
        Assert.Equal(taps, chart.Events.Count(n => n.Kind == MusicNoteKind.Tap));
        Assert.Equal(holds, chart.Events.Count(n => n.Kind == MusicNoteKind.Hold));
        Assert.Equal(longest, chart.Events.Where(n => n.Kind == MusicNoteKind.Hold).Max(n => n.DurationMs));
        Assert.Equal(chart.Events.Count, chart.Events.Select(n => n.NoteId).Distinct().Count());
        Assert.Contains(chart.Events, n => n.SyncGroupId != null);
    }

    [Fact]
    public void Mapping_IsExplicitAndNeverAssumesInstrumentPitches()
    {
        const string json = """{"buttonSequence":[{"button":"Right_Up","time":100}]}""";
        Assert.Throws<InvalidDataException>(() => SourceChartAdapter.AdaptJson(json, new Dictionary<string, MusicLane>()));
        var chart = SourceChartAdapter.AdaptJson(json, new Dictionary<string, MusicLane> { ["Right_Up"] = MusicLane.F });
        Assert.Equal("F", Assert.Single(chart.Events).Key);
    }

    [Theory]
    [InlineData("""{"button":"Unknown","time":0}""")]
    [InlineData("""{"button":"Left_Left","time":-1}""")]
    [InlineData("""{"button":"Left_Left","time":"1"}""")]
    [InlineData("""{"button":"Left_Left"}""")]
    [InlineData("""{"button":"Left_Left","time":0,"isLongPress":true,"longPressTime":0}""")]
    public void InvalidSource_ReportsEventContext(string note)
    {
        var error = Assert.Throws<InvalidDataException>(() => SourceChartAdapter.AdaptJson("{\"buttonSequence\":[" + note + "]}", Mapping));
        Assert.Contains("Event 0", error.Message);
    }
}
