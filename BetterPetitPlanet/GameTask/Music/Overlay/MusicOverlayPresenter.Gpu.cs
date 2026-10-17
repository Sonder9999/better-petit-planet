using System;
using System.Linq;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Rendering.Gpu;
using BetterPetitPlanet.GameTask.Music.Skins;
using Microsoft.Extensions.Logging;

namespace BetterPetitPlanet.GameTask.Music.Overlay;

public sealed partial class MusicOverlayPresenter
{
    private readonly Func<IMusicOverlayGpuBackend> _gpuBackendFactory;
    private IMusicOverlayGpuBackend? _gpuBackend;
    private bool _gpuVisible;
    private string? _gpuFailure;
    private PerformanceTimeline? _gpuTimeline;
    private MusicChartIndex _gpuChart = MusicChartIndex.Empty;
    private string? _backendDiagnostic;

    public bool IsGpuActive => _gpuVisible;
    public string? BackendDiagnostic => _backendDiagnostic;

    private void SetBackendDiagnostic(string? diagnostic)
    {
        if (_backendDiagnostic == diagnostic) return;
        _backendDiagnostic = diagnostic;
        Changed?.Invoke();
    }

    public static bool IsGpuSupportedSkin(string? skinId)
    {
        if (string.IsNullOrEmpty(skinId) || string.Equals(skinId, "default", StringComparison.OrdinalIgnoreCase))
            return true;
        // Builtin reference skins: validate structural and asset completeness generically.
        if (skinId.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase))
        {
            var def = MusicReferenceSkinCatalog.Find(skinId);
            return IsReferenceSkinGpuCapable(def);
        }
        // Custom skins are supported by GPU.
        return true;
    }

    private static bool IsReferenceSkinGpuCapable(ReferenceSkinDefinition? def)
    {
        if (def == null) return false;

        // 1. Note textures completeness (requires both hold note [0] and tap note [1])
        if (def.NoteTextures == null || def.NoteTextures.Length < 2 ||
            string.IsNullOrWhiteSpace(def.NoteTextures[0]) || string.IsNullOrWhiteSpace(def.NoteTextures[1]))
            return false;

        // 2. Judge point texture completeness
        if (string.IsNullOrWhiteSpace(def.JudgePointTexture))
            return false;

        // 3. Multi-layer background definition completeness
        if (def.Background == null || def.Background.Length == 0 ||
            def.Background.Any(layer => string.IsNullOrWhiteSpace(layer?.Url)))
            return false;

        // 4. Hit burst effect completeness
        if (def.Hit == null || string.IsNullOrWhiteSpace(def.Hit.Url) || def.Hit.FrameCount <= 0)
            return false;

        // 5. Hold effect capabilities: either frame-animation profile (C2) or particle styles (C1)
        if (def.HoldProfile != null)
        {
            if (string.IsNullOrWhiteSpace(def.HoldProfile.NormalUrl) && def.HoldProfile.Layers.Length == 0)
                return false;
        }
        else
        {
            if (def.HoldStyles == null || def.HoldStyles.Length == 0)
                return false;
        }

        return true;
    }

    // Must run before WPF positioning: that helper includes SWP_SHOWWINDOW.
    private bool TryUseGpu(GameWindowState game, MusicOverlayConfig config, bool visible)
    {
        string? fallback = config.Backend != MusicOverlayBackend.Gpu ? null :
            _gpuFailure != null ? "GPU 已回退 WPF：" + _gpuFailure :
            Adjusting ? "调整布局期间使用 WPF；结束调整后恢复 GPU。" :
            !IsGpuSupportedSkin(config.SkinId) || !IsGpuSupportedSkin(_activeSkinId)
                ? "当前皮肤尚不支持 GPU，使用 WPF。选择原生默认皮肤或自定义皮肤可启用 GPU。" : null;
        if (config.Backend != MusicOverlayBackend.Gpu || fallback != null)
        {
            ReleaseGpuBackend();
            SetBackendDiagnostic(fallback);
            return false;
        }

        try
        {
            if (_gpuBackend?.Failure is { } failure) throw failure;
            bool isBuiltin = (!string.IsNullOrEmpty(_activeSkinId) && _activeSkinId.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase)) ||
                (_activeSkin?.Id != null && _activeSkin.Id.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase));
            bool isCustom = _activeSkin is { IsCustom: true } ||
                (!string.IsNullOrEmpty(_activeSkinId) && !_activeSkinId.Equals("default", StringComparison.OrdinalIgnoreCase) && !_activeSkinId.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase));
            var resourceDiagnostic = MusicDefaultSkin.ResourceDiagnostic;
            string capDesc = isBuiltin ? MusicGpuRenderer.ReferenceCapabilityDescription :
                isCustom ? MusicGpuRenderer.CustomCapabilityDescription :
                MusicGpuRenderer.CapabilityDescription;
            SetBackendDiagnostic(capDesc + (resourceDiagnostic == null || isCustom || isBuiltin ? "" : Environment.NewLine + resourceDiagnostic));

            if (_window?.IsVisible == true || _skinResourcesVisible)
            {
                if (_window?.IsVisible == true) _window.Hide();
                _skinResourcesVisible = false;
                _window?.PlayfieldControl.ReleaseSkinResources();
            }
            if (!visible)
            {
                HideGpu();
                return true;
            }

            if (_activeSkin == null || !string.Equals(_activeSkin.Id, _activeSkinId, StringComparison.OrdinalIgnoreCase))
            {
                _ = RestoreSkinAsync(DecodeSize(game));
            }

            _gpuBackend ??= _gpuBackendFactory();
            if (_gpuBackend.Failure is { } startupFailure) throw startupFailure;
            var bounds = game.PhysicalBounds;
            _gpuBackend.SetBounds((int)Math.Round(bounds.X), (int)Math.Round(bounds.Y),
                Math.Max(1, (int)Math.Round(bounds.Width)), Math.Max(1, (int)Math.Round(bounds.Height)));
            PublishGpu(CreateRenderSnapshot(_session.Playback.CaptureSnapshot()));
            if (!_gpuVisible)
            {
                _gpuBackend.Show();
                _gpuVisible = true;
                Changed?.Invoke();
            }
            return true;
        }
        catch (Exception ex)
        {
            FailGpu(ex);
            return false; // Same tick enters the existing WPF show/update path.
        }
    }

    private void PublishGpu(MusicRenderSnapshot snapshot)
    {
        var timeline = _session.Playback.CurrentTimeline;
        if (!ReferenceEquals(_gpuTimeline, timeline))
        {
            _gpuTimeline = timeline;
            _gpuChart = timeline == null ? MusicChartIndex.Empty : new MusicChartIndex(timeline);
            _gpuBackend?.ClearHits();
        }
        _gpuBackend?.Publish(snapshot, _gpuChart, _config.Config.MusicOverlay, _activeSkin);
    }

    private bool TryPublishGpu(MusicSessionSnapshot? snapshot = null)
    {
        if (!_gpuVisible || _gpuBackend == null) return false;
        try
        {
            if (_gpuBackend.Failure is { } failure) throw failure;
            PublishGpu(CreateRenderSnapshot(snapshot ?? _session.Playback.CaptureSnapshot()));
            return true;
        }
        catch (Exception ex)
        {
            FailGpu(ex);
            Tick();
            return false;
        }
    }

    private void FailGpu(Exception exception)
    {
        _gpuFailure = exception.Message;
        _logger?.LogWarning(exception, "GPU music overlay failed; restoring WPF independently of playback");
        ReleaseGpuBackend();
        SetBackendDiagnostic("GPU 已回退 WPF：" + _gpuFailure + "。重新应用设置可重试。");
    }

    private void HideGpu()
    {
        if (!_gpuVisible) return;
        _gpuBackend?.ClearHits();
        _gpuBackend?.Hide();
        _gpuVisible = false;
        LogGpuStatistics();
        Changed?.Invoke();
    }

    private void LogGpuStatistics()
    {
        if (_gpuBackend == null || _logger == null) return;
        try
        {
            var stats = _gpuBackend.Statistics;
            _logger.LogInformation("Music GPU telemetry (CPU/presentation timings, not display FPS): frames={Frames}, recreations={Recreations}, " +
                "wait p50/p95/p99/max={WaitP50}/{WaitP95}/{WaitP99}/{WaitMax:F2} ms (avg={WaitAvg:F2}), " +
                "render p50/p95/p99/max={RenderP50}/{RenderP95}/{RenderP99}/{RenderMax:F2} ms, " +
                "present call p50/p95/p99/max={PresentP50}/{PresentP95}/{PresentP99}/{PresentMax:F2} ms, " +
                "present return interval p50/p95/p99/max={ReturnP50}/{ReturnP95}/{ReturnP99}/{ReturnMax:F2} ms (avg={ReturnAvg:F2}), " +
                "render start interval p50/p95/p99/max={StartP50}/{StartP95}/{StartP99}/{StartMax:F2} ms, " +
                "cadence drops [>8.33ms (~2x@240Hz)={Gt8}, >16.67ms (~4x@240Hz)={Gt16}, >50ms={Gt50}], " +
                "wait reasons [frame={WaitFrame}, wake={WaitWake}, msg={WaitMsg}, timeout={WaitTimeout}, err={WaitErr}], " +
                "anomalies recorded={TotalAnomalies}",
                stats.Frames, stats.DeviceRecreations,
                stats.WaitP50Ms, stats.WaitP95Ms, stats.WaitP99Ms, stats.WaitMaxMs, stats.WaitAvgMs,
                stats.RenderP50Ms, stats.RenderP95Ms, stats.RenderP99Ms, stats.RenderMaxMs,
                stats.PresentP50Ms, stats.PresentP95Ms, stats.PresentP99Ms, stats.PresentMaxMs,
                stats.PresentReturnIntervalP50Ms, stats.PresentReturnIntervalP95Ms, stats.PresentReturnIntervalP99Ms, stats.PresentReturnIntervalMaxMs, stats.PresentReturnIntervalAvgMs,
                stats.RenderStartIntervalP50Ms, stats.RenderStartIntervalP95Ms, stats.RenderStartIntervalP99Ms, stats.RenderStartIntervalMaxMs,
                stats.IntervalGt8_3msCount, stats.IntervalGt16_6msCount, stats.IntervalGt50msCount,
                stats.WaitFrameLatencyCount, stats.WaitWakeHandleCount, stats.WaitWindowMessageCount, stats.WaitTimeoutCount, stats.WaitErrorCount,
                stats.TotalAnomalies);

            if (stats.RecentAnomalies != null && stats.RecentAnomalies.Count > 0)
            {
                int sampleCount = Math.Min(stats.RecentAnomalies.Count, 5);
                for (int i = stats.RecentAnomalies.Count - sampleCount; i < stats.RecentAnomalies.Count; i++)
                {
                    var a = stats.RecentAnomalies[i];
                    _logger.LogWarning("Music GPU cadence anomaly [{Index}]: time={Time:F1}ms, reason={Reason}, wait={Wait:F2}ms, presentReturnInterval={PresentInterval:F2}ms, renderStartInterval={RenderInterval:F2}ms, render={Render:F2}ms, presentCall={Present:F2}ms",
                        i, a.MonotonicMs, a.WaitReason, a.WaitDurationMs, a.PresentReturnIntervalMs, a.RenderStartIntervalMs, a.RenderDurationMs, a.PresentCallDurationMs);
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Unable to read GPU overlay statistics"); }
    }

    private void ReleaseGpuBackend()
    {
        if (_gpuBackend == null) return;
        bool wasVisible = _gpuVisible;
        try { HideGpu(); } catch (Exception ex) { _logger?.LogDebug(ex, "Unable to hide GPU overlay"); }
        if (!wasVisible) LogGpuStatistics();
        try { _gpuBackend.Dispose(); } catch (Exception ex) { _logger?.LogWarning(ex, "Unable to dispose GPU overlay"); }
        _gpuBackend = null;
        _gpuVisible = false;
        _gpuTimeline = null;
        _gpuChart = MusicChartIndex.Empty;
    }
}
