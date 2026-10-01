using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BetterPetitPlanet.GameTask.Music.Model;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace BetterPetitPlanet.GameTask.Music.Service;

public sealed class PetitScoreParser
{
    private readonly ILogger<PetitScoreParser>? _logger;

    public PetitScoreParser(ILogger<PetitScoreParser>? logger = null)
    {
        _logger = logger;
    }

    public async Task<PerformanceTimeline?> ParseFileAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            _logger?.LogWarning("Score file does not exist: {Path}", filePath);
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var score = JsonConvert.DeserializeObject<PetitSongScore>(json);
            if (score == null || score.Sequence.Count == 0)
            {
                _logger?.LogWarning("Score file is empty or invalid format: {Path}", filePath);
                return null;
            }

            var events = score.Sequence
                .OrderBy(e => e.TimeMs)
                .Select(e => new PerformanceEvent
                {
                    TimeMs = e.TimeMs,
                    Key = ResolveKey(e.Key, e.Chord, score.KeyMapping),
                    Chord = e.Chord,
                    DurationMs = Math.Clamp(e.DurationMs, 20, 500),
                    Lyric = e.Lyric
                })
                .Where(e => !string.IsNullOrEmpty(e.Key))
                .ToList();

            int totalDuration = events.Count > 0
                ? events.Max(e => e.TimeMs + e.DurationMs)
                : 0;

            return new PerformanceTimeline
            {
                Metadata = score.Metadata,
                Events = events,
                TotalDurationMs = totalDuration,
                FilePath = filePath
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to parse score file: {Path}", filePath);
            return null;
        }
    }

    private static string ResolveKey(string rawKey, string chord, Dictionary<string, string> keyMapping)
    {
        if (!string.IsNullOrWhiteSpace(rawKey))
        {
            return rawKey.Trim().ToUpperInvariant();
        }

        if (!string.IsNullOrWhiteSpace(chord) && keyMapping.TryGetValue(chord, out var mappedKey))
        {
            return mappedKey.Trim().ToUpperInvariant();
        }

        return string.Empty;
    }
}
