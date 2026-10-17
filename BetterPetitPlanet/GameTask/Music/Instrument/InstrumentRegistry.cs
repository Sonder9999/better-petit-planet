using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BetterPetitPlanet.GameTask.Music.Instrument;

/// <summary>
/// 乐器识别与配置注册表服务实现
/// </summary>
public sealed class InstrumentRegistry : IInstrumentRegistry
{
    private static readonly Lazy<InstrumentRegistry> _defaultInstance = new(() => new InstrumentRegistry());
    public static InstrumentRegistry Default => _defaultInstance.Value;

    private readonly ConcurrentDictionary<string, InstrumentProfile> _profilesById = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _aliasToIdMap = new(StringComparer.OrdinalIgnoreCase);

    public InstrumentRegistry()
    {
        RegisterBuiltInProfiles();
    }

    private void RegisterBuiltInProfiles()
    {
        RegisterProfile(CreateRecorderProfile());
        RegisterProfile(CreateGuitarProfile());
    }

    public static IReadOnlyList<AnchorTarget> CreateCommonKeyAnchors() =>
    [
        new("key_A", "A", 574, 1272, 48, 38, "A", "key_A.png", IsExclusive: false, Category: AnchorCategory.Key),
        new("key_S", "S", 802, 1272, 48, 38, "S", "key_S.png", IsExclusive: false, Category: AnchorCategory.Key),
        new("key_D", "D", 1028, 1272, 48, 38, "D", "key_D.png", IsExclusive: false, Category: AnchorCategory.Key),
        new("key_F", "F", 1256, 1272, 48, 38, "F", "key_F.png", IsExclusive: false, Category: AnchorCategory.Key),
        new("key_J", "J", 1484, 1272, 48, 38, "J", "key_J.png", IsExclusive: false, Category: AnchorCategory.Key),
        new("key_K", "K", 1710, 1272, 48, 38, "K", "key_K.png", IsExclusive: false, Category: AnchorCategory.Key),
        new("key_L", "L", 1936, 1272, 48, 38, "L", "key_L.png", IsExclusive: false, Category: AnchorCategory.Key),
    ];

    private static InstrumentProfile CreateRecorderProfile()
    {
        var keyAnchors = CreateCommonKeyAnchors();

        var exclusiveTargets = new List<AnchorTarget>
        {
            // 7 个数字
            new("1", "1", 571, 1147, 54, 58, "1", "1.png", IsExclusive: true, Category: AnchorCategory.Number),
            new("2", "2", 799, 1147, 54, 58, "2", "2.png", IsExclusive: true, Category: AnchorCategory.Number),
            new("3", "3", 1025, 1147, 54, 58, "3", "3.png", IsExclusive: true, Category: AnchorCategory.Number),
            new("4", "4", 1253, 1147, 54, 58, "4", "4.png", IsExclusive: true, Category: AnchorCategory.Number),
            new("5", "5", 1481, 1147, 54, 58, "5", "5.png", IsExclusive: true, Category: AnchorCategory.Number),
            new("6", "6", 1707, 1147, 54, 58, "6", "6.png", IsExclusive: true, Category: AnchorCategory.Number),
            new("7", "7", 1933, 1147, 54, 58, "7", "7.png", IsExclusive: true, Category: AnchorCategory.Number),

            // 7 个唱名
            new("do", "do", 572, 1214, 52, 32, "do", "do.png", IsExclusive: true, Category: AnchorCategory.Syllable),
            new("re", "re", 800, 1214, 52, 32, "re", "re.png", IsExclusive: true, Category: AnchorCategory.Syllable),
            new("mi", "mi", 1026, 1214, 52, 32, "mi", "mi.png", IsExclusive: true, Category: AnchorCategory.Syllable),
            new("fa", "fa", 1254, 1214, 52, 32, "fa", "fa.png", IsExclusive: true, Category: AnchorCategory.Syllable),
            new("so", "so", 1482, 1214, 52, 32, "so", "so.png", IsExclusive: true, Category: AnchorCategory.Syllable),
            new("la", "la", 1708, 1214, 52, 32, "la", "la.png", IsExclusive: true, Category: AnchorCategory.Syllable),
            new("ti", "ti", 1934, 1214, 52, 32, "ti", "ti.png", IsExclusive: true, Category: AnchorCategory.Syllable),
        };

        var allTargets = new List<AnchorTarget>(exclusiveTargets);
        allTargets.AddRange(keyAnchors);

        var playCoordinates = new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase)
        {
            ["1"] = (598.0, 1230.0),
            ["A"] = (598.0, 1230.0),
            ["KEY_A"] = (598.0, 1230.0),
            ["DO"] = (598.0, 1230.0),

            ["2"] = (826.0, 1230.0),
            ["S"] = (826.0, 1230.0),
            ["KEY_S"] = (826.0, 1230.0),
            ["RE"] = (826.0, 1230.0),

            ["3"] = (1052.0, 1230.0),
            ["D"] = (1052.0, 1230.0),
            ["KEY_D"] = (1052.0, 1230.0),
            ["MI"] = (1052.0, 1230.0),

            ["4"] = (1280.0, 1230.0),
            ["F"] = (1280.0, 1230.0),
            ["KEY_F"] = (1280.0, 1230.0),
            ["FA"] = (1280.0, 1230.0),

            ["5"] = (1508.0, 1230.0),
            ["J"] = (1508.0, 1230.0),
            ["KEY_J"] = (1508.0, 1230.0),
            ["SO"] = (1508.0, 1230.0),

            ["6"] = (1734.0, 1230.0),
            ["K"] = (1734.0, 1230.0),
            ["KEY_K"] = (1734.0, 1230.0),
            ["LA"] = (1734.0, 1230.0),

            ["7"] = (1960.0, 1230.0),
            ["L"] = (1960.0, 1230.0),
            ["KEY_L"] = (1960.0, 1230.0),
            ["TI"] = (1960.0, 1230.0),
        };

