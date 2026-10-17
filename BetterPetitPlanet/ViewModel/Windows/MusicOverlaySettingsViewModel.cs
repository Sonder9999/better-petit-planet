using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Overlay;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Skins;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterPetitPlanet.ViewModel.Windows;

public partial class MusicOverlaySettingsViewModel : ObservableObject, IDisposable
{
    private readonly IConfigService _configService;
    private readonly MusicOverlayPresenter? _presenter;
    private readonly Dispatcher? _dispatcher;
    private bool _isUpdatingInternally;
    private bool _isApplying;
    private bool _disposed;

    // --- Draft: Basic Overlay Settings ---
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _useGpuRenderer;
    [ObservableProperty] private int _fallDurationMs = 3000;
    [ObservableProperty] private int _visualOffsetMs;

    // --- Draft: Layout Settings ---
    [ObservableProperty] private double _layoutX;
    [ObservableProperty] private double _layoutY;
    [ObservableProperty] private double _layoutWidth = 1.0;
    [ObservableProperty] private double _layoutHeight = 0.78;
    [ObservableProperty] private double _layoutScale = 1.0;
    [ObservableProperty] private double _judgementLine = 0.95;

    // --- Draft: Effects Settings ---
    [ObservableProperty] private bool _hitBursts = true;
    [ObservableProperty] private bool _holdGlow = true;
    [ObservableProperty] private bool _syncLines = true;
    [ObservableProperty] private bool _laneGuides = true;
    [ObservableProperty] private bool _showKeyLabels = true;
    [ObservableProperty] private double _intensity = 1.0;
    [ObservableProperty] private bool _showBackgroundDeco = true;
    [ObservableProperty] private double _backgroundOpacity = 0.8;

    public bool KeyLabels { get => ShowKeyLabels; set => ShowKeyLabels = value; }
    public bool BackgroundEnabled { get => ShowBackgroundDeco; set => ShowBackgroundDeco = value; }

    // --- State & Validation ---
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _hasValidationError;
    [ObservableProperty] private string? _validationError;
    [ObservableProperty] private string _statusMessage = "就绪";
    [ObservableProperty] private bool _isAdjusting;

    public string AdjustButtonText => IsAdjusting ? "结束调整" : "调整位置";

    // --- Read-only Hotkey Summary ---
    public string HotkeyAutoPick => _configService.Config.Hotkey.ToggleAutoPickHotkey;
    public string HotkeyAutoPlay => _configService.Config.Hotkey.ToggleMusicHotkey;
    public string HotkeyTimeline => _configService.Config.Hotkey.ToggleMusicTimelineHotkey;
    public string HotkeyOverlay => _configService.Config.Hotkey.ToggleMusicOverlayHotkey;
    public string HotkeyPrevSong => _configService.Config.Hotkey.PreviousSongHotkey;
    public string HotkeyNextSong => _configService.Config.Hotkey.NextSongHotkey;

    internal bool IsDisposed => _disposed;

    public MusicOverlaySettingsViewModel(
        IConfigService configService,
        MusicOverlayPresenter? presenter = null,
        Func<MusicPreviewSource>? previewSourceFactory = null,
        Dispatcher? dispatcher = null,
        MusicSkinRepository? skinRepository = null,
        Func<string, Task<MusicSkin>>? skinLoader = null)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _presenter = presenter;
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.FromThread(Thread.CurrentThread) ?? Dispatcher.CurrentDispatcher;
        _previewSource = previewSourceFactory != null ? previewSourceFactory() : new MusicPreviewSource();
        _skinRepository = skinRepository ?? new MusicSkinRepository();
        _skinPackageImporter = new MusicSkinPackageImporter(_skinRepository);
        _loadSkin = skinLoader == null
            ? (id, token) => _skinRepository.LoadSkinResultAsync(id, MusicSkinDecodeSize.Preview, token)
            : async (id, _) => new MusicSkinLoadResult(id, await skinLoader(id));
        if (_presenter != null) _presenter.Changed += OnPresenterChanged;

