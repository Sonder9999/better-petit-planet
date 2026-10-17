using System.Reflection;
using System.Windows;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask;
using BetterPetitPlanet.GameTask.AutoPick;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Overlay;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Rendering.Gpu;
using BetterPetitPlanet.GameTask.Music.Service;
using BetterPetitPlanet.GameTask.Music.Skins;
using BetterPetitPlanet.MusicOverlay.Gpu;
using BetterPetitPlanet.ViewModel.Windows;
using Newtonsoft.Json;

namespace BetterPetitPlanet.Test;

public sealed class MusicOverlayGpuIntegrationTest
{
    [Fact]
    public void Backend_DefaultMigrationCloneAndValidation_PreserveExplicitSelection()
    {
        Assert.Equal(MusicOverlayBackend.Wpf, new MusicOverlayConfig().Backend);
        var selected = new MusicOverlayConfig { Backend = MusicOverlayBackend.Gpu };
        var restored = JsonConvert.DeserializeObject<MusicOverlayConfig>(JsonConvert.SerializeObject(selected));
        var migrated = MusicOverlayConfigMigration.Migrate(restored);
        Assert.Equal(MusicOverlayBackend.Gpu, migrated.Backend);
        Assert.Equal(MusicOverlayBackend.Gpu, migrated.Clone().Backend);
        var applied = new MusicOverlayConfig();
        applied.ApplySettingsFrom(migrated);
        Assert.Equal(MusicOverlayBackend.Gpu, applied.Backend);
        applied.Backend = (MusicOverlayBackend)999;
        Assert.Throws<ArgumentException>(applied.Validate);
        Assert.Equal(MusicOverlayBackend.Wpf, MusicOverlayConfigMigration.Migrate(applied).Backend);
    }

    [Fact]
    public async Task Backend_SaveAndStartupReload_RetainGpuSelection()
    {
        string relative = "gpu-integration-tests/" + Guid.NewGuid().ToString("N") + ".json";
        string full = Path.Combine(AppContext.BaseDirectory, relative);
        try
        {
            var config = new ConfigService(configRelativePath: relative);
            await config.SaveMusicOverlayAsync(new MusicOverlayConfig { Backend = MusicOverlayBackend.Gpu });
            Assert.Equal(MusicOverlayBackend.Gpu, new ConfigService(configRelativePath: relative).Config.MusicOverlay.Backend);
        }
        finally
        {
            File.Delete(full);
            File.Delete(full + ".bak");
        }
    }

    [Fact]
    public void Settings_BackendDraftApplyResetAndReload_AreConnected() => InSta(async () =>
    {
        var config = new MemoryConfig();
        using var vm = new MusicOverlaySettingsViewModel(config);
        Assert.False(vm.UseGpuRenderer);
        vm.UseGpuRenderer = true;
        Assert.True(vm.IsDirty);
        await vm.ApplyAsync();
        Assert.Equal(MusicOverlayBackend.Gpu, config.Config.MusicOverlay.Backend);
        Assert.False(vm.IsDirty);
        vm.UseGpuRenderer = false;
        vm.LoadFromConfig();
        Assert.True(vm.UseGpuRenderer);
        Assert.False(vm.IsDirty);
        vm.ResetToDefault();
        Assert.False(vm.UseGpuRenderer);
        Assert.True(vm.IsDirty);
        await vm.ApplyAsync();
        Assert.Equal(MusicOverlayBackend.Wpf, config.Config.MusicOverlay.Backend);
    });

