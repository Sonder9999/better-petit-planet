using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Overlay;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.ViewModel.Windows;
using Xunit;

namespace BetterPetitPlanet.Test;

public sealed class MusicOverlaySettingsTest
{
    private sealed class MemoryConfigService : IConfigService
    {
        public AppConfig Config { get; } = new();
        public int SaveCount { get; private set; }

        public void Save() => SaveCount++;
        public Task SaveAsync()
        {
            SaveCount++;
            return Task.CompletedTask;
        }
        public Task SaveMusicOverlayAsync(MusicOverlayConfig settings)
        {
            settings.Validate();
            Config.MusicOverlay.ApplySettingsFrom(settings);
            return SaveAsync();
        }
        public void Reload() { }
    }

    [Fact]
    public async Task DraftValidation_RejectsInvalidFallDuration_GeneratesErrorMessage()
    {
        var configService = new MemoryConfigService();
        using var vm = new MusicOverlaySettingsViewModel(configService);

        Assert.False(vm.HasValidationError);
        Assert.Null(vm.ValidationError);

        // Invalid: below minimum (500)
        vm.FallDurationMs = 200;
        Assert.True(vm.HasValidationError);
        Assert.Contains("提前显示时长", vm.ValidationError);
        Assert.Contains("暂时无效", vm.StatusMessage);

        // The async command reports validation in the UI without faulting.
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.True(vm.IsDirty);
        Assert.Equal(0, configService.SaveCount);
        Assert.Equal(3000, configService.Config.MusicOverlay.FallDurationMs);
        Assert.Contains("失败", vm.StatusMessage);

        // Restore valid
        vm.FallDurationMs = 2500;
        Assert.False(vm.HasValidationError);
        Assert.Null(vm.ValidationError);

        // Invalid: above maximum (10000)
        vm.FallDurationMs = 15000;
        Assert.True(vm.HasValidationError);
        Assert.Contains("提前显示时长", vm.ValidationError);
    }

    [Fact]
    public void DraftValidation_RejectsInvalidVisualOffset()
    {
        var configService = new MemoryConfigService();
        using var vm = new MusicOverlaySettingsViewModel(configService);

        vm.VisualOffsetMs = 2500; // > 2000
        Assert.True(vm.HasValidationError);
        Assert.Contains("视觉偏移", vm.ValidationError);

        vm.VisualOffsetMs = -3000; // < -2000
        Assert.True(vm.HasValidationError);
        Assert.Contains("视觉偏移", vm.ValidationError);

        vm.VisualOffsetMs = -120;
        Assert.False(vm.HasValidationError);
    }

    [Fact]
    public void DraftValidation_RejectsOutOfBoundsLayout()
    {
        var configService = new MemoryConfigService();
        using var vm = new MusicOverlaySettingsViewModel(configService);

        // X + Width > 1.0001
        vm.LayoutX = 0.7;
        vm.LayoutWidth = 0.5;
        Assert.True(vm.HasValidationError);
        Assert.Contains("超出游戏客户区", vm.ValidationError);

        // Restore valid layout
        vm.LayoutX = 0.1;
        vm.LayoutWidth = 0.8;
        Assert.False(vm.HasValidationError);
    }

    [Fact]
    public void DraftValidation_RejectsInvalidScaleAndIntensity()
    {
        var configService = new MemoryConfigService();
        using var vm = new MusicOverlaySettingsViewModel(configService);

        vm.LayoutScale = 3.5; // > 2.0
        Assert.True(vm.HasValidationError);
        Assert.Contains("缩放倍率", vm.ValidationError);

        vm.LayoutScale = 1.0;
        Assert.False(vm.HasValidationError);

        vm.Intensity = 1.5; // > 1.0
        Assert.True(vm.HasValidationError);
        Assert.Contains("特效强度", vm.ValidationError);
    }

    [Fact]
    public async Task ApplyDraft_PersistsToConfigService_AndClearsDirty()
    {
        var configService = new MemoryConfigService();
        configService.Config.MusicOverlay.FallDurationMs = 3000;
        configService.Config.MusicOverlay.VisualOffsetMs = 0;

        using var vm = new MusicOverlaySettingsViewModel(configService);
        Assert.False(vm.IsDirty);

        vm.FallDurationMs = 4500;
        vm.VisualOffsetMs = -180;
        vm.HitBursts = false;
        vm.LayoutScale = 1.25;

        Assert.True(vm.IsDirty);
        // Runtime config must NOT be mutated before apply
        Assert.Equal(3000, configService.Config.MusicOverlay.FallDurationMs);
        Assert.Equal(0, configService.Config.MusicOverlay.VisualOffsetMs);
        Assert.True(configService.Config.MusicOverlay.Effects.HitBursts);

        await vm.ApplyAsync();

        Assert.False(vm.IsDirty);
        Assert.Equal(4500, configService.Config.MusicOverlay.FallDurationMs);
        Assert.Equal(-180, configService.Config.MusicOverlay.VisualOffsetMs);
        Assert.False(configService.Config.MusicOverlay.Effects.HitBursts);
        Assert.Equal(1.25, configService.Config.MusicOverlay.Layout.Scale);
        Assert.True(configService.SaveCount > 0);
    }

