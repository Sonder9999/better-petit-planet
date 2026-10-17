using System;
using System.Windows.Threading;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Overlay;
using BetterPetitPlanet.GameTask.Music.Rendering;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterPetitPlanet.ViewModel.Windows;

public partial class MusicOverlaySettingsViewModel
{
    private readonly MusicPreviewSource _previewSource;
    private DispatcherTimer? _previewTimer;
    private bool _previewSuspended;

    [ObservableProperty] private bool _isPreviewPlaying;

    public string PreviewPlayButtonText => IsPreviewPlaying ? "暂停示例" : "播放示例";
    public MusicPreviewSource PreviewSource => _previewSource;
    private bool PreviewUsesGpuCapabilities => UseGpuRenderer && MusicOverlayPresenter.IsGpuSupportedSkin(SelectedSkinId);
    public string PreviewBackendStatus => PreviewUsesGpuCapabilities
        ? (string.Equals(SelectedSkinId, "default", StringComparison.OrdinalIgnoreCase)
            ? "GPU 外观预览：原生默认皮肤完整特效（含命中爆发、长按粒子及按键反馈）。"
            : (SelectedSkinId != null && SelectedSkinId.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase))
                ? "GPU 外观预览：内置参考皮肤完整特效（含多层背景、命中爆发、长按粒子及按键反馈）。"
                : "GPU 外观预览：自定义皮肤完整特效（含命中爆发、长按粒子及按键反馈）。")
        : UseGpuRenderer ? "此皮肤暂由 WPF 呈现，预览使用 WPF 效果。" : "WPF 外观预览";

    public MusicOverlayEffectsConfig CreatePreviewEffectsConfig()
    {
        return CreateCurrentEffectsConfig();
    }
    public event Action<MusicRenderSnapshot>? SnapshotUpdated;
    public event Action? PreviewParamsChanged;
    internal event Action? PreviewDisposed;

    [RelayCommand]
    public void TogglePreviewPlayback()
    {
        if (_disposed) return;
        if (_previewSource.IsPlaying)
        {
            PausePreview();
            return;
        }
        else
        {
            _previewSource.Start();
            IsPreviewPlaying = true;
            StartPreviewTimer();
        }

        OnPropertyChanged(nameof(PreviewPlayButtonText));
        SnapshotUpdated?.Invoke(_previewSource.CaptureSnapshot());
    }

    public void PausePreview()
    {
        if (_disposed) return;
        PausePreviewCore();
    }

    private void PausePreviewCore()
    {
        bool wasPlaying = _previewSource.IsPlaying || IsPreviewPlaying;
        StopPreviewTimer();
        _previewSource.Pause();
        IsPreviewPlaying = false;
        OnPropertyChanged(nameof(PreviewPlayButtonText));
        if (!_disposed && wasPlaying)
            SnapshotUpdated?.Invoke(_previewSource.CaptureSnapshot());
    }

    private void StartPreviewTimer()
    {
        if (_disposed || _dispatcher == null || _dispatcher.HasShutdownStarted) return;
        if (_previewTimer == null)
        {
            _previewTimer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _previewTimer.Tick += OnPreviewTick;
        }
        _previewTimer.Start();
    }

    private void StopPreviewTimer()
    {
        if (_previewTimer == null) return;
        _previewTimer.Stop();
        _previewTimer.Tick -= OnPreviewTick;
        _previewTimer = null;
    }

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        if (_disposed || !_previewSource.IsPlaying)
        {
            StopPreviewTimer();
            return;
        }
        SnapshotUpdated?.Invoke(_previewSource.CaptureSnapshot());
    }
}