    [Fact]
    public void GpuLifecycle_UsesPhysicalBoundsAndHidesWhenGameLosesForeground() => InSta(() =>
    {
        using var fixture = new Fixture();
        using var presenter = fixture.CreatePresenter();
        presenter.Tick();
        Assert.True(presenter.IsGpuActive);
        Assert.Null(presenter.Window); // GPU does not create/present a WPF layered window.
        Assert.Equal((150, 300, 1920, 1080), fixture.Gpu.Bounds);
        Assert.NotNull(fixture.Gpu.Chart);
        fixture.Foreground = false;
        presenter.Tick();
        Assert.False(presenter.IsGpuActive);
        Assert.False(fixture.Gpu.Visible);
        Assert.False(fixture.Gpu.Disposed);
        fixture.Foreground = true;
        fixture.Bounds = new Rect(200, 400, 2560, 1440);
        presenter.Tick();
        Assert.True(presenter.IsGpuActive);
        Assert.Equal((200, 400, 2560, 1440), fixture.Gpu.Bounds);
        fixture.Exists = false;
        presenter.Tick();
        Assert.False(fixture.Gpu.Visible);
        fixture.Exists = true;
        presenter.Tick();
        Assert.True(fixture.Gpu.Visible);
        fixture.Config.Config.MusicOverlay.Enabled = false;
        presenter.Tick();
        Assert.False(fixture.Gpu.Visible);
        fixture.Config.Config.MusicOverlay.Enabled = true;
        presenter.Tick();
        Assert.True(fixture.Gpu.Visible);
        presenter.Dispose();
        Assert.True(fixture.Gpu.Disposed);
    });

    [Fact]
    public void GpuFailure_RestoresVisibleWpfInSameTickAndLeavesPlaybackReady() => InSta(() =>
    {
        using var fixture = new Fixture();
        using var presenter = fixture.CreatePresenter();
        presenter.Tick();
        fixture.Gpu.Failure = new InvalidOperationException("simulated device failure");
        presenter.Tick();
        Assert.False(presenter.IsGpuActive);
        Assert.True(fixture.Gpu.Disposed);
        Assert.NotNull(presenter.Window);
        Assert.True(presenter.Window.IsVisible);
        Assert.False(presenter.IsFailed);
        Assert.True(fixture.Coordinator.Ready);
        Assert.Contains("simulated device failure", presenter.BackendDiagnostic);
        Assert.Equal(1, fixture.GpuCreations); // Failure stays latched until an explicit retry.
        presenter.Tick();
        Assert.Equal(1, fixture.GpuCreations);
    });

