using System;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Rendering.Gpu;
using BetterPetitPlanet.MusicOverlay.Gpu;

using BetterPetitPlanet.GameTask.Music.Skins;

namespace BetterPetitPlanet.GameTask.Music.Overlay;

/// <summary>Presenter boundary; publishing never depends on WPF rendering callbacks.</summary>
public interface IMusicOverlayGpuBackend : IDisposable
{
    Exception? Failure { get; }
    GpuOverlayStatistics Statistics { get; }
    void Publish(MusicRenderSnapshot snapshot, MusicChartIndex chart, MusicOverlayConfig config, MusicSkin? skin = null);
    void PostHitEvent(in MusicGpuHitEvent hit);
    void ClearHits();
    void SetBounds(int x, int y, int width, int height);
    void Show();
    void Hide();
}

internal sealed class MusicOverlayGpuBackend : IMusicOverlayGpuBackend
{
    private readonly MusicGpuRenderer _renderer = new();
    private readonly GpuOverlayHost _host;

    public MusicOverlayGpuBackend() => _host = new GpuOverlayHost(_renderer);
    public Exception? Failure => _host.Failure;
    public GpuOverlayStatistics Statistics => _host.Statistics;
    public void Publish(MusicRenderSnapshot snapshot, MusicChartIndex chart, MusicOverlayConfig config, MusicSkin? skin = null)
        => _renderer.Publish(snapshot, chart, config, skin);
    public void PostHitEvent(in MusicGpuHitEvent hit) => _renderer.EnqueueHit(hit);
    public void ClearHits() => _renderer.ClearHits();
    public void SetBounds(int x, int y, int width, int height) => _host.SetBounds(x, y, width, height);
    public void Show() => _host.Show();
    public void Hide() => _host.Hide();
    public void Dispose() => _host.Dispose();
}