        var buttonBounds = new List<(int X, int Y, int W, int H, string Label)>
        {
            (560, 1140, 75, 178, "1 do A"),
            (788, 1140, 75, 178, "2 re S"),
            (1014, 1140, 75, 178, "3 mi D"),
            (1242, 1140, 75, 178, "4 fa F"),
            (1470, 1140, 75, 178, "5 so J"),
            (1696, 1140, 75, 178, "6 la K"),
            (1922, 1140, 75, 178, "7 ti L"),
        };

        return new InstrumentProfile
        {
            Id = "Recorder",
            DisplayName = "竖笛",
            Aliases = ["recorder", "竖笛", "flute"],
            BaseResolution = (2560.0, 1440.0),
            TemplateRelativePath = Path.Combine("Assets", "Instruments", "Recorder", "template.png"),
            TargetsDirectoryRelativePath = Path.Combine("Assets", "Instruments", "Recorder", "targets"),
            KeyAnchors = keyAnchors,
            ExclusiveTargets = exclusiveTargets,
            VerificationAnchors = [exclusiveTargets[0], exclusiveTargets[6]], // "1" 和 "7"
            AllTargets = allTargets,
            PlayCoordinates = playCoordinates,
            ButtonBounds = buttonBounds
        };
    }

    private static InstrumentProfile CreateGuitarProfile()
    {
        var keyAnchors = CreateCommonKeyAnchors();

        var exclusiveTargets = new List<AnchorTarget>
        {
            // 7 个吉他和弦标注小图 (对应 music_guitar.xml / music_guitar_with_keys.xml)
            new("C", "C", 573, 1158, 49, 57, "C", "C.png", IsExclusive: true, Category: AnchorCategory.Chord),
            new("Dm", "Dm", 768, 1158, 114, 57, "Dm", "Dm.png", IsExclusive: true, Category: AnchorCategory.Chord),
            new("Em", "Em", 1000, 1158, 105, 57, "Em", "Em.png", IsExclusive: true, Category: AnchorCategory.Chord),
            new("F", "F", 1259, 1158, 43, 57, "F", "F.png", IsExclusive: true, Category: AnchorCategory.Chord),
            new("G", "G", 1479, 1158, 53, 57, "G", "G.png", IsExclusive: true, Category: AnchorCategory.Chord),
            new("Am", "Am", 1675, 1158, 116, 57, "Am", "Am.png", IsExclusive: true, Category: AnchorCategory.Chord),
            new("G7", "G7", 1913, 1158, 94, 57, "G7", "G7.png", IsExclusive: true, Category: AnchorCategory.Chord),
        };

        var allTargets = new List<AnchorTarget>(exclusiveTargets);
        allTargets.AddRange(keyAnchors);

        var playCoordinates = new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase)
        {
            ["C"] = (598.0, 1230.0),
            ["A"] = (598.0, 1230.0),
            ["KEY_A"] = (598.0, 1230.0),
            ["1"] = (598.0, 1230.0),
            ["DO"] = (598.0, 1230.0),

            ["DM"] = (826.0, 1230.0),
            ["S"] = (826.0, 1230.0),
            ["KEY_S"] = (826.0, 1230.0),
            ["2"] = (826.0, 1230.0),
            ["RE"] = (826.0, 1230.0),

            ["EM"] = (1052.0, 1230.0),
            ["D"] = (1052.0, 1230.0),
            ["KEY_D"] = (1052.0, 1230.0),
            ["3"] = (1052.0, 1230.0),
            ["MI"] = (1052.0, 1230.0),

            ["F"] = (1280.0, 1230.0),
            ["KEY_F"] = (1280.0, 1230.0),
            ["4"] = (1280.0, 1230.0),
            ["FA"] = (1280.0, 1230.0),

            ["G"] = (1508.0, 1230.0),
            ["J"] = (1508.0, 1230.0),
            ["KEY_J"] = (1508.0, 1230.0),
            ["5"] = (1508.0, 1230.0),
            ["SO"] = (1508.0, 1230.0),

            ["AM"] = (1734.0, 1230.0),
            ["K"] = (1734.0, 1230.0),
            ["KEY_K"] = (1734.0, 1230.0),
            ["6"] = (1734.0, 1230.0),
            ["LA"] = (1734.0, 1230.0),

            ["G7"] = (1960.0, 1230.0),
            ["L"] = (1960.0, 1230.0),
            ["KEY_L"] = (1960.0, 1230.0),
            ["7"] = (1960.0, 1230.0),
            ["TI"] = (1960.0, 1230.0),
        };

        var buttonBounds = new List<(int X, int Y, int W, int H, string Label)>
        {
            (560, 1140, 75, 178, "C A"),
            (760, 1140, 130, 178, "Dm S"),
            (990, 1140, 125, 178, "Em D"),
            (1242, 1140, 75, 178, "F F"),
            (1470, 1140, 75, 178, "G J"),
            (1670, 1140, 130, 178, "Am K"),
            (1905, 1140, 115, 178, "G7 L"),
        };

        return new InstrumentProfile
        {
            Id = "Guitar",
            DisplayName = "吉他",
            Aliases = ["guitar", "吉他"],
            BaseResolution = (2560.0, 1440.0),
            TemplateRelativePath = Path.Combine("Assets", "Instruments", "Guitar", "template.png"),
            TargetsDirectoryRelativePath = Path.Combine("Assets", "Instruments", "Guitar", "targets"),
            KeyAnchors = keyAnchors,
            ExclusiveTargets = exclusiveTargets,
            VerificationAnchors = [exclusiveTargets[0], exclusiveTargets[6]], // "C" 和 "G7"
            AllTargets = allTargets,
            PlayCoordinates = playCoordinates,
            ButtonBounds = buttonBounds
        };
    }

    public bool RegisterProfile(InstrumentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            throw new ArgumentException("InstrumentProfile Id cannot be empty.", nameof(profile));
        }

        _profilesById[profile.Id] = profile;
        _aliasToIdMap[profile.Id] = profile.Id;

        if (!string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            _aliasToIdMap[profile.DisplayName] = profile.Id;
        }

        foreach (var alias in profile.Aliases)
        {
            if (!string.IsNullOrWhiteSpace(alias))
            {
                _aliasToIdMap[alias] = profile.Id;
            }
        }

        return true;
    }

    public InstrumentProfile GetProfile(string id)
    {
        if (TryGetProfile(id, out var profile) && profile != null)
        {
            return profile;
        }

        throw new KeyNotFoundException($"InstrumentProfile with id or alias '{id}' was not found.");
    }

    public bool TryGetProfile(string id, out InstrumentProfile? profile)
    {
        profile = null;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var lookup = id.Trim();
        if (_aliasToIdMap.TryGetValue(lookup, out var targetId) && _profilesById.TryGetValue(targetId, out profile))
        {
            return true;
        }

        if (_profilesById.TryGetValue(lookup, out profile))
        {
            return true;
        }

        return false;
    }

    public IReadOnlyCollection<InstrumentProfile> GetAllProfiles()
    {
        return _profilesById.Values.ToList().AsReadOnly();
    }

    public bool TryGetBaseCoordinate(string instrumentId, string keyOrChord, out double baseX, out double baseY)
    {
        baseX = 0;
        baseY = 0;

        if (string.IsNullOrWhiteSpace(keyOrChord))
        {
            return false;
        }

        // If instrumentId is empty, try default "Recorder"
        var effectiveInstrumentId = string.IsNullOrWhiteSpace(instrumentId) ? "Recorder" : instrumentId;
        if (!TryGetProfile(effectiveInstrumentId, out var profile) || profile == null)
        {
            return false;
        }

        var key = keyOrChord.Trim().ToUpperInvariant();
        if (profile.PlayCoordinates.TryGetValue(key, out var point))
        {
            baseX = point.X;
            baseY = point.Y;
            return true;
        }

        return false;
    }

    public bool TryGetScaledCoordinate(string instrumentId, string keyOrChord, int clientWidth, int clientHeight, out int clientX, out int clientY)
    {
        clientX = 0;
        clientY = 0;

        if (clientWidth <= 0 || clientHeight <= 0)
        {
            return false;
        }

        var effectiveInstrumentId = string.IsNullOrWhiteSpace(instrumentId) ? "Recorder" : instrumentId;
        if (!TryGetProfile(effectiveInstrumentId, out var profile) || profile == null)
        {
            return false;
        }

        if (!TryGetBaseCoordinate(effectiveInstrumentId, keyOrChord, out double baseX, out double baseY))
        {
            return false;
        }

        double scaleX = clientWidth / profile.BaseResolution.Width;
        double scaleY = clientHeight / profile.BaseResolution.Height;

        clientX = (int)Math.Round(baseX * scaleX);
        clientY = (int)Math.Round(baseY * scaleY);
        return true;
    }

    public string? ResolveTemplatePath(string instrumentId, string? baseDirectory = null)
    {
        if (!TryGetProfile(instrumentId, out var profile) || profile == null)
        {
            return null;
        }

        var candidateDirs = GetCandidateSearchDirectories(baseDirectory);
        foreach (var dir in candidateDirs)
        {
            // 1. Direct relative path from profile
            var path1 = Path.Combine(dir, profile.TemplateRelativePath);
            if (File.Exists(path1)) return path1;

            // 2. Relative to Assets/Instruments/{profile.Id}/template.png
            var path2 = Path.Combine(dir, "Assets", "Instruments", profile.Id, "template.png");
            if (File.Exists(path2)) return path2;

            // 3. In dir/Instruments/{profile.Id}/template.png
            var path3 = Path.Combine(dir, "Instruments", profile.Id, "template.png");
            if (File.Exists(path3)) return path3;

            // 4. In dir/{profile.Id}/template.png
            var path4 = Path.Combine(dir, profile.Id, "template.png");
            if (File.Exists(path4)) return path4;
        }

        return null;
    }

    public string? ResolveTargetFilePath(string instrumentId, string targetFileName, string? baseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(targetFileName))
        {
            return null;
        }

        var effectiveInstrumentId = string.IsNullOrWhiteSpace(instrumentId) ? "Recorder" : instrumentId;
        TryGetProfile(effectiveInstrumentId, out var profile);

        var instrumentName = profile?.Id ?? effectiveInstrumentId;
        var candidateDirs = GetCandidateSearchDirectories(baseDirectory);

        foreach (var dir in candidateDirs)
        {
            // 1. Check instrument specific targets directory
            var path1 = Path.Combine(dir, "Assets", "Instruments", instrumentName, "targets", targetFileName);
            if (File.Exists(path1)) return path1;

            var path2 = Path.Combine(dir, "Instruments", instrumentName, "targets", targetFileName);
            if (File.Exists(path2)) return path2;

            var path3 = Path.Combine(dir, instrumentName, "targets", targetFileName);
            if (File.Exists(path3)) return path3;

            // 2. Check common directory (key_A.png ... key_L.png directly in Assets/Instruments/common)
            var pathCommon1 = Path.Combine(dir, "Assets", "Instruments", "common", targetFileName);
            if (File.Exists(pathCommon1)) return pathCommon1;

            var pathCommon2 = Path.Combine(dir, "Instruments", "common", targetFileName);
            if (File.Exists(pathCommon2)) return pathCommon2;

            var pathCommon3 = Path.Combine(dir, "common", targetFileName);
            if (File.Exists(pathCommon3)) return pathCommon3;

            var pathCommonTargets = Path.Combine(dir, "Assets", "Instruments", "common", "targets", targetFileName);
            if (File.Exists(pathCommonTargets)) return pathCommonTargets;

            // 4. Check Recorder targets fallback (historical compatibility)
            var pathRecorder = Path.Combine(dir, "Assets", "Instruments", "Recorder", "targets", targetFileName);
            if (File.Exists(pathRecorder)) return pathRecorder;
        }

        return null;
    }

    public InstrumentValidationResult ValidateProfile(string id, string? baseDirectory = null)
    {
        if (!TryGetProfile(id, out var profile) || profile == null)
        {
            return new InstrumentValidationResult
            {
                InstrumentId = id,
                IsValid = false,
                DiagnosisMessage = $"未找到乐器档案 '{id}'"
            };
        }

        var result = new InstrumentValidationResult
        {
            InstrumentId = profile.Id,
            DisplayName = profile.DisplayName
        };

        // 1. 验证大底模板
        var templatePath = ResolveTemplatePath(profile.Id, baseDirectory);
        result.TemplatePath = templatePath;
        if (templatePath != null && File.Exists(templatePath))
        {
            try
            {
                var fi = new FileInfo(templatePath);
                if (fi.Length > 0)
                {
                    result.TemplateExists = true;
                }
                else
                {
                    result.TemplateExists = false;
                    result.CorruptedTargets.Add($"template: {templatePath} (0 bytes)");
                }
            }
            catch (Exception ex)
            {
                result.TemplateExists = false;
                result.CorruptedTargets.Add($"template: {templatePath} ({ex.Message})");
            }
        }
        else
        {
            result.TemplateExists = false;
        }

        // 2. 验证各个锚点小图目标
        foreach (var target in profile.AllTargets)
        {
            var fileName = target.TemplateFileName;
            var filePath = ResolveTargetFilePath(profile.Id, fileName, baseDirectory);
            if (filePath != null && File.Exists(filePath))
            {
                try
                {
                    var fi = new FileInfo(filePath);
                    if (fi.Length > 0)
                    {
                        result.LoadedTargets.Add(fileName);
                    }
                    else
                    {
                        result.CorruptedTargets.Add($"{fileName} (0 bytes at {filePath})");
                    }
                }
                catch (Exception ex)
                {
                    result.CorruptedTargets.Add($"{fileName} (Error: {ex.Message})");
                }
            }
            else
            {
                result.MissingTargets.Add(fileName);
            }
        }

        // 综合有效性评估
        result.IsValid = result.TemplateExists && result.MissingTargets.Count == 0 && result.CorruptedTargets.Count == 0;

        if (result.IsValid)
        {
            result.DiagnosisMessage = $"乐器 '{profile.DisplayName}' 资源校验通过：模板存在，{result.LoadedTargets.Count} 个锚点模板均有效。";
        }
        else
        {
            var issues = new List<string>();
            if (!result.TemplateExists)
            {
                issues.Add($"大底模板缺失或损坏 (预期相对路径: {profile.TemplateRelativePath})");
            }
            if (result.MissingTargets.Count > 0)
            {
                issues.Add($"缺失 {result.MissingTargets.Count} 个模板小图: [{string.Join(", ", result.MissingTargets)}]");
            }
            if (result.CorruptedTargets.Count > 0)
            {
                issues.Add($"损坏 {result.CorruptedTargets.Count} 个模板文件: [{string.Join(", ", result.CorruptedTargets)}]");
            }
            result.DiagnosisMessage = $"乐器 '{profile.DisplayName}' 资源存在问题：{string.Join("；", issues)}";
        }

        return result;
    }

    public IReadOnlyList<InstrumentValidationResult> ValidateAllProfiles(string? baseDirectory = null)
    {
        return _profilesById.Values
            .Select(profile => ValidateProfile(profile.Id, baseDirectory))
            .ToList();
    }

    private static List<string> GetCandidateSearchDirectories(string? baseDirectory)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(baseDirectory) && Directory.Exists(baseDirectory))
        {
            dirs.Add(baseDirectory);
        }

        var appBase = AppContext.BaseDirectory;
        dirs.Add(appBase);

        // Project source fallbacks for development/testing
        try
        {
            dirs.Add(Path.GetFullPath(Path.Combine(appBase, "..", "..", "..")));
            dirs.Add(Path.GetFullPath(Path.Combine(appBase, "..", "..", "..", "..", "BetterPetitPlanet")));
            dirs.Add(Path.GetFullPath(Path.Combine(appBase, "..", "..", "..", "BetterPetitPlanet")));
        }
        catch
        {
            // Ignore path resolution errors
        }

        // Unique existing directories
        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
    }
}
