using System;
using System.IO;
using System.Threading.Tasks;
using BetterPetitPlanet.Core.Config;
using Newtonsoft.Json;
using Xunit;

namespace BetterPetitPlanet.Test;

public sealed class MusicOverlayConfigMigrationTest : IDisposable
{
    private readonly string _testDirectory;

    public MusicOverlayConfigMigrationTest()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "BPP_ConfigTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, true);
            }
        }
        catch
        {
            // Ignore cleanup failure in temp directory
        }
    }

    [Fact]
    public void DefaultConfig_HasValidNativeOverlayDefaults()
    {
        var config = new MusicOverlayConfig();
        config.Validate();

        Assert.False(config.Enabled);
        Assert.Equal("default", config.SkinId);
        Assert.Equal(3000, config.FallDurationMs);
        Assert.Equal(0, config.VisualOffsetMs);
        Assert.Equal("Assets/MusicOverlay", config.AssetDirectory);
        Assert.Equal(1.0, config.Layout.Width);
        Assert.Equal(0.78, config.Layout.Height);
        Assert.True(config.Effects.HitBursts);
    }

    [Fact]
    public void Migration_NormalizesInvalidValues_PreservingValidFields()
    {
        var invalid = new MusicOverlayConfig
        {
            Enabled = true,
            SkinId = "   ",
            FallDurationMs = -9999,
            VisualOffsetMs = 99999,
            AssetDirectory = string.Empty
        };

        var migrated = MusicOverlayConfigMigration.Migrate(invalid);
        migrated.Validate();

        Assert.True(migrated.Enabled);
        Assert.Equal("default", migrated.SkinId);
        Assert.Equal(3000, migrated.FallDurationMs);
        Assert.Equal(2000, migrated.VisualOffsetMs); // Clamped to valid range
        Assert.Equal("Assets/MusicOverlay", migrated.AssetDirectory);
    }

    [Fact]
    public void Migration_FallsBackInvalidLayoutAndEffects_WithoutLosingOtherConfig()
    {
        var invalid = new MusicOverlayConfig
        {
            Enabled = true,
            SkinId = "custom-skin",
            Layout = new MusicOverlayLayoutConfig
            {
                X = 0.8,
                Width = 0.5 // X + Width = 1.3 > 1 (invalid)
            },
            Effects = new MusicOverlayEffectsConfig
            {
                Intensity = 5.0 // Out of 0..1 range (invalid)
            }
        };

        var migrated = MusicOverlayConfigMigration.Migrate(invalid);
        migrated.Validate();

        Assert.True(migrated.Enabled);
        Assert.Equal("custom-skin", migrated.SkinId);
        // Layout and Effects should be safely reset to valid defaults
        Assert.Equal(0.0, migrated.Layout.X);
        Assert.Equal(1.0, migrated.Layout.Width);
        Assert.Equal(1.0, migrated.Effects.Intensity);
    }

    [Fact]
    public void ToReadinessOptions_MapsFreshnessAndDebounceProperties()
    {
        var config = new MusicOverlayConfig
        {
            CaptureFreshnessMs = 1500,
            DetectionFreshnessMs = 2500,
            ReadySuccessCount = 4,
            ReadyFailureCount = 5,
            SnapshotIntervalMs = 60,
            WindowPollIntervalMs = 120
        };

        var options = MusicOverlayConfigMigration.ToReadinessOptions(config);
        options.Validate();

        Assert.Equal(TimeSpan.FromMilliseconds(1500), options.ScreenshotMaxAge);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), options.DetectionMaxAge);
        Assert.Equal(4, options.ReadySuccessCount);
        Assert.Equal(5, options.ReadyFailureCount);
        Assert.Equal(60, options.SnapshotIntervalMs);
        Assert.Equal(120, options.WindowPollIntervalMs);
    }

    [Fact]
    public async Task ConfigService_ReadsWritesAndMigratesAcrossTemporaryFileAtomicSaves()
    {
        // Write a legacy config JSON with old overlay fields
        string relativeConfig = Path.Combine("config", "test_config_" + Guid.NewGuid().ToString("N") + ".json");
        string fullConfigPath = Path.Combine(AppContext.BaseDirectory, relativeConfig);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullConfigPath)!);
            var legacyJson = @"{
  ""MusicOverlay"": {
    ""Enabled"": true,
    ""SkinId"": ""classic"",
    ""FallDurationMs"": 2000,
    ""VisualOffsetMs"": 50,
    ""AssetDirectory"": ""Assets/MusicOverlay""
  },
  ""Music"": {
    ""AutomaticPlaybackEnabled"": true,
    ""PreparationMs"": 1500
  }
}";
            await File.WriteAllTextAsync(fullConfigPath, legacyJson);

            var service = new ConfigService(configRelativePath: relativeConfig);
            Assert.True(service.Config.MusicOverlay.Enabled);
            Assert.Equal("classic", service.Config.MusicOverlay.SkinId);
            Assert.Equal(2000, service.Config.MusicOverlay.FallDurationMs);
            Assert.Equal(50, service.Config.MusicOverlay.VisualOffsetMs);
            Assert.Equal(1500, service.Config.Music.PreparationMs);

            // Modify and save
            service.Config.MusicOverlay.FallDurationMs = 2500;
            service.Config.Music.PreparationMs = 2000;
            await service.SaveAsync();

            // Verify temporary file is cleaned up after atomic save
            Assert.False(File.Exists(fullConfigPath + ".tmp"));
            Assert.True(File.Exists(fullConfigPath));

            // Reload with a fresh service instance
            var reloadedService = new ConfigService(configRelativePath: relativeConfig);
            Assert.Equal(2500, reloadedService.Config.MusicOverlay.FallDurationMs);
            Assert.Equal(2000, reloadedService.Config.Music.PreparationMs);
        }
        finally
        {
            try
            {
                if (File.Exists(fullConfigPath)) File.Delete(fullConfigPath);
                if (File.Exists(fullConfigPath + ".tmp")) File.Delete(fullConfigPath + ".tmp");
                if (File.Exists(fullConfigPath + ".bak")) File.Delete(fullConfigPath + ".bak");
            }
            catch { }
        }
    }
}
