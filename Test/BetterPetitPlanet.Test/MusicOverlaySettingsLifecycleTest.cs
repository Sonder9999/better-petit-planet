using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Skins;
using BetterPetitPlanet.View.Controls.Music;
using BetterPetitPlanet.ViewModel.Windows;

namespace BetterPetitPlanet.Test;

public sealed class MusicOverlaySettingsLifecycleTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DataContextChange_DropsQueuedUpdatesFromOldSubscription(bool skinUpdate) => MusicRenderingTestHost.Run(() =>
    {
        using var first = CreateViewModel();
        using var second = CreateViewModel();
        var control = new MusicOverlaySettingsControl { ViewModel = first };
        using var host = MusicRenderingTestHost.Mount(control);
        var playfield = (MusicPlayfieldControl)control.FindName("PlayfieldPreview");
        if (skinUpdate) Assert.True(first.LoadSelectedSkinAsync("old-skin").IsCompletedSuccessfully);
        else first.TogglePreviewPlayback();
        control.ViewModel = second;
        MusicRenderingTestHost.Drain();
        Assert.Equal(second.PreviewSource.SessionId, playfield.CurrentSnapshot.SessionId);
        Assert.Same(second.PreviewSource.ChartIndex, playfield.ChartIndex);
        Assert.False(playfield.CurrentSnapshot.IsPlaying);
        Assert.Same(second.ActiveSkin, playfield.Skin);
        Assert.Equal(second.FallDurationMs, playfield.FallDurationMs);
        Assert.False(first.PreviewSource.IsPlaying);
    });

    [Fact]
    public void Unload_InvalidatesQueuedCallbacks_AndReloadStartsStatic() => MusicRenderingTestHost.Run(() =>
    {
        using var vm = CreateViewModel();
        var control = new MusicOverlaySettingsControl { ViewModel = vm };
        using var host = MusicRenderingTestHost.Mount(control);
        var playfield = (MusicPlayfieldControl)control.FindName("PlayfieldPreview");
        vm.TogglePreviewPlayback();
        vm.SelectedSkinId = "queued-skin";
        vm.FallDurationMs = 2000;
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        MusicRenderingTestHost.Drain();
        Assert.Equal(MusicRenderSnapshot.Empty, playfield.CurrentSnapshot);
        Assert.Null(playfield.ChartIndex);
        Assert.Same(MusicSkin.Default, playfield.Skin);
        Assert.False(vm.PreviewSource.IsPlaying);
        Assert.False(vm.IsPreviewPlaying);
        host.RootVisual = null;
        MusicRenderingTestHost.Drain();
        host.RootVisual = control;
        MusicRenderingTestHost.Drain();
        Assert.Equal(vm.PreviewSource.SessionId, playfield.CurrentSnapshot.SessionId);
        Assert.Same(vm.PreviewSource.ChartIndex, playfield.ChartIndex);
        Assert.False(playfield.CurrentSnapshot.IsPlaying);
        Assert.Same(vm.ActiveSkin, playfield.Skin);
        Assert.Equal(2000, playfield.FallDurationMs);
    });

    [Fact]
    public void CollapsedPreview_StopsSourceAndDispatcherTimer_AndReopensStatic() => MusicRenderingTestHost.Run(() =>
    {
        double now = 0;
        using var vm = CreateViewModel(() => now);
        var control = new MusicOverlaySettingsControl { ViewModel = vm };
        using var host = MusicRenderingTestHost.Mount(control);
        var playfield = (MusicPlayfieldControl)control.FindName("PlayfieldPreview");
        vm.TogglePreviewPlayback();
        MusicRenderingTestHost.Drain();
        // Inspect the real timer's public state in addition to observable source and visual behavior.
        var timer = (DispatcherTimer)typeof(MusicOverlaySettingsViewModel)
            .GetField("_previewTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        Assert.True(timer.IsEnabled);
        now = 1500;
        playfield.Visibility = Visibility.Collapsed;
        MusicRenderingTestHost.Drain();
        Assert.False(vm.PreviewSource.IsPlaying);
        Assert.False(vm.IsPreviewPlaying);
        Assert.False(timer.IsEnabled);
        var pausedPosition = vm.PreviewSource.GetCurrentPositionMs();
        now += 3000;
        playfield.Visibility = Visibility.Visible;
        MusicRenderingTestHost.Drain();
        Assert.Equal(pausedPosition, vm.PreviewSource.GetCurrentPositionMs());
        Assert.Equal(vm.PreviewSource.SessionId, playfield.CurrentSnapshot.SessionId);
        Assert.Same(vm.PreviewSource.ChartIndex, playfield.ChartIndex);
        Assert.False(playfield.CurrentSnapshot.IsPlaying);
        Assert.False(timer.IsEnabled);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposeAfterHidingOrUnloading_ReopeningKeepsPreviewReleased(bool unload) => MusicRenderingTestHost.Run(() =>
    {
        using var vm = CreateViewModel(() => 0);
        var control = new MusicOverlaySettingsControl { ViewModel = vm };
        using var host = MusicRenderingTestHost.Mount(control);
        var playfield = (MusicPlayfieldControl)control.FindName("PlayfieldPreview");
        var hooked = typeof(MusicPlayfieldControl).GetField("_isRenderingHooked", BindingFlags.Instance | BindingFlags.NonPublic)!;
        vm.TogglePreviewPlayback();
        MusicRenderingTestHost.Drain();
        Assert.True(playfield.CurrentSnapshot.IsPlaying);
        Assert.True((bool)hooked.GetValue(playfield)!);

        if (unload) host.RootVisual = null;
        else playfield.Visibility = Visibility.Collapsed;
        MusicRenderingTestHost.Drain();
        Assert.False(vm.PreviewSource.IsPlaying);
        Assert.False((bool)hooked.GetValue(playfield)!);
        vm.Dispose();

        if (unload) host.RootVisual = control;
        else playfield.Visibility = Visibility.Visible;
        MusicRenderingTestHost.Drain();
        Assert.True(control.IsLoaded);
        Assert.True(playfield.IsVisible);
        Assert.False(vm.PreviewSource.IsPlaying);
        Assert.False(playfield.CurrentSnapshot.IsPlaying);
        Assert.False((bool)hooked.GetValue(playfield)!);
        Assert.Equal(MusicRenderSnapshot.Empty, playfield.CurrentSnapshot);
        Assert.Null(playfield.ChartIndex);
        Assert.True(MusicPlayfieldLifecycleTest.Layer(playfield, 1).Bounds.IsEmpty);
        Assert.True(MusicPlayfieldLifecycleTest.Layer(playfield, 2).Bounds.IsEmpty);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DataContextClearedOrDisposed_ReleasesDisplayedPreview(bool disposedReplacement) => MusicRenderingTestHost.Run(() =>
    {
        using var vm = CreateViewModel(() => 0);
        using var replacement = CreateViewModel(() => 0);
        replacement.Dispose();
        var control = new MusicOverlaySettingsControl { ViewModel = vm };
        using var host = MusicRenderingTestHost.Mount(control);
        var playfield = (MusicPlayfieldControl)control.FindName("PlayfieldPreview");
        var hooked = typeof(MusicPlayfieldControl).GetField("_isRenderingHooked", BindingFlags.Instance | BindingFlags.NonPublic)!;
        vm.TogglePreviewPlayback();
        MusicRenderingTestHost.Drain();
        Assert.True(playfield.CurrentSnapshot.IsPlaying);
        Assert.True((bool)hooked.GetValue(playfield)!);

        vm.TogglePreviewPlayback();
        vm.TogglePreviewPlayback(); // Leave another playing snapshot queued before detaching.
        control.ViewModel = disposedReplacement ? replacement : null;
        MusicRenderingTestHost.Drain();

        Assert.True(control.IsLoaded);
        Assert.False(vm.PreviewSource.IsPlaying);
        Assert.False(playfield.CurrentSnapshot.IsPlaying);
        Assert.False((bool)hooked.GetValue(playfield)!);
        Assert.Equal(MusicRenderSnapshot.Empty, playfield.CurrentSnapshot);
        Assert.Null(playfield.ChartIndex);
        Assert.True(MusicPlayfieldLifecycleTest.Layer(playfield, 1).Bounds.IsEmpty);
        Assert.True(MusicPlayfieldLifecycleTest.Layer(playfield, 2).Bounds.IsEmpty);
    });

    [Fact]
    public void DisposedViewModel_CannotRestartPreviewOrPublishSnapshots() => MusicRenderingTestHost.Run(() =>
    {
        using var vm = CreateViewModel();
        vm.TogglePreviewPlayback();
        vm.Dispose();
        int published = 0;
        int propertiesChanged = 0;
        vm.SnapshotUpdated += _ => published++;
        vm.PropertyChanged += (_, _) => propertiesChanged++;
        vm.TogglePreviewPlayback();
        vm.PausePreview();
        MusicRenderingTestHost.Drain();
        Assert.False(vm.IsPreviewPlaying);
        Assert.False(vm.PreviewSource.IsPlaying);
        Assert.Equal(0, published);
        Assert.Equal(0, propertiesChanged);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueuedSnapshot_CannotReviveAfterResubscriptionOrDisposal(bool dispose) => MusicRenderingTestHost.Run(() =>
    {
        using var vm = CreateViewModel();
        var control = new MusicOverlaySettingsControl { ViewModel = vm };
        using var host = MusicRenderingTestHost.Mount(control);
        var playfield = (MusicPlayfieldControl)control.FindName("PlayfieldPreview");
        vm.TogglePreviewPlayback(); // Leave the playing snapshot queued on the dispatcher.
        if (dispose) vm.Dispose();
        else
        {
            playfield.Visibility = Visibility.Collapsed;
            playfield.Visibility = Visibility.Visible; // Same VM, new subscription generation.
        }
        MusicRenderingTestHost.Drain();
        Assert.False(playfield.CurrentSnapshot.IsPlaying);
        Assert.False(vm.PreviewSource.IsPlaying);
    });

    [Fact]
    public void DisposeWhileMountedAndPlaying_ReleasesDisplayedPreviewWithoutUnloading() => MusicRenderingTestHost.Run(() =>
    {
        using var vm = CreateViewModel();
        var control = new MusicOverlaySettingsControl { ViewModel = vm };
        using var host = MusicRenderingTestHost.Mount(control);
        var playfield = (MusicPlayfieldControl)control.FindName("PlayfieldPreview");
        var hooked = typeof(MusicPlayfieldControl).GetField("_isRenderingHooked", BindingFlags.Instance | BindingFlags.NonPublic)!;
        vm.TogglePreviewPlayback();
        MusicRenderingTestHost.Drain();
        Assert.True(playfield.CurrentSnapshot.IsPlaying);
        Assert.True((bool)hooked.GetValue(playfield)!);

        // Also leave a normal playing snapshot queued before the explicit release boundary.
        vm.TogglePreviewPlayback();
        vm.TogglePreviewPlayback();
        int normalSnapshots = 0;
        vm.SnapshotUpdated += _ => normalSnapshots++;
        vm.Dispose();
        MusicRenderingTestHost.Drain();

        Assert.True(control.IsLoaded);
        Assert.False(vm.PreviewSource.IsPlaying);
        Assert.False(playfield.CurrentSnapshot.IsPlaying);
        Assert.False((bool)hooked.GetValue(playfield)!);
        Assert.Equal(0, normalSnapshots);
        Assert.True(MusicPlayfieldLifecycleTest.Layer(playfield, 1).Bounds.IsEmpty);
        Assert.True(MusicPlayfieldLifecycleTest.Layer(playfield, 2).Bounds.IsEmpty);

        // A callback already in flight must not reattach or redraw the released chart.
        typeof(MusicPlayfieldControl).GetMethod("OnRendering", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(playfield, [null, EventArgs.Empty]);
        Assert.False((bool)hooked.GetValue(playfield)!);
        Assert.True(MusicPlayfieldLifecycleTest.Layer(playfield, 1).Bounds.IsEmpty);
    });

    [Fact]
    public void GpuPreview_TargetVisibilityAndParameterSync_FollowsBackendAndSkinSupport() => MusicRenderingTestHost.Run(() =>
    {
        using var vm = CreateViewModel();
        var control = new MusicOverlaySettingsControl { ViewModel = vm };
        using var host = MusicRenderingTestHost.Mount(control);
        var wpfPreview = (MusicPlayfieldControl)control.FindName("PlayfieldPreview");
        var gpuPreview = (MusicGpuPlayfieldControl)control.FindName("GpuPlayfieldPreview");

        // 1. Initial state (WPF default)
        Assert.False(vm.UseGpuRenderer);
        Assert.Equal(Visibility.Visible, wpfPreview.Visibility);
        Assert.Equal(Visibility.Collapsed, gpuPreview.Visibility);

        // 2. Enable GPU renderer with default skin -> switches to GPU preview
        vm.UseGpuRenderer = true;
        MusicRenderingTestHost.Drain();
        Assert.Equal(Visibility.Collapsed, wpfPreview.Visibility);
        Assert.Equal(Visibility.Visible, gpuPreview.Visibility);
        Assert.True(gpuPreview.EffectsConfig.HitBursts);
        Assert.True(gpuPreview.EffectsConfig.HoldGlow);

        // 3. Switch to supported reference skin: builtin:shougun -> remains GPU preview
        vm.SelectedSkinId = "builtin:shougun";
        MusicRenderingTestHost.Drain();
        Assert.Equal(Visibility.Collapsed, wpfPreview.Visibility);
        Assert.Equal(Visibility.Visible, gpuPreview.Visibility);
        Assert.Equal("builtin:shougun", gpuPreview.Skin?.Id);

        // 3b. Switch to C2 pilot skin: builtin:furina -> remains GPU preview
        vm.SelectedSkinId = "builtin:furina";
        MusicRenderingTestHost.Drain();
        Assert.Equal(Visibility.Collapsed, wpfPreview.Visibility);
        Assert.Equal(Visibility.Visible, gpuPreview.Visibility);
        Assert.Equal("builtin:furina", gpuPreview.Skin?.Id);

        // 3c. Switch to C2 reference skin: builtin:neuvillette -> remains GPU preview
        vm.SelectedSkinId = "builtin:neuvillette";
        MusicRenderingTestHost.Drain();
        Assert.Equal(Visibility.Collapsed, wpfPreview.Visibility);
        Assert.Equal(Visibility.Visible, gpuPreview.Visibility);
        Assert.Equal("builtin:neuvillette", gpuPreview.Skin?.Id);

        // 4. Switch to unsupported reference skin: builtin:unknown_fallback -> falls back to WPF preview
        vm.SelectedSkinId = "builtin:unknown_fallback";
        MusicRenderingTestHost.Drain();
        Assert.Equal(Visibility.Visible, wpfPreview.Visibility);
        Assert.Equal(Visibility.Collapsed, gpuPreview.Visibility);
        Assert.Equal("builtin:unknown_fallback", wpfPreview.Skin?.Id);

        // 5. Switch back to default skin -> returns to GPU preview
        vm.SelectedSkinId = "default";
        MusicRenderingTestHost.Drain();
        Assert.Equal(Visibility.Collapsed, wpfPreview.Visibility);
        Assert.Equal(Visibility.Visible, gpuPreview.Visibility);

        // 6. Toggling playback and parameters updates GpuPlayfieldPreview
        vm.FallDurationMs = 2500;
        vm.VisualOffsetMs = 50;
        vm.HitBursts = false;
        MusicRenderingTestHost.Drain();
        Assert.Equal(2500, gpuPreview.FallDurationMs);
        Assert.Equal(50, gpuPreview.VisualOffsetMs);
        Assert.False(gpuPreview.EffectsConfig.HitBursts);

        vm.TogglePreviewPlayback();
        MusicRenderingTestHost.Drain();
        Assert.True(vm.IsPreviewPlaying);
        Assert.True(gpuPreview.CurrentSnapshot.IsPlaying);

        vm.TogglePreviewPlayback();
        MusicRenderingTestHost.Drain();
        Assert.False(vm.IsPreviewPlaying);
        Assert.False(gpuPreview.CurrentSnapshot.IsPlaying);

        // 7. Disable GPU renderer -> returns to WPF preview with matching parameters
        vm.UseGpuRenderer = false;
        MusicRenderingTestHost.Drain();
        Assert.Equal(Visibility.Visible, wpfPreview.Visibility);
        Assert.Equal(Visibility.Collapsed, gpuPreview.Visibility);
        Assert.Equal(2500, wpfPreview.FallDurationMs);
        Assert.Equal(50, wpfPreview.VisualOffsetMs);
        Assert.False(wpfPreview.EffectsConfig.HitBursts);
    });

    private static MusicOverlaySettingsViewModel CreateViewModel(Func<double>? clock = null) => new(
        new MemoryConfig(), previewSourceFactory: () => new MusicPreviewSource(clock),
        dispatcher: Dispatcher.CurrentDispatcher,
        skinLoader: id => Task.FromResult(new MusicSkin { Id = id }));

    private sealed class MemoryConfig : IConfigService
    {
        public AppConfig Config { get; } = new();
        public void Save() { }
        public void Reload() { }
    }
}