    [Fact]
    public void ResetToDefault_RestoresDefaultsInDraft_WithoutMutatingConfigBeforeApply()
    {
        var configService = new MemoryConfigService();
        configService.Config.MusicOverlay.FallDurationMs = 5000;
        configService.Config.MusicOverlay.VisualOffsetMs = 300;
        configService.Config.MusicOverlay.Layout.Scale = 1.5;

        using var vm = new MusicOverlaySettingsViewModel(configService);
        Assert.False(vm.IsDirty);

        vm.ResetToDefault();

        // Draft reset to defaults
        Assert.Equal(3000, vm.FallDurationMs);
        Assert.Equal(0, vm.VisualOffsetMs);
        Assert.Equal(1.0, vm.LayoutScale);
        Assert.True(vm.IsDirty);

        // Original config NOT changed yet
        Assert.Equal(5000, configService.Config.MusicOverlay.FallDurationMs);
        Assert.Equal(300, configService.Config.MusicOverlay.VisualOffsetMs);
        Assert.Equal(1.5, configService.Config.MusicOverlay.Layout.Scale);
    }

    [Fact]
    public void AutoSave_ClosingWithPendingTimer_FlushesAndPersistsEdits()
    {
        var configService = new MemoryConfigService();
        configService.Config.MusicOverlay.FallDurationMs = 3000;
        configService.Config.MusicOverlay.VisualOffsetMs = 50;

        {
            using var vm = new MusicOverlaySettingsViewModel(configService);
            vm.FallDurationMs = 6000;
            vm.VisualOffsetMs = -400;
            Assert.True(vm.IsDirty);
            // Window closes -> vm.Dispose() called while timer is waiting -> must flush
        }

        Assert.Equal(6000, configService.Config.MusicOverlay.FallDurationMs);
        Assert.Equal(-400, configService.Config.MusicOverlay.VisualOffsetMs);
        Assert.True(configService.SaveCount > 0);
    }

    [Fact]
    public void AutoSave_ClosingWithValidationError_DoesNotFlushInvalidEdits()
    {
        var configService = new MemoryConfigService();
        configService.Config.MusicOverlay.FallDurationMs = 3000;
        configService.Config.MusicOverlay.VisualOffsetMs = 50;

        {
            using var vm = new MusicOverlaySettingsViewModel(configService);
            vm.FallDurationMs = 200; // invalid, < 500
            Assert.True(vm.HasValidationError);
            Assert.True(vm.IsDirty);
            // Window closes with validation error -> must NOT flush
        }

        Assert.Equal(3000, configService.Config.MusicOverlay.FallDurationMs);
        Assert.Equal(50, configService.Config.MusicOverlay.VisualOffsetMs);
        Assert.Equal(0, configService.SaveCount);
    }

    [Fact]
    public void PreviewSource_StrictPhysicalIsolation_AndPlaybackToggle()
    {
        var configService = new MemoryConfigService();
        using var vm = new MusicOverlaySettingsViewModel(configService);

        Assert.NotNull(vm.PreviewSource);
        Assert.False(vm.IsPreviewPlaying);
        Assert.False(vm.PreviewSource.IsPlaying);

        // Capture static initial snapshot
        var initialSnapshot = vm.PreviewSource.CaptureSnapshot();
        Assert.NotNull(initialSnapshot);
        Assert.False(initialSnapshot.IsPlaying);
        Assert.True(initialSnapshot.NoteCount > 0);

        // Toggle playback
        vm.TogglePreviewPlayback();
        Assert.True(vm.IsPreviewPlaying);
        Assert.True(vm.PreviewSource.IsPlaying);

        var playingSnapshot = vm.PreviewSource.CaptureSnapshot();
        Assert.True(playingSnapshot.IsPlaying);

        // Pause playback
        vm.TogglePreviewPlayback();
        Assert.False(vm.IsPreviewPlaying);
        Assert.False(vm.PreviewSource.IsPlaying);
    }

    [Fact]
    public void RefreshLayoutFromConfig_UpdatesDraftFields()
    {
        var configService = new MemoryConfigService();
        using var vm = new MusicOverlaySettingsViewModel(configService);

        configService.Config.MusicOverlay.Layout.X = 0.25;
        configService.Config.MusicOverlay.Layout.Width = 0.70;

        vm.RefreshLayoutFromConfig();

        Assert.Equal(0.25, vm.LayoutX);
        Assert.Equal(0.70, vm.LayoutWidth);
    }

