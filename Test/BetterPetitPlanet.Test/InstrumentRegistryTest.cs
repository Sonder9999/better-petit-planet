using System;
using System.IO;
using System.Linq;
using BetterPetitPlanet.GameTask.Music.Instrument;
using BetterPetitPlanet.GameTask.Music.Service;
using Xunit;

namespace BetterPetitPlanet.Test;

public class InstrumentRegistryTest
{
    [Fact]
    public void TestBuiltInProfilesRegistered()
    {
        var registry = new InstrumentRegistry();
        var profiles = registry.GetAllProfiles();

        Assert.NotNull(profiles);
        Assert.True(profiles.Count >= 2);

        // Check Recorder profile
        Assert.True(registry.TryGetProfile("Recorder", out var recorder));
        Assert.NotNull(recorder);
        Assert.Equal("Recorder", recorder.Id);
        Assert.Equal("竖笛", recorder.DisplayName);

        // Check Guitar profile
        Assert.True(registry.TryGetProfile("Guitar", out var guitar));
        Assert.NotNull(guitar);
        Assert.Equal("Guitar", guitar.Id);
        Assert.Equal("吉他", guitar.DisplayName);
    }

    [Fact]
    public void TestProfileLookup_CaseInsensitiveAndAliases()
    {
        var registry = new InstrumentRegistry();

        // Recorder lookup variants
        Assert.True(registry.TryGetProfile("recorder", out var rec1));
        Assert.True(registry.TryGetProfile("RECORDER", out var rec2));
        Assert.True(registry.TryGetProfile("竖笛", out var rec3));
        Assert.Same(rec1, rec2);
        Assert.Same(rec1, rec3);

        // Guitar lookup variants
        Assert.True(registry.TryGetProfile("guitar", out var g1));
        Assert.True(registry.TryGetProfile("GUITAR", out var g2));
        Assert.True(registry.TryGetProfile("吉他", out var g3));
        Assert.Same(g1, g2);
        Assert.Same(g1, g3);

        // Unknown instrument
        Assert.False(registry.TryGetProfile("Violin", out _));
        Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => registry.GetProfile("Violin"));
    }

    [Fact]
    public void TestGuitarProfile_AnchorsAndTargets()
    {
        var registry = new InstrumentRegistry();
        var guitar = registry.GetProfile("Guitar");

        // 7 key anchors
        Assert.Equal(7, guitar.KeyAnchors.Count);
        Assert.Contains(guitar.KeyAnchors, k => k.Name == "key_A" && k.ExpectedText == "A");
        Assert.Contains(guitar.KeyAnchors, k => k.Name == "key_L" && k.ExpectedText == "L");

        // 7 exclusive chord targets
        Assert.Equal(7, guitar.ExclusiveTargets.Count);
        var chordNames = guitar.ExclusiveTargets.Select(t => t.Name).ToList();
        Assert.Equal(["C", "Dm", "Em", "F", "G", "Am", "G7"], chordNames);

        // Total 14 targets
        Assert.Equal(14, guitar.AllTargets.Count);
    }

    [Fact]
    public void TestRecorderProfile_AnchorsAndTargets()
    {
        var registry = new InstrumentRegistry();
        var recorder = registry.GetProfile("Recorder");

        // 7 key anchors
        Assert.Equal(7, recorder.KeyAnchors.Count);

        // 14 exclusive targets (7 numbers + 7 syllables)
        Assert.Equal(14, recorder.ExclusiveTargets.Count);

        // Total 21 targets
        Assert.Equal(21, recorder.AllTargets.Count);
    }

    [Fact]
    public void TestCoordinates_GuitarBaseResolution()
    {
        var registry = new InstrumentRegistry();

        // 7 guitar chord center coordinates at 2560x1440
        Assert.True(registry.TryGetBaseCoordinate("Guitar", "C", out double cx, out double cy));
        Assert.Equal(598.0, cx);
        Assert.Equal(1230.0, cy);

        Assert.True(registry.TryGetBaseCoordinate("Guitar", "Dm", out double dmx, out double dmy));
        Assert.Equal(826.0, dmx);
        Assert.Equal(1230.0, dmy);

        Assert.True(registry.TryGetBaseCoordinate("Guitar", "Em", out double emx, out double emy));
        Assert.Equal(1052.0, emx);
        Assert.Equal(1230.0, emy);

        Assert.True(registry.TryGetBaseCoordinate("Guitar", "F", out double fx, out double fy));
        Assert.Equal(1280.0, fx);
        Assert.Equal(1230.0, fy);

        Assert.True(registry.TryGetBaseCoordinate("Guitar", "G", out double gx, out double gy));
        Assert.Equal(1508.0, gx);
        Assert.Equal(1230.0, gy);

        Assert.True(registry.TryGetBaseCoordinate("Guitar", "Am", out double amx, out double amy));
        Assert.Equal(1734.0, amx);
        Assert.Equal(1230.0, amy);

        Assert.True(registry.TryGetBaseCoordinate("Guitar", "G7", out double g7x, out double g7y));
        Assert.Equal(1960.0, g7x);
        Assert.Equal(1230.0, g7y);

        // Physical key mapping queries also work on Guitar
        Assert.True(registry.TryGetBaseCoordinate("Guitar", "A", out double ax, out _));
        Assert.Equal(598.0, ax);
        Assert.True(registry.TryGetBaseCoordinate("Guitar", "L", out double lx, out _));
        Assert.Equal(1960.0, lx);
    }

    [Fact]
    public void TestCoordinates_Scaling()
    {
        var registry = new InstrumentRegistry();

        // Scale to 1920x1080 (0.75x)
        Assert.True(registry.TryGetScaledCoordinate("Guitar", "C", 1920, 1080, out int cx, out int cy));
        int expectedX = (int)Math.Round(598.0 * 1920.0 / 2560.0);
        int expectedY = (int)Math.Round(1230.0 * 1080.0 / 1440.0);
        Assert.Equal(expectedX, cx);
        Assert.Equal(expectedY, cy);

        // Invalid resolution returns false
        Assert.False(registry.TryGetScaledCoordinate("Guitar", "C", 0, 1080, out _, out _));
        Assert.False(registry.TryGetScaledCoordinate("Guitar", "C", 1920, -1, out _, out _));

        // Invalid key returns false
        Assert.False(registry.TryGetScaledCoordinate("Guitar", "INVALID_KEY", 1920, 1080, out _, out _));
    }

    [Fact]
    public void TestInstrumentCoordinateService_MultiInstrumentIntegration()
    {
        var registry = new InstrumentRegistry();
        var service = new InstrumentCoordinateService(registry);

        // Legacy single-arg calls still default to Recorder
        Assert.True(service.TryGetNoteCoordinate("1", 2560, 1440, out int x1, out int y1));
        Assert.Equal(598, x1);
        Assert.Equal(1230, y1);

        // Multi-arg calls support Guitar
        Assert.True(service.TryGetNoteCoordinate("Guitar", "C", 2560, 1440, out int cgx, out int cgy));
        Assert.Equal(598, cgx);
        Assert.Equal(1230, cgy);

        Assert.True(service.TryGetNoteCoordinate("Guitar", "G7", 2560, 1440, out int g7x, out int g7y));
        Assert.Equal(1960, g7x);
        Assert.Equal(1230, g7y);
    }

    [Fact]
    public void TestProfileValidation_GuitarAndRecorder()
    {
        var registry = new InstrumentRegistry();

        // Validate Guitar profile
        var guitarResult = registry.ValidateProfile("Guitar");
        Assert.NotNull(guitarResult);
        Assert.True(guitarResult.TemplateExists, $"Guitar template missing: {guitarResult.DiagnosisMessage}");
        Assert.Empty(guitarResult.MissingTargets);
        Assert.Empty(guitarResult.CorruptedTargets);
        Assert.True(guitarResult.IsValid, $"Guitar profile validation failed: {guitarResult.DiagnosisMessage}");
        Assert.Equal(14, guitarResult.LoadedTargets.Count);

        // Validate Recorder profile
        var recorderResult = registry.ValidateProfile("Recorder");
        Assert.NotNull(recorderResult);
        Assert.True(recorderResult.TemplateExists, $"Recorder template missing: {recorderResult.DiagnosisMessage}");
        Assert.Empty(recorderResult.MissingTargets);
        Assert.Empty(recorderResult.CorruptedTargets);
        Assert.True(recorderResult.IsValid, $"Recorder profile validation failed: {recorderResult.DiagnosisMessage}");
        Assert.Equal(21, recorderResult.LoadedTargets.Count);
    }

    [Fact]
    public void TestProfileValidation_DiagnosticReportOnMissing()
    {
        var registry = new InstrumentRegistry();

        // Non-existent profile
        var missingProfileResult = registry.ValidateProfile("NonExistent");
        Assert.False(missingProfileResult.IsValid);
        Assert.Contains("未找到乐器档案", missingProfileResult.DiagnosisMessage);

        // Profile with missing files
        var dummyProfile = new InstrumentProfile
        {
            Id = "Dummy",
            DisplayName = "测试乐器",
            TemplateRelativePath = "Assets/Instruments/Dummy/missing_template.png",
            ExclusiveTargets = [
                new("dummy_target", "dummy", 0, 0, 10, 10, "dummy", "missing_target.png", true)
            ]
        };
        registry.RegisterProfile(dummyProfile);

        var dummyResult = registry.ValidateProfile("Dummy");
        Assert.False(dummyResult.IsValid);
        Assert.False(dummyResult.TemplateExists);
        Assert.Contains("missing_target.png", dummyResult.MissingTargets);
        Assert.Contains("缺失", dummyResult.DiagnosisMessage);
    }
}
