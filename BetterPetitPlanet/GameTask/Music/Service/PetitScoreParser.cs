using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BetterPetitPlanet.GameTask.Music.Model;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace BetterPetitPlanet.GameTask.Music.Service;

public sealed partial class PetitScoreParser
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

            var events = ScoreNormalizer.Normalize(score);
            int totalDuration = events.Max(e => checked(e.TimeMs + e.DurationMs));

            var sections = ExtractSections(events, totalDuration);

            return new PerformanceTimeline
            {
                Metadata = score.Metadata,
                Events = events,
                Sections = sections,
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

}
