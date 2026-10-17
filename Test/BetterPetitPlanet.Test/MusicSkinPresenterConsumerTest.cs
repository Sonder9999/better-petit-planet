using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask;
using BetterPetitPlanet.GameTask.AutoPick;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Overlay;
using BetterPetitPlanet.GameTask.Music.Rendering;
using BetterPetitPlanet.GameTask.Music.Service;
using BetterPetitPlanet.GameTask.Music.Skins;
using BetterPetitPlanet.View.Controls.Music;
using BetterPetitPlanet.ViewModel.Windows;

namespace BetterPetitPlanet.Test;

public sealed class MusicSkinPresenterConsumerTest
{
    private sealed class MemoryConfig : IConfigService
    {
        public AppConfig Config { get; } = new();
        public void Save() { }
        public void Reload() { }
        public Task SaveMusicOverlayAsync(MusicOverlayConfig settings)
        {
            Config.MusicOverlay.ApplySettingsFrom(settings);
            return Task.CompletedTask;
        }
    }

    private sealed class Input : IMusicInputTransport
    {
        public IReadOnlyList<MusicInputResult> Submit(IReadOnlyList<MusicKeyCommand> commands, IntPtr target, MusicInputMode mode) =>
            commands.Select(c => new MusicInputResult(c, true)).ToArray();
    }

    private sealed class Runtime : IDisposable
    {
        public MemoryConfig Settings { get; } = new();
        public bool Foreground = true;
        public Rect Bounds = new(0, 0, 1200, 675);
        public MusicPlaybackService Playback { get; }
        public MusicSessionCoordinator Session { get; }
        public MusicOverlayPresenter Presenter { get; }

        public Runtime(MusicSkinRepository repo, string id = "fixture", Func<string, Task<MusicSkin>>? loader = null)
        {
            Settings.Config.MusicOverlay.Enabled = true;
            Settings.Config.MusicOverlay.SkinId = id;
            Settings.Config.Music.AutoFocusGameOnPlayback = false;
            var process = new GameProcessDetector(Settings);
            Playback = new(Settings, process, new KeyInputTransports(), clock: new MusicSessionClock(() => 0), inputTransport: new Input());
            Session = new(Settings, Playback, new InstrumentDetector(Settings, null, null), process,
                new GameTaskManager(Settings, process, new TaskTriggerDispatcher()), new AutoPickTrigger(Settings, null!, null!),
                new MusicCaptureObserver(), isGameRunning: () => true, isGameForeground: () => true,
                isCaptureRunning: () => true, clockNow: () => 1000);
            Session.ReadinessGate.ObserveCapture(1000);
            Session.ReadinessGate.ObserveDetection(true, 1000);
            Session.ReadinessGate.ObserveDetection(true, 1000);
            Session.Tick();
            Presenter = new(Settings, Session, process, skinRepository: repo, skinLoader: loader,
                tracker: new GameWindowTracker(() => new(new IntPtr(1), Foreground, Bounds, 144)));
        }

        public void Tick() => Presenter.Tick();
        public Task Loaded(string id) => MusicSkinConsumerTest.Until(() => { Tick(); return Presenter.Window?.PlayfieldControl.Skin.Id == id; });
        public void Dispose() { Presenter.Dispose(); Session.Dispose(); Playback.Dispose(); }
    }