    [Fact]
    public void UnsupportedSkin_UsesWpfAndExplainsFallback() => InSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Config.Config.MusicOverlay.SkinId = "builtin:unknown_fallback";
        using var presenter = fixture.CreatePresenter();
        presenter.Tick();
        Assert.Equal(0, fixture.GpuCreations);
        Assert.True(presenter.Window?.IsVisible);
        Assert.Contains("皮肤尚不支持", presenter.BackendDiagnostic);
    });

    [Fact]
    public void CustomSkin_UsesGpuAndPublishesActiveSkin() => InSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Config.Config.MusicOverlay.SkinId = "custom-test-theme";
        var customSkin = new MusicSkin
        {
            Id = "custom-test-theme",
            DisplayName = "Test Custom",
            IsCustom = true
        };
        using var presenter = fixture.CreatePresenter(_ => Task.FromResult(customSkin));
        presenter.Tick();
        Assert.True(presenter.IsGpuActive);
        Assert.Equal(1, fixture.GpuCreations);
        Assert.Contains("自定义皮肤", presenter.BackendDiagnostic);
        Assert.NotNull(fixture.Gpu.Skin);
        Assert.Equal("custom-test-theme", fixture.Gpu.Skin.Id);
        Assert.True(fixture.Gpu.Skin.IsCustom);
    });

    [Fact]
    public void SupportedReferenceSkin_Shougun_UsesGpuAndPublishesReferenceSkin() => InSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Config.Config.MusicOverlay.SkinId = "builtin:shougun";
        var shougunSkin = new MusicSkin
        {
            Id = "builtin:shougun",
            DisplayName = "雷电将军",
            Reference = new ReferenceSkinResources(
                new ReferenceSkinDefinition { Id = "shougun", DisplayName = "雷电将军" },
                new Dictionary<string, ReferenceSkinImage>())
        };
        using var presenter = fixture.CreatePresenter(_ => Task.FromResult(shougunSkin));
        presenter.Tick();
        Assert.True(presenter.IsGpuActive);
        Assert.Equal(1, fixture.GpuCreations);
        Assert.Contains("内置原版皮肤", presenter.BackendDiagnostic);
        Assert.NotNull(fixture.Gpu.Skin);
        Assert.Equal("builtin:shougun", fixture.Gpu.Skin.Id);
        Assert.NotNull(fixture.Gpu.Skin.Reference);
    });

    [Fact]
    public void SupportedReferenceSkin_Furina_UsesGpuAndPublishesReferenceSkin() => InSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Config.Config.MusicOverlay.SkinId = "builtin:furina";
        var furinaSkin = new MusicSkin
        {
            Id = "builtin:furina",
            DisplayName = "芙宁娜",
            Reference = new ReferenceSkinResources(
                new ReferenceSkinDefinition { Id = "furina", DisplayName = "芙宁娜" },
                new Dictionary<string, ReferenceSkinImage>())
        };
        using var presenter = fixture.CreatePresenter(_ => Task.FromResult(furinaSkin));
        presenter.Tick();
        Assert.True(presenter.IsGpuActive);
        Assert.Equal(1, fixture.GpuCreations);
        Assert.Contains("内置原版皮肤", presenter.BackendDiagnostic);
        Assert.NotNull(fixture.Gpu.Skin);
        Assert.Equal("builtin:furina", fixture.Gpu.Skin.Id);
        Assert.NotNull(fixture.Gpu.Skin.Reference);
    });

    [Fact]
    public void SupportedReferenceSkin_Neuvillette_UsesGpuAndPublishesReferenceSkin() => InSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Config.Config.MusicOverlay.SkinId = "builtin:neuvillette";
        var neuvilletteSkin = new MusicSkin
        {
            Id = "builtin:neuvillette",
            DisplayName = "那维莱特",
            Reference = new ReferenceSkinResources(
                new ReferenceSkinDefinition { Id = "neuvillette", DisplayName = "那维莱特" },
                new Dictionary<string, ReferenceSkinImage>())
        };
        using var presenter = fixture.CreatePresenter(_ => Task.FromResult(neuvilletteSkin));
        presenter.Tick();
        Assert.True(presenter.IsGpuActive);
        Assert.Equal(1, fixture.GpuCreations);
        Assert.Contains("内置原版皮肤", presenter.BackendDiagnostic);
        Assert.NotNull(fixture.Gpu.Skin);
        Assert.Equal("builtin:neuvillette", fixture.Gpu.Skin.Id);
        Assert.NotNull(fixture.Gpu.Skin.Reference);
    });

    [Theory]
    [InlineData("factory")]
    [InlineData("publish")]
    [InlineData("show")]
    public void StartupFailures_RestoreWpfAndCanRetryExplicitly(string stage) => InSta(() =>
    {
        using var fixture = new Fixture();
        fixture.FailureStage = stage;
        using var presenter = fixture.CreatePresenter();
        presenter.Tick();
        Assert.True(presenter.Window?.IsVisible);
        Assert.False(presenter.IsFailed);
        Assert.True(fixture.Coordinator.Ready);
        Assert.Contains("simulated", presenter.BackendDiagnostic);
        fixture.FailureStage = null;
        presenter.ResetRendererFailure();
        presenter.Tick();
        Assert.True(presenter.IsGpuActive);
        Assert.False(presenter.Window!.IsVisible);
        Assert.Equal(2, fixture.GpuCreations);
    });

    [Fact]
    public void SwitchingBackend_DisposesGpuAndResumesWithFreshSnapshot() => InSta(() =>
    {
        using var fixture = new Fixture();
        using var presenter = fixture.CreatePresenter();
        presenter.Tick();
        var firstGpu = fixture.Gpu;
        fixture.Config.Config.MusicOverlay.Backend = MusicOverlayBackend.Wpf;
        presenter.Tick();
        Assert.True(firstGpu.Disposed);
        Assert.True(presenter.Window?.IsVisible);
        fixture.Config.Config.MusicOverlay.Backend = MusicOverlayBackend.Gpu;
        presenter.Tick();
        Assert.True(presenter.IsGpuActive);
        Assert.False(presenter.Window!.IsVisible);
        Assert.NotSame(firstGpu, fixture.Gpu);
        Assert.NotNull(fixture.Gpu.Snapshot);
    });

    [Fact]
    public void LayoutAdjustment_TemporarilyUsesWpfAndReturnsToGpu() => InSta(async () =>
    {
        using var fixture = new Fixture();
        using var presenter = fixture.CreatePresenter();
        presenter.Tick();
        var firstGpu = fixture.Gpu;
        await presenter.SetAdjustingAsync(true);
        Assert.True(firstGpu.Disposed);
        Assert.True(presenter.Window?.IsVisible);
        Assert.Contains("调整布局", presenter.BackendDiagnostic);
        await presenter.SetAdjustingAsync(false);
        fixture.Coordinator.ReadinessGate.ObserveCapture(1000);
        fixture.Coordinator.ReadinessGate.ObserveDetection(true, 1000);
        fixture.Coordinator.ReadinessGate.ObserveDetection(true, 1000);
        fixture.Coordinator.Tick();
        presenter.Tick();
        Assert.True(presenter.IsGpuActive);
        Assert.False(presenter.Window!.IsVisible);
    });

    [Fact]
    public void SessionAndInputCallbacks_PublishWithoutWpfWindowOrRenderingHeartbeat() => InSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Playback.LoadTimeline(new PerformanceTimeline
        {
            Events = [new PerformanceEvent { Key = "A", TimeMs = 500 }], TotalDurationMs = 1000
        });
        using var presenter = fixture.CreatePresenter();
        presenter.Tick();
        Assert.Equal(1, fixture.Gpu.Chart!.NoteCount);
        var chart = fixture.Gpu.Chart;
        int initial = fixture.Gpu.Publications;
        var session = new MusicSessionSnapshot("gpu-test", "playing", 250, 1000, 1, 1000, true, 4, ["A"]);
        typeof(MusicOverlayPresenter).GetMethod("OnSession", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(presenter, [session]);
        Assert.Equal(initial + 1, fixture.Gpu.Publications);
        Assert.Equal("gpu-test", fixture.Gpu.Snapshot!.SessionId);
        Assert.True(fixture.Gpu.Snapshot.IsPlaying);
        typeof(MusicOverlayPresenter).GetMethod("OnInputObserved", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(presenter, [null]);
        Assert.Equal(initial + 2, fixture.Gpu.Publications);
        Assert.Same(chart, fixture.Gpu.Chart);
        Assert.Null(presenter.Window);
    });

    [Fact]
    public void OnInputObserved_DownAction_DirectlyPostsHitEventToGpuBackend() => InSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Config.Config.MusicOverlay.Backend = MusicOverlayBackend.Gpu;
        fixture.Config.Config.MusicOverlay.Enabled = true;
        using var presenter = fixture.CreatePresenter();
        presenter.Tick();

        var inputDown = new MusicInputEvent(
            LaneId: "D",
            Action: "down",
            Source: "automatic",
            ChartTimeMs: 12073,
            HostMonotonicMs: 5000,
            NoteId: "note-123",
            Revision: 1);

        typeof(MusicOverlayPresenter).GetMethod("OnInputObserved", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(presenter, [inputDown]);

        Assert.Single(fixture.Gpu.Hits);
        var hit = fixture.Gpu.Hits[0];
        Assert.Equal("D", hit.LaneKey);
        Assert.Equal(2, hit.LaneIndex);
        Assert.Equal(12073, hit.ChartTimeMs);
        Assert.Equal(5000, hit.HostMonotonicMs);
        Assert.Equal("note-123", hit.NoteId);
        Assert.Equal("automatic", hit.Source);

        var inputUp = new MusicInputEvent(
            LaneId: "D",
            Action: "up",
            Source: "automatic",
            ChartTimeMs: 12118,
            HostMonotonicMs: 5045,
            NoteId: "note-123",
            Revision: 1);

        typeof(MusicOverlayPresenter).GetMethod("OnInputObserved", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(presenter, [inputUp]);

        // KeyUp does not post a new hit event
        Assert.Single(fixture.Gpu.Hits);
    });

    private static void InSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw new AggregateException(error);
    }

    private static void InSta(Func<Task> action) => InSta(() => action().GetAwaiter().GetResult());

    private sealed class MemoryConfig : IConfigService
    {
        public AppConfig Config { get; } = new();
        public void Save() { }
        public Task SaveAsync() => Task.CompletedTask;
        public void Reload() { }
        public Task SaveMusicOverlayAsync(MusicOverlayConfig settings)
        {
            Config.MusicOverlay.ApplySettingsFrom(settings);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGpu : IMusicOverlayGpuBackend
    {
        public Exception? Failure { get; set; }
        public GpuOverlayStatistics Statistics => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        public bool Visible, Disposed;
        public int Publications;
        public (int, int, int, int) Bounds;
        public MusicRenderSnapshot? Snapshot;
        public MusicChartIndex? Chart;
        public string? FailureStage;
        public MusicSkin? Skin;
        public readonly List<MusicGpuHitEvent> Hits = [];
        public void PostHitEvent(in MusicGpuHitEvent hit) => Hits.Add(hit);
        public void ClearHits() => Hits.Clear();
        public void Publish(MusicRenderSnapshot snapshot, MusicChartIndex chart, MusicOverlayConfig config, MusicSkin? skin = null)
        {
            if (FailureStage == "publish") throw new InvalidOperationException("simulated publish failure");
            Snapshot = snapshot; Chart = chart; Skin = skin; Publications++;
        }
        public void SetBounds(int x, int y, int width, int height) => Bounds = (x, y, width, height);
        public void Show()
        {
            if (FailureStage == "show") throw new InvalidOperationException("simulated show failure");
            Visible = true;
        }
        public void Hide() => Visible = false;
        public void Dispose() { Visible = false; Disposed = true; }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly MemoryConfig Config = new();
        public FakeGpu Gpu = new();
        public readonly MusicPlaybackService Playback;
        public readonly MusicSessionCoordinator Coordinator;
        private readonly GameProcessDetector _process;
        public bool Foreground = true, Exists = true;
        public Rect Bounds = new(150, 300, 1920, 1080);
        public int GpuCreations;
        public string? FailureStage;

        public Fixture()
        {
            Config.Config.MusicOverlay.Enabled = true;
            Config.Config.MusicOverlay.Backend = MusicOverlayBackend.Gpu;
            Config.Config.Music.AutoFocusGameOnPlayback = false;
            _process = new GameProcessDetector(Config);
            Playback = new MusicPlaybackService(Config, _process, new KeyInputTransports(), clock: new MusicSessionClock(() => 0));
            Coordinator = new MusicSessionCoordinator(Config, Playback, new InstrumentDetector(Config, null, null),
                _process, new GameTaskManager(Config, _process, new TaskTriggerDispatcher()),
                new AutoPickTrigger(Config, null!, null!), new MusicCaptureObserver(),
                isGameRunning: () => Exists, isGameForeground: () => Foreground, isCaptureRunning: () => true, clockNow: () => 1000);
            Coordinator.ReadinessGate.ObserveCapture(1000);
            Coordinator.ReadinessGate.ObserveDetection(true, 1000);
            Coordinator.ReadinessGate.ObserveDetection(true, 1000);
            Coordinator.Tick();
            Assert.True(Coordinator.Ready);
        }

        public MusicOverlayPresenter CreatePresenter(Func<string, Task<MusicSkin>>? skinLoader = null) => new(Config, Coordinator, _process,
            tracker: new GameWindowTracker(() => new GameWindowState(Exists ? new IntPtr(1) : IntPtr.Zero, Foreground, Bounds, 144)),
            skinLoader: skinLoader,
            gpuBackendFactory: () =>
            {
                GpuCreations++;
                if (FailureStage == "factory") throw new InvalidOperationException("simulated factory failure");
                if (Gpu.Disposed) Gpu = new FakeGpu();
                Gpu.FailureStage = FailureStage;
                return Gpu;
            });

        public void Dispose() { Coordinator.Dispose(); Playback.Dispose(); }
    }
}
