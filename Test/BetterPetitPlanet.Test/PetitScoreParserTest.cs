using System.IO;
using System.Threading.Tasks;
using BetterPetitPlanet.GameTask.Music.Service;
using Xunit;

namespace BetterPetitPlanet.Test;

public class PetitScoreParserTest
{
    private const string SampleJson = """
    {
      "metadata": {
        "title": "测试乐曲",
        "artist": "测试艺术家",
        "bpm": 120.0,
        "time_signature": "4/4",
        "instrument": "guitar",
        "key": "C",
        "version": "1.0.0"
      },
      "key_mapping": {
        "C": "A",
        "Dm": "S",
        "Em": "D",
        "F": "F",
        "G": "J",
        "Am": "K",
        "G7": "L"
      },
      "sequence": [
        {
          "time_ms": 0,
          "chord": "C",
          "key": "A",
          "duration_ms": 50
        },
        {
          "time_ms": 500,
          "chord": "G",
          "key": "J",
          "duration_ms": 50
        }
      ]
    }
    """;

    [Fact]
    public async Task ParseFileAsync_ValidJson_ReturnsPerformanceTimeline()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, SampleJson);

            var parser = new PetitScoreParser();
            var timeline = await parser.ParseFileAsync(tempFile);

            Assert.NotNull(timeline);
            Assert.Equal("测试乐曲", timeline.Metadata.Title);
            Assert.Equal("测试艺术家", timeline.Metadata.Artist);
            Assert.Equal(2, timeline.Events.Count);
            Assert.Equal("A", timeline.Events[0].Key);
            Assert.Equal("J", timeline.Events[1].Key);
            Assert.Equal(550, timeline.TotalDurationMs);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task ParseFileAsync_NonExistentFile_ReturnsNull()
    {
        var parser = new PetitScoreParser();
        var timeline = await parser.ParseFileAsync("non_existent_file.json");
        Assert.Null(timeline);
    }

    [Fact]
    public async Task MusicLibraryService_ScanAndLoadScoresAsync_LoadsRealScores()
    {
        var parser = new PetitScoreParser();
        var configService = new BetterPetitPlanet.Core.Config.ConfigService();
        var library = new MusicLibraryService(parser, configService);

        var scores = await library.ScanAndLoadScoresAsync(instrumentFilter: "全部乐器");
        Assert.NotEmpty(scores);
        Assert.True(scores.Count >= 60);
    }
}
