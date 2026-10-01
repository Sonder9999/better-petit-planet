using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Model;
using Microsoft.Extensions.Logging;

namespace BetterPetitPlanet.GameTask.Music.Service;

public sealed class MusicLibraryService
{
    private readonly ILogger<MusicLibraryService>? _logger;
    private readonly PetitScoreParser _parser;
    private readonly IConfigService _configService;

    private readonly List<PerformanceTimeline> _cachedScores = [];
    private int _currentIndex = -1;

    public IReadOnlyList<PerformanceTimeline> CachedScores => _cachedScores;
    public int CurrentIndex => _currentIndex;

    public MusicLibraryService(
        PetitScoreParser parser,
        IConfigService configService,
        ILogger<MusicLibraryService>? logger = null)
    {
        _parser = parser;
        _configService = configService;
        _logger = logger;
    }

    public async Task<List<PerformanceTimeline>> ScanAndLoadScoresAsync(string? targetDir = null, string instrumentFilter = "全部乐器")
    {
        _cachedScores.Clear();
        _currentIndex = -1;

        var directory = FindSongsDirectory(targetDir);
        if (directory == null)
        {
            _logger?.LogInformation("Songs directory could not be located");
            return [];
        }

        try
        {
            var files = Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var timeline = await _parser.ParseFileAsync(file);
                if (timeline == null) continue;

                // Instrument filter
                if (instrumentFilter != "全部乐器")
                {
                    bool matchGuitar = instrumentFilter == "吉他" &&
                        (timeline.Metadata.Instrument.Contains("guitar", StringComparison.OrdinalIgnoreCase) ||
                         file.Contains("guitar", StringComparison.OrdinalIgnoreCase));
                    bool matchRecorder = instrumentFilter == "竖笛" &&
                        (timeline.Metadata.Instrument.Contains("recorder", StringComparison.OrdinalIgnoreCase) ||
                         file.Contains("recorder", StringComparison.OrdinalIgnoreCase));

                    if (!matchGuitar && !matchRecorder) continue;
                }

                _cachedScores.Add(timeline);
            }

            _logger?.LogInformation("Scanned {Count} scores in {Dir}", _cachedScores.Count, directory);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to scan songs directory: {Dir}", directory);
        }

        return _cachedScores;
    }

    public PerformanceTimeline? SelectSong(int index)
    {
        if (index >= 0 && index < _cachedScores.Count)
        {
            _currentIndex = index;
            return _cachedScores[index];
        }
        return null;
    }

    public PerformanceTimeline? GetNextSong()
    {
        if (_cachedScores.Count == 0) return null;
        _currentIndex = (_currentIndex + 1) % _cachedScores.Count;
        return _cachedScores[_currentIndex];
    }

    public PerformanceTimeline? GetPreviousSong()
    {
        if (_cachedScores.Count == 0) return null;
        _currentIndex = (_currentIndex - 1 + _cachedScores.Count) % _cachedScores.Count;
        return _cachedScores[_currentIndex];
    }

    public PerformanceTimeline? GetRandomSong(Random? random = null)
    {
        if (_cachedScores.Count == 0) return null;
        if (_cachedScores.Count == 1) return _cachedScores[0];

        var rnd = random ?? Random.Shared;
        int nextIndex;
        do
        {
            nextIndex = rnd.Next(_cachedScores.Count);
        } while (nextIndex == _currentIndex && _cachedScores.Count > 1);

        _currentIndex = nextIndex;
        return _cachedScores[_currentIndex];
    }

    public static string? FindSongsDirectory(string? targetDir = null)
    {
        if (!string.IsNullOrWhiteSpace(targetDir) && Directory.Exists(targetDir))
        {
            return targetDir;
        }

        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            var candidate = Path.Combine(dir, "data", "songs");
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.json", SearchOption.AllDirectories).Any())
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }

        var currentCandidate = Path.Combine(Directory.GetCurrentDirectory(), "data", "songs");
        if (Directory.Exists(currentCandidate) && Directory.EnumerateFiles(currentCandidate, "*.json", SearchOption.AllDirectories).Any())
        {
            return currentCandidate;
        }

        var relativeFallback = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "petit_music", "petit_music", "data", "songs"));
        if (Directory.Exists(relativeFallback))
        {
            return relativeFallback;
        }

        return null;
    }
}
