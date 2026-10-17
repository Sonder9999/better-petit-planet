using System.Windows;
using System.Windows.Media;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Skins;
using BetterPetitPlanet.View.Controls.Music;
using BetterPetitPlanet.ViewModel.Windows;

namespace BetterPetitPlanet.Test;

public sealed class MusicSkinConsumerTest
{
    private sealed class MemoryConfig : IConfigService
    {
        public AppConfig Config { get; } = new();
        public void Save() { }
        public void Reload() { }
        public Task SaveMusicOverlayAsync(MusicOverlayConfig settings)
        {
            Config.MusicOverlay = settings.Clone();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task MissingSelection_ShowsReasonWithoutChangingRequestedOrPersistedIdentity()
    {
        using var fixture = new MusicSkinFixture();
        var config = new MemoryConfig();
        using var vm = new MusicOverlaySettingsViewModel(config, skinRepository: new MusicSkinRepository(fixture.Root, limits: new()));
        vm.SelectedSkinId = "missing";
        await vm.LoadSelectedSkinAsync("missing");
        Assert.Same(MusicSkin.Default, vm.ActiveSkin);
        Assert.Equal("missing", vm.SelectedSkinId);
        Assert.Equal("default", config.Config.MusicOverlay.SkinId);
        Assert.Contains("未安装", Diagnostic(vm));
        await vm.ApplyAsync();
        Assert.Equal("missing", config.Config.MusicOverlay.SkinId);
        Assert.Contains("未安装", Diagnostic(vm));
        Assert.DoesNotContain("保存失败", vm.StatusMessage);
    }

    private static string? Diagnostic(MusicOverlaySettingsViewModel vm) =>
        vm.SkinDiagnostic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedSelection_ShowsRepositoryReasonAndKeepsDraft(bool budget)
    {
        using var fixture = new MusicSkinFixture();
        if (!budget) fixture.WriteManifest(version: 99);
        var repo = new MusicSkinRepository(fixture.Root, limits: new() { MaxDecodedBytes = budget ? 1 : 1024 * 1024 });
        using var vm = new MusicOverlaySettingsViewModel(new MemoryConfig(), skinRepository: repo);
        vm.SelectedSkinId = "fixture";
        await vm.LoadSelectedSkinAsync("fixture");
        Assert.Equal("fixture", vm.SelectedSkinId);
        Assert.Same(MusicSkin.Default, vm.ActiveSkin);
        Assert.Contains(budget ? "预算" : "schemaVersion", Diagnostic(vm));
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task Preview_DecodesBackgroundToPreviewBounds()
    {
        using var fixture = BackgroundFixture();
        using var vm = new MusicOverlaySettingsViewModel(new MemoryConfig(), skinRepository: new(fixture.Root, limits: new()));
        await vm.LoadSelectedSkinAsync("fixture");
        Assert.Equal(960, vm.ActiveSkin.BackgroundBitmap!.PixelWidth);
        Assert.Equal(540, vm.ActiveSkin.BackgroundBitmap.PixelHeight);
    }

    internal static MusicSkinFixture BackgroundFixture(int width = 1600, int height = 900)
    {
        var fixture = new MusicSkinFixture();
        fixture.WriteImage("background.png", width, height);
        fixture.WriteManifest();
        var path = Path.Combine(fixture.Package, "skin.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!;
        manifest["textures"]!["background"] = "background.png";
        File.WriteAllText(path, manifest.ToJsonString());
        return fixture;
    }

    [Fact]
    public async Task LateError_CannotReplaceNewSelectionOrDisposedDiagnostic()
    {
        using var fixture = new MusicSkinFixture();
        var repo = new MusicSkinRepository(fixture.Root, limits: new());
        var slow = new TaskCompletionSource<MusicSkin>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var vm = new MusicOverlaySettingsViewModel(new MemoryConfig(), skinRepository: repo,
            skinLoader: id => id == "slow" ? slow.Task : repo.LoadSkinAsync(id));
        var pending = vm.LoadSelectedSkinAsync("slow");
        await vm.LoadSelectedSkinAsync("fixture");
        slow.SetException(new InvalidOperationException("stale error"));
        await pending;
        Assert.Null(Diagnostic(vm));
        Assert.Equal("fixture", vm.ActiveSkin.Id);
        slow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = vm.LoadSelectedSkinAsync("slow");
        vm.Dispose();
        slow.SetException(new InvalidOperationException("disposed error"));
        await pending;
        Assert.Null(Diagnostic(vm));
        Assert.Same(MusicSkin.Default, vm.ActiveSkin);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewDetach_ReleasesAssetsAndRestoresRequestedSkin(bool unload) => RunSta(async () =>
    {
        using var fixture = new MusicSkinFixture();
        using var vm = new MusicOverlaySettingsViewModel(new MemoryConfig(), skinRepository: new(fixture.Root, limits: new()));
        vm.SelectedSkinId = "fixture";
        var view = new MusicOverlaySettingsControl { ViewModel = vm };
        using var host = MusicRenderingTestHost.Mount(view);
        var field = (MusicPlayfieldControl)view.FindName("PlayfieldPreview");
        await vm.LoadSelectedSkinAsync("fixture");
        MusicRenderingTestHost.Drain();
        Assert.NotNull(field.Skin.NoteTapBitmap);
        if (unload) host.RootVisual = null;
        else field.Visibility = Visibility.Collapsed;
        MusicRenderingTestHost.Drain();
        Assert.Null(vm.ActiveSkin.NoteTapBitmap);
        Assert.Null(field.Skin.NoteTapBitmap);
        for (int i = 0; i < 3; i++) Assert.True(MusicPlayfieldLifecycleTest.Layer(field, i).Bounds.IsEmpty);
        Assert.Equal("fixture", vm.SelectedSkinId);
        if (unload) host.RootVisual = view;
        else field.Visibility = Visibility.Visible;
        await Until(() => field.Skin.Id == "fixture");
        Assert.NotNull(field.Skin.NoteTapBitmap);
        Assert.False(field.CurrentSnapshot.IsPlaying);
    });

    [Fact]
    public async Task Dispose_DropsPreviewAssetReferences()
    {
        using var fixture = new MusicSkinFixture();
        using var vm = new MusicOverlaySettingsViewModel(new MemoryConfig(), skinRepository: new MusicSkinRepository(fixture.Root, limits: new()));
        await vm.LoadSelectedSkinAsync("fixture");
        Assert.NotNull(vm.ActiveSkin.NoteTapBitmap);
        vm.Dispose();
        Assert.Null(vm.ActiveSkin.NoteTapBitmap);
    }

    [Fact]
    public void ReleasedResources_LateSnapshotCannotRestartRendering() => MusicRenderingTestHost.Run(() =>
    {
        var control = new MusicPlayfieldControl();
        using var host = MusicRenderingTestHost.Mount(control);
        control.ReleaseSkinResources();
        control.UpdateSnapshot(MusicPlayfieldLifecycleTest.Snapshot() with { IsPlaying = true });
        Assert.False((bool)typeof(MusicPlayfieldControl).GetField("_isRenderingHooked",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(control)!);
        for (int i = 0; i < 3; i++) Assert.True(MusicPlayfieldLifecycleTest.Layer(control, i).Bounds.IsEmpty);
    });

    [Fact]
    public void Unload_DropsAllImageDrawingReferences() => MusicRenderingTestHost.Run(() =>
    {
        using var fixture = new MusicSkinFixture();
        var image = new MusicSkinAssetCache().GetOrLoadBitmap(fixture.Package, "note.png");
        var control = new MusicPlayfieldControl();
        using var host = MusicRenderingTestHost.Mount(control);
        control.SetSkin(new MusicSkin { BackgroundBitmap = image, NoteTapBitmap = image });
        Assert.NotNull(control.Skin.BackgroundBitmap);
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.Null(control.Skin.BackgroundBitmap);
        for (int i = 0; i < 3; i++) Assert.True(MusicPlayfieldLifecycleTest.Layer(control, i).Bounds.IsEmpty);
    });

    internal static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "Expected asynchronous consumer state was not reached");
    }

    internal static void RunSta(Func<Task> action)
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
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA consumer test timed out");
        if (error != null) throw new AggregateException(error);
    }
}
