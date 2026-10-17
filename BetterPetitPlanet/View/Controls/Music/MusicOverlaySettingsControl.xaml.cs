using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using BetterPetitPlanet.GameTask.Music.Overlay;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Skins;
using BetterPetitPlanet.ViewModel.Windows;

namespace BetterPetitPlanet.View.Controls.Music;

public partial class MusicOverlaySettingsControl : UserControl
{
    private MusicOverlaySettingsViewModel? _subscribedViewModel;
    private bool _isLoaded;
    private long _subscriptionGeneration;
    private Action<MusicRenderSnapshot>? _snapshotUpdated;
    private Action? _previewParamsChanged;
    private Action<MusicSkin>? _skinChanged;
    private Action? _previewDisposed;

    public MusicOverlaySettingsViewModel? ViewModel
    {
        get => DataContext as MusicOverlaySettingsViewModel;
        set => DataContext = value;
    }

    private bool ShouldUseGpuPreview =>
        ViewModel is { UseGpuRenderer: true } vm &&
        MusicOverlayPresenter.IsGpuSupportedSkin(vm.SelectedSkinId) &&
        (GpuPlayfieldPreview.Host?.Failure == null);

    public MusicOverlaySettingsControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PlayfieldPreview.IsVisibleChanged += (_, _) => UpdatePreviewSubscription();
        GpuPlayfieldPreview.IsVisibleChanged += (_, _) => UpdatePreviewSubscription();
    }

    private void UpdatePreviewTargetVisibility()
    {
        if (ShouldUseGpuPreview)
        {
            if (GpuPlayfieldPreview.Visibility != Visibility.Visible)
            {
                GpuPlayfieldPreview.Visibility = Visibility.Visible;
                if (ViewModel != null)
                    GpuPlayfieldPreview.SetChartIndex(ViewModel.PreviewSource.ChartIndex);
            }
            if (PlayfieldPreview.Visibility != Visibility.Collapsed)
            {
                PlayfieldPreview.Visibility = Visibility.Collapsed;
                PlayfieldPreview.UpdateSnapshot(MusicRenderSnapshot.Empty);
                PlayfieldPreview.ReleaseSkinResources();
            }
        }
        else
        {
            if (PlayfieldPreview.Visibility != Visibility.Visible)
            {
                PlayfieldPreview.Visibility = Visibility.Visible;
                if (ViewModel != null)
                    PlayfieldPreview.SetChartIndex(ViewModel.PreviewSource.ChartIndex);
            }
            if (GpuPlayfieldPreview.Visibility != Visibility.Collapsed)
            {
                GpuPlayfieldPreview.Visibility = Visibility.Collapsed;
                GpuPlayfieldPreview.Reset();
                GpuPlayfieldPreview.ReleaseSkinResources();
            }
        }
    }

    private async void ImportSkinPackage_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { IsDisposed: false } viewModel || viewModel.IsImporting) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择音乐皮肤 ZIP 包",
            Filter = "音乐皮肤 ZIP 包 (*.zip)|*.zip",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        ImportSkinPackageButton.IsEnabled = false;
        try
        {
            await viewModel.ImportSkinPackageAsync(dialog.FileName);
        }
        finally
        {
            ImportSkinPackageButton.IsEnabled = true;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UnsubscribeFromViewModel();
        UpdatePreviewTargetVisibility();
        UpdatePreviewSubscription();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        UpdatePreviewTargetVisibility();
        UpdatePreviewSubscription();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        UnsubscribeFromViewModel();
    }

    private void UpdatePreviewSubscription()
    {
        bool previewVisible = PlayfieldPreview.IsVisible || GpuPlayfieldPreview.IsVisible;
        if (!_isLoaded || !previewVisible)
        {
            UnsubscribeFromViewModel();
            ViewModel?.SuspendPreviewResources();
            return;
        }
        if (ViewModel is { IsDisposed: false } vm && _subscribedViewModel != vm)
            SubscribeToViewModel(vm);
    }

    private void SubscribeToViewModel(MusicOverlaySettingsViewModel vm)
    {
        UnsubscribeFromViewModel();
        vm.PausePreview();
        _subscribedViewModel = vm;
        long generation = _subscriptionGeneration;

        UpdatePreviewTargetVisibility();
        PlayfieldPreview.SetChartIndex(vm.PreviewSource.ChartIndex);
        GpuPlayfieldPreview.SetChartIndex(vm.PreviewSource.ChartIndex);
        UpdatePreviewParameters(vm);

        _snapshotUpdated = snapshot => QueuePreviewUpdate(vm, generation, () =>
        {
            if (ShouldUseGpuPreview)
            {
                GpuPlayfieldPreview.UpdateSnapshot(snapshot);
            }
            else
            {
                if (PlayfieldPreview.Visibility != Visibility.Visible)
                {
                    UpdatePreviewTargetVisibility();
                    UpdatePreviewParameters(vm);
                }
                PlayfieldPreview.UpdateSnapshot(snapshot);
            }
        });
        _previewParamsChanged = () => QueuePreviewUpdate(vm, generation, () =>
        {
            UpdatePreviewTargetVisibility();
            UpdatePreviewParameters(vm);
            var snapshot = vm.PreviewSource.CaptureSnapshot();
            if (ShouldUseGpuPreview)
                GpuPlayfieldPreview.UpdateSnapshot(snapshot);
            else
                PlayfieldPreview.UpdateSnapshot(snapshot);
        });
        _skinChanged = skin => QueuePreviewUpdate(vm, generation, () =>
        {
            UpdatePreviewTargetVisibility();
            UpdatePreviewParameters(vm);
            var snapshot = vm.PreviewSource.CaptureSnapshot();
            if (ShouldUseGpuPreview)
                GpuPlayfieldPreview.UpdateSnapshot(snapshot);
            else
                PlayfieldPreview.UpdateSnapshot(snapshot);
        });
        _previewDisposed = () =>
        {
            if (Dispatcher.CheckAccess()) ReleasePreview(vm, generation);
            else Dispatcher.InvokeAsync(() => ReleasePreview(vm, generation));
        };
        vm.SnapshotUpdated += _snapshotUpdated;
        vm.PreviewParamsChanged += _previewParamsChanged;
        vm.SkinChanged += _skinChanged;
        vm.PreviewDisposed += _previewDisposed;
        _ = vm.ResumePreviewResourcesAsync();

        // Display initial static frame
        var initialSnapshot = vm.PreviewSource.CaptureSnapshot();
        if (ShouldUseGpuPreview)
        {
            PlayfieldPreview.UpdateSnapshot(MusicRenderSnapshot.Empty);
            PlayfieldPreview.ReleaseSkinResources();
            GpuPlayfieldPreview.UpdateSnapshot(initialSnapshot);
        }
        else
        {
            PlayfieldPreview.UpdateSnapshot(initialSnapshot);
        }
    }

    private void UnsubscribeFromViewModel()
    {
        _subscriptionGeneration++;
        if (_subscribedViewModel != null)
        {
            var vm = _subscribedViewModel;
            vm.SnapshotUpdated -= _snapshotUpdated;
            vm.PreviewParamsChanged -= _previewParamsChanged;
            vm.SkinChanged -= _skinChanged;
            vm.PreviewDisposed -= _previewDisposed;
            _subscribedViewModel = null;
            _snapshotUpdated = null;
            _previewParamsChanged = null;
            _skinChanged = null;
            _previewDisposed = null;
            vm.SuspendPreviewResources();
        }
        PlayfieldPreview.SetChartIndex(null);
        PlayfieldPreview.UpdateSnapshot(MusicRenderSnapshot.Empty);
        PlayfieldPreview.ReleaseSkinResources();
        GpuPlayfieldPreview.Reset();
        GpuPlayfieldPreview.ReleaseSkinResources();
    }

    private void ReleasePreview(MusicOverlaySettingsViewModel vm, long generation)
    {
        if (_subscriptionGeneration != generation || !ReferenceEquals(_subscribedViewModel, vm)) return;
        UnsubscribeFromViewModel();
    }

    private void QueuePreviewUpdate(MusicOverlaySettingsViewModel vm, long generation, Action update)
    {
        Dispatcher.InvokeAsync(() =>
        {
            // The same VM can be reattached before an earlier subscription's work reaches the dispatcher.
            bool previewVisible = PlayfieldPreview.IsVisible || GpuPlayfieldPreview.IsVisible;
            if (_isLoaded && previewVisible && !vm.IsDisposed &&
                _subscriptionGeneration == generation && ReferenceEquals(_subscribedViewModel, vm))
                update();
        });
    }

    private void UpdatePreviewParameters(MusicOverlaySettingsViewModel vm)
    {
        var layout = vm.CreateCurrentLayoutConfig();
        var effects = vm.CreatePreviewEffectsConfig();
        int fallDuration = vm.FallDurationMs;
        int visualOffset = vm.VisualOffsetMs;
        var skin = vm.ActiveSkin;

        PlayfieldPreview.LayoutConfig = layout;
        PlayfieldPreview.EffectsConfig = effects;
        PlayfieldPreview.FallDurationMs = fallDuration;
        PlayfieldPreview.VisualOffsetMs = visualOffset;
        PlayfieldPreview.SetSkin(skin);
        PlayfieldPreview.SetChartIndex(vm.PreviewSource.ChartIndex);

        GpuPlayfieldPreview.SetChartIndex(vm.PreviewSource.ChartIndex);
        GpuPlayfieldPreview.SetParameters(layout, effects, fallDuration, visualOffset, skin);
    }
}
