using System;
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
    public async Task MusicLibraryService_ScanAndLoadScoresAsync_LoadsScoresFromDirectoryFixture()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "BetterPetitPlanet_MusicFixture_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var guitarDir = Path.Combine(tempDir, "guitar");
            var recorderDir = Path.Combine(tempDir, "recorder");
            Directory.CreateDirectory(guitarDir);
            Directory.CreateDirectory(recorderDir);

            var guitarScore = SampleJson;
            var recorderScore = SampleJson
                .Replace("\"guitar\"", "\"recorder\"")
                .Replace("测试乐曲", "竖笛测试乐曲");

            await File.WriteAllTextAsync(Path.Combine(guitarDir, "test_guitar.json"), guitarScore);
            await File.WriteAllTextAsync(Path.Combine(recorderDir, "test_recorder.json"), recorderScore);
            await File.WriteAllTextAsync(Path.Combine(tempDir, "notes.txt"), "Synthetic test score notes");

            var parser = new PetitScoreParser();
            var configService = new BetterPetitPlanet.Core.Config.ConfigService();
            var library = new MusicLibraryService(parser, configService);

            var allScores = await library.ScanAndLoadScoresAsync(targetDir: tempDir, instrumentFilter: "全部乐器");
            Assert.Equal(2, allScores.Count);

            var guitarScores = await library.ScanAndLoadScoresAsync(targetDir: tempDir, instrumentFilter: "吉他");
            Assert.Single(guitarScores);
            Assert.Equal("测试乐曲", guitarScores[0].Metadata.Title);

            var recorderScores = await library.ScanAndLoadScoresAsync(targetDir: tempDir, instrumentFilter: "竖笛");
            Assert.Single(recorderScores);
            Assert.Equal("竖笛测试乐曲", recorderScores[0].Metadata.Title);

            // Test navigation
            Assert.NotNull(library.SelectSong(0));
            Assert.NotNull(library.GetNextSong());
            Assert.NotNull(library.GetPreviousSong());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
