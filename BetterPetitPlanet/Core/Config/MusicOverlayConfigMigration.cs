using System;
using Microsoft.Extensions.Logging;
using BetterPetitPlanet.GameTask.Music.Service;

namespace BetterPetitPlanet.Core.Config;

public static class MusicOverlayConfigMigration
{
    public const int CurrentConfigVersion = 1;

    public static MusicOverlayConfig Migrate(MusicOverlayConfig? source, ILogger? logger = null)
    {
        if (source == null)
        {
            return new MusicOverlayConfig();
        }

        var migrated = new MusicOverlayConfig
        {
            Enabled = source.Enabled,
            Backend = Enum.IsDefined(source.Backend) ? source.Backend : MusicOverlayBackend.Wpf,
            SkinId = string.IsNullOrWhiteSpace(source.SkinId) ? "default" : source.SkinId,
            FallDurationMs = Math.Clamp(source.FallDurationMs is >= 500 and <= 10000 ? source.FallDurationMs : 3000, 500, 10000),
            VisualOffsetMs = Math.Clamp(source.VisualOffsetMs, -2000, 2000),
            AssetDirectory = string.IsNullOrWhiteSpace(source.AssetDirectory) ? "Assets/MusicOverlay" : source.AssetDirectory,
            ReadySuccessCount = source.ReadySuccessCount is >= 1 and <= 20 ? source.ReadySuccessCount : 2,
            ReadyFailureCount = source.ReadyFailureCount is >= 1 and <= 20 ? source.ReadyFailureCount : 3,
            DetectionFreshnessMs = source.DetectionFreshnessMs is >= 200 and <= 30000 ? source.DetectionFreshnessMs : 2000,
            CaptureFreshnessMs = source.CaptureFreshnessMs is >= 200 and <= 30000 ? source.CaptureFreshnessMs : 2000,
            SnapshotIntervalMs = source.SnapshotIntervalMs is >= 16 and <= 1000 ? source.SnapshotIntervalMs : 50,
            WindowPollIntervalMs = source.WindowPollIntervalMs is >= 20 and <= 1000 ? source.WindowPollIntervalMs : 100
        };

        if (source.Layout != null)
        {
            try
            {
                source.Layout.Validate();
                migrated.Layout = new MusicOverlayLayoutConfig
                {
                    X = source.Layout.X,
                    Y = source.Layout.Y,
                    Width = source.Layout.Width,
                    Height = source.Layout.Height,
                    Scale = source.Layout.Scale,
                    JudgementLine = source.Layout.JudgementLine
                };
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Invalid MusicOverlay.Layout reset to default");
                migrated.Layout = new MusicOverlayLayoutConfig();
            }
        }

        if (source.Effects != null)
        {
            try
            {
                source.Effects.Validate();
                migrated.Effects = new MusicOverlayEffectsConfig
                {
                    HitBursts = source.Effects.HitBursts,
                    HoldGlow = source.Effects.HoldGlow,
                    SyncLines = source.Effects.SyncLines,
                    LaneGuides = source.Effects.LaneGuides,
                    ShowKeyLabels = source.Effects.ShowKeyLabels,
                    Intensity = source.Effects.Intensity,
                    ShowBackgroundDeco = source.Effects.ShowBackgroundDeco,
                    BackgroundOpacity = source.Effects.BackgroundOpacity
                };
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Invalid MusicOverlay.Effects reset to default");
                migrated.Effects = new MusicOverlayEffectsConfig();
            }
        }

        return migrated;
    }

    public static MusicReadinessOptions ToReadinessOptions(MusicOverlayConfig config)
    {
        return new MusicReadinessOptions
        {
            ScreenshotMaxAge = TimeSpan.FromMilliseconds(config.CaptureFreshnessMs),
            DetectionMaxAge = TimeSpan.FromMilliseconds(config.DetectionFreshnessMs),
            ReadySuccessCount = config.ReadySuccessCount,
            ReadyFailureCount = config.ReadyFailureCount,
            SnapshotIntervalMs = config.SnapshotIntervalMs,
            WindowPollIntervalMs = config.WindowPollIntervalMs
        };
    }
}