    [Fact]
    public void OverlayBudgetDiagnostic_IsVisibleInSettingsWithoutBecomingSaveFailure() => MusicSkinConsumerTest.RunSta(async () =>
    {
        using var fixture = new MusicSkinFixture();
        using var runtime = new Runtime(new(fixture.Root, limits: new() { MaxDecodedBytes = 1 }));
        using var vm = new MusicOverlaySettingsViewModel(runtime.Settings, runtime.Presenter, skinRepository: new(fixture.Root, limits: new()));
        var view = new MusicOverlaySettingsControl { ViewModel = vm };
        using var host = MusicRenderingTestHost.Mount(view);
        await vm.LoadSelectedSkinAsync("fixture");
        runtime.Tick();
        await MusicSkinConsumerTest.Until(() => runtime.Presenter.SkinDiagnostic != null);
        MusicRenderingTestHost.Drain();
        Assert.Equal("fixture", vm.ActiveSkin.Id);
        Assert.Contains("预算", Assert.IsType<TextBlock>(view.FindName("OverlaySkinDiagnosticText")).Text);
        Assert.False(runtime.Presenter.IsFailed);
        Assert.DoesNotContain("保存失败", vm.StatusMessage);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void HiddenOrDisposedDuringLoad_DropsLateResultsAndDiagnostics(bool dispose, bool fail) => MusicSkinConsumerTest.RunSta(async () =>
    {
        using var fixture = new MusicSkinFixture();
        var repo = new MusicSkinRepository(fixture.Root, limits: new());
        var pending = new TaskCompletionSource<MusicSkin>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var runtime = new Runtime(repo, "fixture", _ => pending.Task);
        runtime.Tick();
        var control = runtime.Presenter.Window!.PlayfieldControl;
        if (dispose) runtime.Presenter.Dispose();
        else { runtime.Foreground = false; runtime.Tick(); }
        if (fail) pending.SetException(new InvalidOperationException("late failure"));
        else pending.SetResult(await repo.LoadSkinAsync("fixture"));
        await Task.Delay(30);
        Assert.Null(control.Skin.NoteTapBitmap);
        Assert.Null(runtime.Presenter.SkinDiagnostic);
        Assert.False(runtime.Presenter.IsFailed);
        for (int i = 0; i < 3; i++) Assert.True(MusicPlayfieldLifecycleTest.Layer(control, i).Bounds.IsEmpty);
    });

    [Fact]
    public void Overlay_UsesPhysicalBoundsAndClampsDecodeSize() => MusicSkinConsumerTest.RunSta(async () =>
    {
        using var fixture = MusicSkinConsumerTest.BackgroundFixture(2400, 1350);
        using var runtime = new Runtime(new(fixture.Root, limits: new()));
        await runtime.Loaded("fixture");
        var bitmap = runtime.Presenter.Window!.PlayfieldControl.Skin.BackgroundBitmap!;
        Assert.Equal(1200, bitmap.PixelWidth); // 800 DIP at 144 DPI is 1200 physical pixels.
        Assert.Equal(675, bitmap.PixelHeight);
        runtime.Bounds = new Rect(0, 0, 3840, 2160);
        runtime.Tick();
        await MusicSkinConsumerTest.Until(() => runtime.Presenter.Window.PlayfieldControl.Skin.BackgroundBitmap?.PixelWidth == 1920);
        Assert.Equal(1080, runtime.Presenter.Window.PlayfieldControl.Skin.BackgroundBitmap!.PixelHeight);
    });

    [Fact]
    public void PresentationFailure_ReleasesActiveAssetsImmediately() => MusicSkinConsumerTest.RunSta(async () =>
    {
        using var fixture = new MusicSkinFixture();
        using var runtime = new Runtime(new(fixture.Root, limits: new()));
        await runtime.Loaded("fixture");
        runtime.Presenter.Changed += () => throw new InvalidOperationException("failed observer");
        var status = await runtime.Presenter.ApplySettingsAsync(runtime.Settings.Config.MusicOverlay.Clone());
        Assert.Contains("已保存", status);
        Assert.True(runtime.Presenter.IsFailed);
        Assert.Null(runtime.Presenter.Window!.PlayfieldControl.Skin.NoteTapBitmap);
        Assert.Null(typeof(MusicOverlayPresenter).GetField("_activeSkin", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime.Presenter));
    });

    [Fact]
    public void HiddenAndDisposed_ReleaseAssets_AndRestoreRequestedIdentity() => MusicSkinConsumerTest.RunSta(async () =>
    {
        using var fixture = new MusicSkinFixture();
        using var runtime = new Runtime(new(fixture.Root, limits: new()));
        await runtime.Loaded("fixture");
        var control = runtime.Presenter.Window!.PlayfieldControl;
        runtime.Foreground = false;
        runtime.Tick();
        Assert.Null(control.Skin.NoteTapBitmap);
        Assert.Null(typeof(MusicOverlayPresenter).GetField("_activeSkin", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime.Presenter));
        for (int i = 0; i < 3; i++) Assert.True(MusicPlayfieldLifecycleTest.Layer(control, i).Bounds.IsEmpty);
        runtime.Foreground = true;
        await runtime.Loaded("fixture");
        Assert.NotNull(control.Skin.NoteTapBitmap);
        runtime.Presenter.Dispose();
        Assert.Null(control.Skin.NoteTapBitmap);
        Assert.Null(typeof(MusicOverlayPresenter).GetField("_activeSkin", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime.Presenter));
    });

    [Fact]
    public void QueuedSelection_DoesNotHideActiveSkinReloadFailure() => MusicSkinConsumerTest.RunSta(async () =>
    {
        using var fixture = new MusicSkinFixture();
        using var runtime = new Runtime(new(fixture.Root, limits: new()));
        await runtime.Loaded("fixture");
        runtime.Playback.LoadTimeline(new PerformanceTimeline
        {
            Events = [new PerformanceEvent { Key = "A", TimeMs = 1000, DurationMs = 100, Kind = MusicNoteKind.Tap }], TotalDurationMs = 3000
        });
        runtime.Playback.Play();
        var next = runtime.Settings.Config.MusicOverlay.Clone();
        next.SkinId = "default";
        await runtime.Presenter.ApplySettingsAsync(next);
        runtime.Foreground = false;
        runtime.Tick();
        File.WriteAllText(Path.Combine(fixture.Package, "note.png"), "invalid PNG");
        runtime.Foreground = true;
        runtime.Tick();
        await MusicSkinConsumerTest.Until(() => runtime.Presenter.SkinDiagnostic != null);
        Assert.Contains("fixture", runtime.Presenter.SkinDiagnostic);
        runtime.Playback.Pause();
        runtime.Tick();
        Assert.Null(runtime.Presenter.SkinDiagnostic);
    });

    [Fact]
    public void MissingThenInstalled_ReShowRetriesRequestedIdAndClearsDiagnostic() => MusicSkinConsumerTest.RunSta(async () =>
    {
        using var fixture = new MusicSkinFixture();
        string manifest = Path.Combine(fixture.Package, "skin.json");
        File.Move(manifest, manifest + ".bak");
        using var runtime = new Runtime(new(fixture.Root, limits: new()));
        runtime.Tick();
        await MusicSkinConsumerTest.Until(() => Diagnostic(runtime.Presenter) != null);
        Assert.Equal("fixture", runtime.Settings.Config.MusicOverlay.SkinId);
        runtime.Foreground = false;
        runtime.Tick();
        File.Move(manifest + ".bak", manifest);
        runtime.Foreground = true;
        await runtime.Loaded("fixture");
        Assert.Null(Diagnostic(runtime.Presenter));
    });

    [Theory]
    [InlineData(0)]
    [InlineData(3000)]
    public void PlayingOrPreparing_HideReshowKeepsActiveSkinUntilPause(int preparation) => MusicSkinConsumerTest.RunSta(async () =>
    {
        using var fixture = new MusicSkinFixture();
        using var runtime = new Runtime(new(fixture.Root, limits: new()));
        await runtime.Loaded("fixture");
        runtime.Playback.ConfigurePreparation(preparation);
        runtime.Playback.LoadTimeline(new PerformanceTimeline
        {
            Events = [new PerformanceEvent { Key = "A", TimeMs = 1000, DurationMs = 100, Kind = MusicNoteKind.Tap }], TotalDurationMs = 3000
        });
        runtime.Playback.Play();
        var settings = runtime.Settings.Config.MusicOverlay.Clone();
        settings.SkinId = "default";
        Assert.Contains("暂停或停止", await runtime.Presenter.ApplySettingsAsync(settings));
        runtime.Foreground = false;
        runtime.Tick();
        runtime.Foreground = true;
        await runtime.Loaded("fixture");
        runtime.Playback.Pause();
        await runtime.Loaded("default");
        Assert.Null(runtime.Presenter.Window!.PlayfieldControl.Skin.NoteTapBitmap);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaleInitialLoad_CannotReplaceNewSelectionOrReportRendererFailure(bool fail) => MusicSkinConsumerTest.RunSta(async () =>
    {
        using var fixture = new MusicSkinFixture();
        var repo = new MusicSkinRepository(fixture.Root, limits: new());
        var slow = new TaskCompletionSource<MusicSkin>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var runtime = new Runtime(repo, "slow", id => id == "slow" ? slow.Task : repo.LoadSkinAsync(id));
        runtime.Tick();
        var next = runtime.Settings.Config.MusicOverlay.Clone();
        next.SkinId = "fixture";
        await runtime.Presenter.ApplySettingsAsync(next);
        if (fail) slow.SetException(new InvalidOperationException("stale failure"));
        else slow.SetResult(new MusicSkin { Id = "slow" });
        await runtime.Loaded("fixture");
        await Task.Delay(30);
        Assert.False(runtime.Presenter.IsFailed);
        Assert.Null(Diagnostic(runtime.Presenter));
        Assert.Equal("fixture", runtime.Presenter.Window!.PlayfieldControl.Skin.Id);
    });

    private static string? Diagnostic(MusicOverlayPresenter presenter) =>
        presenter.SkinDiagnostic;

    [Fact]
    public void RuntimeAnimationFailure_RemainsDefaultAcrossTicksAndResizeUntilNewSelection() => MusicSkinConsumerTest.RunSta(async () =>
    {
        using var fixture = new MusicSkinFixture();
        var repo = new MusicSkinRepository(fixture.Root);
        var mockBitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Pbgra32, null, new byte[16 * 16 * 4], 16 * 4);
        var definition = new ReferenceSkinDefinition
        {
            Id = "missing-test",
            NoteTextures = ["hold.png", "tap.png"],
            JudgePointTexture = "point.png",
            Hit = new ReferenceHitAnimation { Url = "assets/fx/missing-test.png", FrameCount = 60, Fps = 60, DisplaySize = 576 },
        };
        var images = new Dictionary<string, ReferenceSkinImage>
        {
            ["hold.png"] = new(mockBitmap, 16, 16),
            ["tap.png"] = new(mockBitmap, 16, 16),
            ["point.png"] = new(mockBitmap, 16, 16)
        };
        var broken = new MusicSkin
        {
            Id = "builtin:broken",
            NoteHoldBitmap = mockBitmap,
            NoteTapBitmap = mockBitmap,
            NotePointBitmap = mockBitmap,
            Reference = new ReferenceSkinResources(definition, images)
        };
        int loads = 0;
        using var runtime = new Runtime(repo, broken.Id, _ => { loads++; return Task.FromResult(broken); });
        await runtime.Loaded(broken.Id);
        var control = runtime.Presenter.Window!.PlayfieldControl;
        control.EffectsConfig = new MusicOverlayEffectsConfig { BackgroundEnabled = false };
        control.SetChart(new PerformanceTimeline { Events = [new() { Key = "A", TimeMs = 3000 }] });
        control.RenderSnapshot(MusicRenderSnapshot.Empty with { SessionId = "preview-runtime-fallback", PositionMs = 3100 }, 960, 540);
        Assert.Same(MusicSkin.Default, control.Skin);
        string diagnostic = Assert.IsType<string>(control.SkinRenderDiagnostic);
        for (int i = 0; i < 3; i++) runtime.Tick();
        Assert.Equal(diagnostic, runtime.Presenter.SkinDiagnostic);
        Assert.Same(MusicSkin.Default, typeof(MusicOverlayPresenter).GetField("_activeSkin", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime.Presenter));
        runtime.Bounds = new Rect(0, 0, 1600, 900);
        runtime.Tick();
        Assert.Equal(1, loads);
        Assert.Equal(diagnostic, runtime.Presenter.SkinDiagnostic);
        Assert.False(runtime.Presenter.IsFailed);

        control.SetChart(null);
        await runtime.Presenter.ApplySettingsAsync(runtime.Settings.Config.MusicOverlay.Clone());
        Assert.Equal(2, loads);
        Assert.Same(broken, control.Skin);
        Assert.Null(runtime.Presenter.SkinDiagnostic);
    });
}
