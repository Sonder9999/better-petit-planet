using System.Text.Json;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Skins;

namespace BetterPetitPlanet.Test;

public class MusicReferenceHoldEmissionTest
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Lazy<JsonDocument> Fixture = new(() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Music", "reference-hold-schedules.json"))));

    [Theory]
    [InlineData("sumeru")]
    public void EveryActiveProfile_MatchesUnmodifiedTypeScriptScheduler(string skin)
    {
        var row = Fixture.Value.RootElement.GetProperty("profiles").EnumerateArray()
            .Single(item => item.GetProperty("skin").GetString() == skin);
        var profile = row.GetProperty("profile").Deserialize<ReferenceHoldProfile>(JsonOptions)!;
        Assert.True(profile.FrameSize > 0);
        Assert.True(profile.Columns > 0);
        Assert.Equal($"assets/hold/compiled/{skin}-normal.png", profile.NormalUrl);
        Assert.Equal($"assets/hold/compiled/{skin}-additive.png", profile.AdditiveUrl);
        Assert.Equal(profile.Layers.Length, row.GetProperty("schedules").GetArrayLength());
        for (var index = 0; index < profile.Layers.Length; index++)
        {
            var layer = profile.Layers[index];
            Assert.False(string.IsNullOrWhiteSpace(layer.Name));
            Assert.False(string.IsNullOrWhiteSpace(layer.SourceId));
            Assert.True(layer.DisplaySize > 0);
            AssertOracle(layer, row.GetProperty("schedules")[index], $"{skin}/{index}/{layer.Name}");
        }
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("empty-emission")]
    [InlineData("zero-capacity")]
    [InlineData("finite-rate-excludes-duration")]
    [InlineData("finite-burst-after-duration")]
    [InlineData("simultaneous-bursts-rate-and-capacity")]
    [InlineData("source-order-zero-interval-bursts")]
    [InlineData("ambient-finite-rate-phase")]
    [InlineData("ambient-finite-burst-phase")]
    [InlineData("ambient-no-emission")]
    [InlineData("ambient-disabled")]
    public void EmissionEdges_MatchUnmodifiedTypeScriptScheduler(string name)
    {
        var row = Fixture.Value.RootElement.GetProperty("synthetic").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == name);
        AssertOracle(row.GetProperty("layer").Deserialize<ReferenceHoldLayer>(JsonOptions)!, row, name);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonfiniteAge_IsRejectedWithoutChangingSchedule(double age)
    {
        var schedule = new ReferenceHoldEmission(FirstLayer());
        schedule.Update(800);
        var births = schedule.Births.ToArray();
        var frames = Enumerable.Range(0, births.Length).Select(schedule.Frame).ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => schedule.Update(age));
        Assert.Equal(births, schedule.Births);
        Assert.Equal(frames, Enumerable.Range(0, births.Length).Select(schedule.Frame));
    }

    [Fact]
    public void FixedPool_RemainsReadOnlyAndBoundedAcrossLongSeeksAndRewinds()
    {
        var layer = FirstLayer();
        var schedule = new ReferenceHoldEmission(layer);
        var births = schedule.Births;
        Assert.Equal(layer.Slots, births.Count);
        Assert.All(births, birth => Assert.Equal(double.NegativeInfinity, birth));
        if (births is IList<double> list)
            Assert.Throws<NotSupportedException>(() => list[0] = 42);
        foreach (var age in new[] { 0.0, 500, 3600000, 100, -1, 1500 })
        {
            schedule.Update(age);
            Assert.Same(births, schedule.Births);
            Assert.Equal(layer.Slots, births.Count);
        }
        Assert.Null(schedule.Frame(-1));
        Assert.Null(schedule.Frame(layer.Slots));
        Assert.Null(schedule.Frame(int.MaxValue));
    }

    [Fact]
    public void Oracle_CoversBenchmarkProfileAndAllTwentyThreeSourceLayers()
    {
        var profiles = Fixture.Value.RootElement.GetProperty("profiles");
        Assert.Equal(1, profiles.GetArrayLength());
        Assert.Equal(23, profiles.EnumerateArray().Sum(profile => profile.GetProperty("profile")
            .GetProperty("layers").GetArrayLength()));
        Assert.Equal("564c0368c6e94ed0d9e8f6eef9da8ddda1771283da0f11452234021a70d990dd",
            Fixture.Value.RootElement.GetProperty("sourceHashes").GetProperty("render/hold-emission.ts").GetString());
    }

    private static ReferenceHoldLayer FirstLayer() => Fixture.Value.RootElement.GetProperty("profiles")[0]
        .GetProperty("profile").GetProperty("layers")[0].Deserialize<ReferenceHoldLayer>(JsonOptions)!;

    private static void AssertOracle(ReferenceHoldLayer layer, JsonElement oracle, string context)
    {
        Assert.Equal(oracle.GetProperty("slots").GetInt32(), layer.Slots);
        foreach (var run in oracle.GetProperty("runs").EnumerateArray())
        {
            var schedule = new ReferenceHoldEmission(layer);
            Assert.All(schedule.Births, birth => Assert.Equal(double.NegativeInfinity, birth));
            foreach (var sample in run.GetProperty("samples").EnumerateArray())
            {
                var age = sample.GetProperty("ageMs").GetDouble();
                schedule.Update(age);
                var expectedBirths = sample.GetProperty("births").EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.Null ? double.NegativeInfinity : item.GetDouble()).ToArray();
                var expectedFrames = sample.GetProperty("frames").EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.Null ? (int?)null : item.GetInt32()).ToArray();
                var location = $"{context}, {run.GetProperty("name").GetString()}, age {age:R}";
                Assert.True(expectedBirths.SequenceEqual(schedule.Births), $"Birth pool mismatch: {location}");
                var actualFrames = Enumerable.Range(0, layer.Slots).Select(schedule.Frame);
                Assert.True(expectedFrames.SequenceEqual(actualFrames), $"Frame mismatch: {location}");
                Assert.Null(schedule.Frame(-1));
                Assert.Null(schedule.Frame(layer.Slots));
            }
        }
    }
}