    [Fact]
    public async Task SkinSelection_UpdatesDraft_DrivesSkinLoad_AndAppliesToConfig()
    {
        var configService = new MemoryConfigService();
        using var vm = new MusicOverlaySettingsViewModel(configService);

        Assert.Equal("default", vm.SelectedSkinId);
        Assert.False(vm.IsDirty);
        Assert.NotEmpty(vm.AvailableSkins);

        // Switch skin in draft
        bool eventFired = false;
        vm.SkinChanged += _ => eventFired = true;

        vm.SelectedSkinId = "nahida";
        Assert.True(vm.IsDirty);

        // Wait for async load (up to 1000ms)
        for (int i = 0; i < 50 && !eventFired; i++)
        {
            await Task.Delay(20);
        }
        Assert.True(eventFired);

        // Apply draft
        await vm.ApplyAsync();
        Assert.False(vm.IsDirty);
        Assert.Equal("nahida", configService.Config.MusicOverlay.SkinId);

        // Reset to default
        vm.ResetToDefault();
        Assert.Equal("default", vm.SelectedSkinId);
        Assert.True(vm.IsDirty);

        await vm.ApplyAsync();
        Assert.False(vm.IsDirty);
        Assert.Equal("default", configService.Config.MusicOverlay.SkinId);
    }

    private static void RunSta(Func<Task> action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); }
                catch (Exception ex) { error = ex; }
                finally { frame.Continue = false; }
            });
            Dispatcher.PushFrame(frame);
            dispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA test timed out");
        if (error != null) throw new AggregateException(error);
    }

    [Fact]
    public void AutoSave_DebounceTimer_SavesAfterDelay() => RunSta(async () =>
    {
        var configService = new MemoryConfigService();
        using var vm = new MusicOverlaySettingsViewModel(configService, dispatcher: Dispatcher.CurrentDispatcher);
        Assert.False(vm.IsDirty);

        vm.FallDurationMs = 4500;
        Assert.True(vm.IsDirty);
        Assert.Equal("设置已更改，即将自动保存…", vm.StatusMessage);
        Assert.Equal(3000, configService.Config.MusicOverlay.FallDurationMs);

        // Wait for debounce timer (400ms) + background dispatch
        for (int i = 0; i < 50 && vm.IsDirty; i++)
        {
            await Task.Delay(20);
        }

        Assert.False(vm.IsDirty);
        Assert.Equal(4500, configService.Config.MusicOverlay.FallDurationMs);
        Assert.Contains("谱面设置已自动保存并生效", vm.StatusMessage);
    });

    [Fact]
    public async Task ManualApply_CancelsDebounceTimer_AndPersistsImmediately()
    {
        var configService = new MemoryConfigService();
        using var vm = new MusicOverlaySettingsViewModel(configService);

        vm.FallDurationMs = 5500;
        Assert.True(vm.IsDirty);
        Assert.Equal("设置已更改，即将自动保存…", vm.StatusMessage);

        await vm.ApplyAsync();

        Assert.False(vm.IsDirty);
        Assert.Equal(5500, configService.Config.MusicOverlay.FallDurationMs);
        Assert.Contains("谱面设置已成功应用", vm.StatusMessage);
        Assert.Equal(1, configService.SaveCount);
    }

    [Fact]
    public void GameWindowTracker_ShouldUpdatePosition_FiltersSmallDeltas()
    {
        var current = new System.Windows.Rect(100, 200, 800, 600);

        // Identical rect -> false
        Assert.False(GameWindowTracker.ShouldUpdatePosition(current, new System.Windows.Rect(100, 200, 800, 600)));

        // Delta < 1.0 (subpixel movement or noise) -> false
        Assert.False(GameWindowTracker.ShouldUpdatePosition(current, new System.Windows.Rect(100.5, 200.2, 800.8, 599.4)));

        // Delta >= 1.0 in X -> true
        Assert.True(GameWindowTracker.ShouldUpdatePosition(current, new System.Windows.Rect(101.0, 200, 800, 600)));

        // Delta >= 1.0 in Y -> true
        Assert.True(GameWindowTracker.ShouldUpdatePosition(current, new System.Windows.Rect(100, 201.5, 800, 600)));

        // Delta >= 1.0 in Width -> true
        Assert.True(GameWindowTracker.ShouldUpdatePosition(current, new System.Windows.Rect(100, 200, 802.0, 600)));

        // Delta >= 1.0 in Height -> true
        Assert.True(GameWindowTracker.ShouldUpdatePosition(current, new System.Windows.Rect(100, 200, 800, 601.0)));

        // Multi-monitor negative coordinates
        var multiMonitor = new System.Windows.Rect(-1920, 0, 1920, 1080);
        Assert.False(GameWindowTracker.ShouldUpdatePosition(multiMonitor, new System.Windows.Rect(-1920.4, 0.3, 1920, 1080)));
        Assert.True(GameWindowTracker.ShouldUpdatePosition(multiMonitor, new System.Windows.Rect(-1918.0, 0, 1920, 1080)));
    }
}