        LoadFromConfig();
        _previewSource.Seek(3000.0);
    }

    public void LoadFromConfig()
    {
        _isUpdatingInternally = true;
        try
        {
            ReloadAvailableSkins();
            var overlay = _configService.Config.MusicOverlay;
            Enabled = overlay.Enabled;
            UseGpuRenderer = overlay.Backend == MusicOverlayBackend.Gpu;
            SelectedSkinId = overlay.SkinId ?? "default";
            SelectedSkinInfo = FindSkinInfo(SelectedSkinId);
            _ = LoadSelectedSkinAsync(SelectedSkinId);
            FallDurationMs = overlay.FallDurationMs;
            VisualOffsetMs = overlay.VisualOffsetMs;
            ApplyLayout(overlay.Layout ?? new());
            ApplyEffects(overlay.Effects ?? new());
            IsDirty = false;
            HasValidationError = false;
            ValidationError = null;
            StopAutoSaveTimer();
            StatusMessage = "当前设置已与配置同步";
        }
        finally
        {
            _isUpdatingInternally = false;
        }

        PreviewParamsChanged?.Invoke();
    }

    public void RefreshLayoutFromConfig()
    {
        _isUpdatingInternally = true;
        try { ApplyLayout(_configService.Config.MusicOverlay.Layout ?? new()); }
        finally { _isUpdatingInternally = false; }
        OnDraftChanged();
    }

    private void ApplyLayout(MusicOverlayLayoutConfig layout)
    {
        LayoutX = layout.X; LayoutY = layout.Y; LayoutWidth = layout.Width;
        LayoutHeight = layout.Height; LayoutScale = layout.Scale; JudgementLine = layout.JudgementLine;
    }

    private void ApplyEffects(MusicOverlayEffectsConfig effects)
    {
        HitBursts = effects.HitBursts; HoldGlow = effects.HoldGlow;
        SyncLines = effects.SyncLines; LaneGuides = effects.LaneGuides;
        ShowKeyLabels = effects.ShowKeyLabels; Intensity = effects.Intensity;
        ShowBackgroundDeco = effects.ShowBackgroundDeco; BackgroundOpacity = effects.BackgroundOpacity;
    }

    partial void OnEnabledChanged(bool value) => OnDraftChanged();
    partial void OnUseGpuRendererChanged(bool value)
    {
        OnPropertyChanged(nameof(PreviewBackendStatus));
        OnDraftChanged();
    }
    partial void OnFallDurationMsChanged(int value) => OnDraftChanged();
    partial void OnVisualOffsetMsChanged(int value) => OnDraftChanged();
    partial void OnLayoutXChanged(double value) => OnDraftChanged();
    partial void OnLayoutYChanged(double value) => OnDraftChanged();
    partial void OnLayoutWidthChanged(double value) => OnDraftChanged();
    partial void OnLayoutHeightChanged(double value) => OnDraftChanged();
    partial void OnLayoutScaleChanged(double value) => OnDraftChanged();
    partial void OnJudgementLineChanged(double value) => OnDraftChanged();
    partial void OnHitBurstsChanged(bool value) => OnDraftChanged();
    partial void OnHoldGlowChanged(bool value) => OnDraftChanged();
    partial void OnSyncLinesChanged(bool value) => OnDraftChanged();
    partial void OnLaneGuidesChanged(bool value) => OnDraftChanged();
    partial void OnIntensityChanged(double value) => OnDraftChanged();
    partial void OnBackgroundOpacityChanged(double value) => OnDraftChanged();
    partial void OnShowKeyLabelsChanged(bool value) { OnPropertyChanged(nameof(KeyLabels)); OnDraftChanged(); }
    partial void OnShowBackgroundDecoChanged(bool value) { OnPropertyChanged(nameof(BackgroundEnabled)); OnDraftChanged(); }

    private void OnDraftChanged()
    {
        if (_isUpdatingInternally || _disposed) return;

        IsDirty = ComputeIsDirty();

        string? error = ValidateDraft();
        if (error != null)
        {
            StopAutoSaveTimer();
            HasValidationError = true;
            ValidationError = error;
            StatusMessage = "编辑值暂时无效，预览保留上一次有效设置";
            return;
        }

        HasValidationError = false;
        ValidationError = null;
        PreviewParamsChanged?.Invoke();

        if (IsDirty)
        {
            StatusMessage = "设置已更改，即将自动保存…";
            StartOrResetAutoSaveTimer();
        }
        else
        {
            StopAutoSaveTimer();
            StatusMessage = "当前设置已与配置同步";
        }
    }

    public string? ValidateDraft()
    {
        if (string.IsNullOrWhiteSpace(SelectedSkinId) || SelectedSkinId.Length > 128) return "皮肤标识无效";
        if (FallDurationMs is < 500 or > 10000) return "提前显示时长必须在 500–10000 毫秒之间";
        if (VisualOffsetMs is < -2000 or > 2000) return "视觉偏移必须在 -2000–2000 毫秒之间";
        if (!double.IsFinite(LayoutX) || LayoutX is < 0 or > 1) return "水平位置 X 必须在 0.0–1.0 之间";
        if (!double.IsFinite(LayoutY) || LayoutY is < 0 or > 1) return "垂直位置 Y 必须在 0.0–1.0 之间";
        if (!double.IsFinite(LayoutWidth) || LayoutWidth is < 0.1 or > 1) return "轨道宽度必须在 0.1–1.0 之间";
        if (!double.IsFinite(LayoutHeight) || LayoutHeight is < 0.1 or > 1) return "轨道高度必须在 0.1–1.0 之间";
        if (!double.IsFinite(LayoutScale) || LayoutScale is < 0.5 or > 2) return "缩放倍率必须在 0.5–2.0 之间";
        if (!double.IsFinite(JudgementLine) || JudgementLine is < 0.1 or > 1) return "判定线位置必须在 0.1–1.0 之间";
        if (LayoutX + LayoutWidth > 1.0001 || LayoutY + LayoutHeight > 1.0001) return "轨道区域超出游戏客户区";
        if (!double.IsFinite(Intensity) || Intensity is < 0 or > 1) return "特效强度必须在 0.0–1.0 之间";
        if (!double.IsFinite(BackgroundOpacity) || BackgroundOpacity is < 0 or > 1) return "背景不透明度必须在 0.0–1.0 之间";
        return null;
    }

    private bool ComputeIsDirty()
    {
        var orig = _configService.Config.MusicOverlay;
        if (Enabled != orig.Enabled || UseGpuRenderer != (orig.Backend == MusicOverlayBackend.Gpu) ||
            !string.Equals(SelectedSkinId, orig.SkinId, StringComparison.OrdinalIgnoreCase) ||
            FallDurationMs != orig.FallDurationMs || VisualOffsetMs != orig.VisualOffsetMs) return true;

        var l = orig.Layout ?? new();
        if (LayoutX != l.X || LayoutY != l.Y || LayoutWidth != l.Width ||
            LayoutHeight != l.Height || LayoutScale != l.Scale || JudgementLine != l.JudgementLine) return true;

        var e = orig.Effects ?? new();
        return HitBursts != e.HitBursts || HoldGlow != e.HoldGlow || SyncLines != e.SyncLines ||
               LaneGuides != e.LaneGuides || ShowKeyLabels != e.ShowKeyLabels || Intensity != e.Intensity ||
               ShowBackgroundDeco != e.ShowBackgroundDeco || BackgroundOpacity != e.BackgroundOpacity;
    }

    private MusicOverlayConfig CreateSnapshot()
    {
        var snapshot = _configService.Config.MusicOverlay.Clone();
        snapshot.Enabled = Enabled;
        snapshot.Backend = UseGpuRenderer ? MusicOverlayBackend.Gpu : MusicOverlayBackend.Wpf;
        snapshot.SkinId = SelectedSkinId;
        snapshot.FallDurationMs = FallDurationMs;
        snapshot.VisualOffsetMs = VisualOffsetMs;
        snapshot.Layout = CreateCurrentLayoutConfig();
        snapshot.Effects = CreateCurrentEffectsConfig();
        snapshot.Validate();
        return snapshot;
    }

    [RelayCommand]
    public Task ApplyAsync() => ApplyCoreAsync(isAutoSave: false);

    private async Task ApplyCoreAsync(bool isAutoSave)
    {
        if (_disposed || _isApplying) return;
        StopAutoSaveTimer();
        _isApplying = true;
        try
        {
            MusicOverlayConfig snapshot;
            try
            {
                string? error = ValidateDraft();
                if (error != null) throw new ArgumentException(error);
                snapshot = CreateSnapshot();
            }
            catch (ArgumentException ex)
            {
                HasValidationError = true;
                ValidationError = ex.Message;
                IsDirty = ComputeIsDirty();
                StatusMessage = "谱面设置校验失败：" + ex.Message;
                return;
            }

            HasValidationError = false;
            ValidationError = null;
            StatusMessage = isAutoSave ? "正在自动保存谱面设置…" : "正在保存谱面设置…";
            string? runtimeStatus = null;
            if (_presenter != null)
                runtimeStatus = await _presenter.ApplySettingsAsync(snapshot);
            else
                await _configService.SaveMusicOverlayAsync(snapshot);

            if (_disposed) return;
            IsDirty = ComputeIsDirty();
            StatusMessage = runtimeStatus ?? (isAutoSave
                ? (IsDirty ? "本次谱面设置已自动保存；当前草稿仍有未应用修改" : "谱面设置已自动保存并生效")
                : (IsDirty ? "本次谱面设置已保存；当前草稿仍有未应用修改" : "谱面设置已成功应用"));
            if (runtimeStatus != null && IsDirty) StatusMessage += "；当前草稿仍有未应用修改";
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            IsDirty = ComputeIsDirty();
            StatusMessage = "谱面设置保存失败：" + ex.Message;
        }
        finally
        {
            _isApplying = false;
            if (!_disposed && IsDirty && !HasValidationError)
            {
                StartOrResetAutoSaveTimer();
            }
        }
    }

    [RelayCommand]
    public void ResetToDefault()
    {
        _isUpdatingInternally = true;
        try
        {
            var def = new MusicOverlayConfig();
            Enabled = def.Enabled;
            UseGpuRenderer = def.Backend == MusicOverlayBackend.Gpu;
            SelectedSkinId = def.SkinId;
            SelectedSkinInfo = FindSkinInfo(SelectedSkinId);
            _ = LoadSelectedSkinAsync(SelectedSkinId);
            FallDurationMs = def.FallDurationMs;
            VisualOffsetMs = def.VisualOffsetMs;
            ApplyLayout(def.Layout);
            ApplyEffects(def.Effects);
            HasValidationError = false;
            ValidationError = null;
            IsDirty = ComputeIsDirty();
            UpdateResetStatus("已重置为默认值，即将自动保存…");
        }
        finally { _isUpdatingInternally = false; }
        PreviewParamsChanged?.Invoke();
    }

    [RelayCommand]
    public void ResetLayout()
    {
        _isUpdatingInternally = true;
        try
        {
            ApplyLayout(new MusicOverlayLayoutConfig());
            HasValidationError = false;
            ValidationError = null;
            IsDirty = ComputeIsDirty();
            UpdateResetStatus("布局已重置为默认值，即将自动保存…");
        }
        finally { _isUpdatingInternally = false; }
        PreviewParamsChanged?.Invoke();
    }

    private void UpdateResetStatus(string dirtyMessage)
    {
        if (IsDirty) { StatusMessage = dirtyMessage; StartOrResetAutoSaveTimer(); }
        else { StopAutoSaveTimer(); StatusMessage = "当前设置已与配置同步"; }
    }

    [RelayCommand]
    public async Task ToggleAdjustmentAsync()
    {
        if (_presenter == null) return;
        try
        {
            await _presenter.AdjustmentControl.ToggleAsync();
            IsAdjusting = _presenter.Adjusting;
            OnPropertyChanged(nameof(AdjustButtonText));
            if (!_presenter.Adjusting) RefreshLayoutFromConfig();
        }
        catch (Exception ex)
        {
            StatusMessage = "布局调整失败：" + ex.Message;
        }
    }

    public MusicOverlayLayoutConfig CreateCurrentLayoutConfig() => new()
    {
        X = LayoutX, Y = LayoutY, Width = LayoutWidth, Height = LayoutHeight, Scale = LayoutScale, JudgementLine = JudgementLine
    };

    public MusicOverlayEffectsConfig CreateCurrentEffectsConfig() => new()
    {
        HitBursts = HitBursts, HoldGlow = HoldGlow, SyncLines = SyncLines, LaneGuides = LaneGuides,
        ShowKeyLabels = ShowKeyLabels, Intensity = Intensity, ShowBackgroundDeco = ShowBackgroundDeco, BackgroundOpacity = BackgroundOpacity
    };

    public void Dispose()
    {
        if (_disposed) return;
        FlushAutoSave();
        _disposed = true;
        StopAutoSaveTimer();
        _autoSaveTimer = null;
        DisposeSkinResources();
        PausePreviewCore();
        _previewSource.Dispose();
        var previewDisposed = PreviewDisposed;
        PreviewDisposed = null;
        previewDisposed?.Invoke();
        if (_presenter != null && _presenter.Adjusting) _ = _presenter.AdjustmentControl.CloseAsync();
    }
}
