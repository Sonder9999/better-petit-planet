using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask;
using BetterPetitPlanet.GameTask.AutoPick;
using BetterPetitPlanet.GameTask.Music.Model;
using BetterPetitPlanet.GameTask.Music.Overlay;
using BetterPetitPlanet.GameTask.Music.Service;
using BetterPetitPlanet.GameTask.Music.Skins;
using BetterPetitPlanet.ViewModel.Windows;
using Newtonsoft.Json;
using Xunit;

namespace BetterPetitPlanet.Test;

public sealed class MusicOverlaySettingsTransactionTest
{
    private sealed class DeferredConfig : IConfigService, IDisposable
    {
        private readonly string _relativePath = "overlay-transaction-" + Guid.NewGuid().ToString("N") + ".json";
        public ConfigService Inner { get; }
        public AppConfig Config => Inner.Config;
        public string FilePath => Path.Combine(AppContext.BaseDirectory, _relativePath);
        public TaskCompletionSource? ReleaseSave { get; set; }
        public TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SaveCount { get; private set; }
        public DeferredConfig() => Inner = new ConfigService(configRelativePath: _relativePath);
        public void Save() => throw new NotSupportedException();
        public async Task SaveAsync()
        {
            SaveCount++;
            SaveEntered.TrySetResult();
            if (ReleaseSave != null) await ReleaseSave.Task;
            await Inner.SaveAsync();
        }
        public async Task SaveMusicOverlayAsync(MusicOverlayConfig settings)
        {
            SaveCount++;
            SaveEntered.TrySetResult();
            if (ReleaseSave != null) await ReleaseSave.Task;
            await Inner.SaveMusicOverlayAsync(settings);
        }
        public void Reload() { }
        public AppConfig ReadSaved() => JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(FilePath))!;
        public void BlockSave(bool backup = false) => Directory.CreateDirectory(FilePath + (backup ? ".bak" : ".tmp"));
        public void UnblockSave(bool backup = false) => Directory.Delete(FilePath + (backup ? ".bak" : ".tmp"));
        public void Dispose()
        {
            foreach (var path in new[] { FilePath, FilePath + ".tmp", FilePath + ".bak" })
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Delete(path);
                        if (Directory.Exists(path)) Directory.Delete(path, true);
                        break;
                    }
                    catch (IOException) when (attempt < 4)
                    {
                        Thread.Sleep(25);
                    }
                    catch { break; }
                }
            }
        }
    }

    private sealed class Input : IMusicInputTransport
    {
        public List<MusicKeyCommand> Commands { get; } = [];
        public IReadOnlyList<MusicInputResult> Submit(IReadOnlyList<MusicKeyCommand> commands, IntPtr target, MusicInputMode mode)
        {
            Commands.AddRange(commands);
            return commands.Select(command => new MusicInputResult(command, true)).ToArray();
        }
    }

    private sealed class Runtime : IDisposable
    {
        public Input Input { get; } = new();
        public MusicPlaybackService Playback { get; }
        public MusicSessionCoordinator Session { get; }
        public MusicOverlayPresenter Presenter { get; }
        public bool Foreground { get; set; } = true;
        public Runtime(DeferredConfig config, Func<string, Task<MusicSkin>>? skinLoader = null)
        {
            config.Config.MusicOverlay.Enabled = true;
            config.Config.Music.AutoFocusGameOnPlayback = false;
            var process = new GameProcessDetector(config);
            Playback = new MusicPlaybackService(config, process, new KeyInputTransports(),
                clock: new MusicSessionClock(() => 0), inputTransport: Input);
            Session = new MusicSessionCoordinator(config, Playback, new InstrumentDetector(config, null, null),
                process, new GameTaskManager(config, process, new TaskTriggerDispatcher()),
                new AutoPickTrigger(config, null!, null!), new MusicCaptureObserver(),
                isGameRunning: () => true, isGameForeground: () => true, isCaptureRunning: () => true, clockNow: () => 1000);
            Presenter = new MusicOverlayPresenter(config, Session, process,
                skinLoader: skinLoader ?? LoadTestSkin,
                tracker: new GameWindowTracker(() => new GameWindowState(new IntPtr(1), Foreground, new Rect(0, 0, 800, 600), 96)));
            Session.ReadinessGate.ObserveCapture(1000);
            Session.ReadinessGate.ObserveDetection(true, 1000);
            Session.ReadinessGate.ObserveDetection(true, 1000);
            Session.Tick();
            Presenter.Tick();
            Assert.NotNull(Presenter.Window);
            Foreground = false;
            Presenter.Tick();
        }
        public void Dispose()
        {
            Presenter.Dispose();
            Session.Dispose();
            Playback.Dispose();
        }
    }

    private static Task<MusicSkin> LoadTestSkin(string id) => Task.FromResult(new MusicSkin { Id = id });

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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SaveFailure_RetainsCommittedConfigReferencesAndRuntime_AndAllowsRetry(bool withPresenter, bool backupFailure) => RunSta(async () =>
    {
        using var config = new DeferredConfig();
        using var runtime = withPresenter ? new Runtime(config) : null;
        await config.Inner.SaveAsync();
        var originalJson = File.ReadAllText(config.FilePath);
        var root = config.Config;
        var overlay = root.MusicOverlay;
        var layout = overlay.Layout;
        var effects = overlay.Effects;
        using var vm = new MusicOverlaySettingsViewModel(config, runtime?.Presenter);
        vm.FallDurationMs = 4500;
        vm.LayoutScale = 1.4;
        vm.HitBursts = false;
        vm.SelectedSkinId = "nahida";
        config.BlockSave(backupFailure);

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(originalJson, File.ReadAllText(config.FilePath));
        Assert.Same(root, config.Config);
        Assert.Same(overlay, root.MusicOverlay);
        Assert.Same(layout, overlay.Layout);
        Assert.Same(effects, overlay.Effects);
        Assert.Equal(3000, overlay.FallDurationMs);
        Assert.Equal(1, layout.Scale);
        Assert.True(effects.HitBursts);
        Assert.Equal("default", overlay.SkinId);
        Assert.True(vm.IsDirty);
        Assert.Equal(4500, vm.FallDurationMs);
        Assert.Contains("失败", vm.StatusMessage);
        if (runtime != null)
        {
            Assert.Equal(3000, runtime.Presenter.Window!.PlayfieldControl.FallDurationMs);
            Assert.Equal("default", runtime.Presenter.Window.PlayfieldControl.Skin.Id);
            Assert.Empty(runtime.Input.Commands);
        }

        config.UnblockSave(backupFailure);
        await vm.ApplyAsync();
        Assert.False(vm.IsDirty);
        Assert.Equal(4500, config.ReadSaved().MusicOverlay.FallDurationMs);
        Assert.Equal(4500, overlay.FallDurationMs);
        Assert.Same(layout, overlay.Layout);
        Assert.Same(effects, overlay.Effects);
        Assert.Equal(originalJson, File.ReadAllText(config.FilePath + ".bak"));
        if (runtime != null)
            Assert.Equal(4500, runtime.Presenter.Window!.PlayfieldControl.FallDurationMs);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_UsesStartSnapshot_AndRetainsNewerDraftWhilePreventingReentry(bool withPresenter) => RunSta(async () =>
    {
        using var config = new DeferredConfig { ReleaseSave = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var runtime = withPresenter ? new Runtime(config) : null;
        using var vm = new MusicOverlaySettingsViewModel(config, runtime?.Presenter);
        vm.FallDurationMs = 4500;
        var apply = vm.ApplyAsync();
        await config.SaveEntered.Task;
        vm.FallDurationMs = 6000;
        var repeatedApply = vm.ApplyAsync();
        try
        {
            Assert.True(repeatedApply.IsCompleted, "A second apply must be rejected while the first is saving");
            Assert.Equal(1, config.SaveCount);
            Assert.Equal(3000, config.Config.MusicOverlay.FallDurationMs);
            await config.Inner.SaveAsync();
            Assert.Equal(3000, config.ReadSaved().MusicOverlay.FallDurationMs);
        }
        finally
        {
            config.ReleaseSave.TrySetResult();
            await Task.WhenAll(apply, repeatedApply);
        }
        await apply;
        Assert.Equal(4500, config.Config.MusicOverlay.FallDurationMs);
        Assert.Equal(4500, config.ReadSaved().MusicOverlay.FallDurationMs);
        Assert.Equal(6000, vm.FallDurationMs);
        Assert.True(vm.IsDirty);
        Assert.DoesNotContain("成功应用", vm.StatusMessage);
        if (runtime != null)
            Assert.Equal(4500, runtime.Presenter.Window!.PlayfieldControl.FallDurationMs);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidSkin_DoesNotMutateConfigOrRuntime(bool withPresenter) => RunSta(async () =>
    {
        using var config = new DeferredConfig();
        using var runtime = withPresenter ? new Runtime(config) : null;
        using var vm = new MusicOverlaySettingsViewModel(config, runtime?.Presenter);
        vm.SelectedSkinId = " ";
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.True(vm.HasValidationError);
        Assert.True(vm.IsDirty);
        Assert.Contains("失败", vm.StatusMessage);
        Assert.Equal("default", config.Config.MusicOverlay.SkinId);
        Assert.Equal(0, config.SaveCount);
        if (runtime != null) Assert.Equal("default", runtime.Presenter.Window!.PlayfieldControl.Skin.Id);
    });

    [Theory]
    [InlineData(0)]
    [InlineData(3000)]
    public void SuccessfulSave_QueuesSkinUntilPlaybackPauses_AndFailedSavePreservesQueue(int preparationMs) => RunSta(async () =>
    {
        using var config = new DeferredConfig();
        using var runtime = new Runtime(config);
        runtime.Playback.ConfigurePreparation(preparationMs);
        runtime.Playback.LoadTimeline(new PerformanceTimeline
        {
            Events = [new PerformanceEvent { Key = "A", TimeMs = 0, DurationMs = 2500, Kind = MusicNoteKind.Hold }],
            TotalDurationMs = 2500
        });
        runtime.Playback.Play();
        Assert.Equal(PlayerState.Playing, runtime.Playback.State);
        Assert.Equal(preparationMs > 0, runtime.Playback.CaptureSnapshot().PositionMs < 0);
        int inputCount = runtime.Input.Commands.Count;
        using var vm = new MusicOverlaySettingsViewModel(config, runtime.Presenter, skinLoader: LoadTestSkin);
        vm.SelectedSkinId = "test-skin";
        await vm.LoadSelectedSkinAsync("test-skin");
        Assert.Equal("test-skin", vm.ActiveSkin.Id);
        await vm.ApplyAsync();
        Assert.Equal("test-skin", config.ReadSaved().MusicOverlay.SkinId);
        Assert.Equal("default", runtime.Presenter.Window!.PlayfieldControl.Skin.Id);
        Assert.Contains("暂停或停止后", vm.StatusMessage);
        Assert.Equal(inputCount, runtime.Input.Commands.Count);

        vm.SelectedSkinId = "default";
        config.BlockSave();
        await vm.ApplyAsync();
        Assert.Contains("保存失败", vm.StatusMessage);
        Assert.Equal("test-skin", config.Config.MusicOverlay.SkinId);
        Assert.Equal("default", runtime.Presenter.Window.PlayfieldControl.Skin.Id);

        runtime.Playback.Pause();
        runtime.Foreground = true;
        runtime.Presenter.Tick();
        Assert.Equal("test-skin", runtime.Presenter.Window.PlayfieldControl.Skin.Id);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposeDuringSave_CompletesAcceptedSnapshotWithoutUpdatingClosedViewModel(bool withPresenter) => RunSta(async () =>
    {
        using var config = new DeferredConfig { ReleaseSave = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var runtime = withPresenter ? new Runtime(config) : null;
        using var vm = new MusicOverlaySettingsViewModel(config, runtime?.Presenter);
        vm.FallDurationMs = 4500;
        var apply = vm.ApplyAsync();
        await config.SaveEntered.Task;
        vm.FallDurationMs = 6000;
        vm.Dispose();
        runtime?.Presenter.Dispose();
        int notifications = 0;
        vm.PropertyChanged += (_, _) => notifications++;
        config.ReleaseSave.SetResult();
        await apply;
        await vm.ApplyAsync();
        Assert.Equal(0, notifications);
        Assert.Equal(4500, config.ReadSaved().MusicOverlay.FallDurationMs);
        Assert.Equal(4500, config.Config.MusicOverlay.FallDurationMs);
        Assert.Equal(1, config.SaveCount);
        if (runtime != null) Assert.Null(runtime.Presenter.Window);
    });

    [Fact]
    public void InvalidNumericDraft_IsDirtyEvenWhenFirstEditIsNaN() => RunSta(async () =>
    {
        using var config = new DeferredConfig();
        using var vm = new MusicOverlaySettingsViewModel(config);
        vm.LayoutScale = double.NaN;
        await vm.ApplyAsync();
        Assert.True(vm.HasValidationError);
        Assert.True(vm.IsDirty);
        Assert.Equal(0, config.SaveCount);
    });

    [Fact]
    public void VisualFailureAfterSave_ReportsCommittedConfigurationInsteadOfSaveFailure() => RunSta(async () =>
    {
        using var config = new DeferredConfig();
        using var runtime = new Runtime(config);
        using var vm = new MusicOverlaySettingsViewModel(config, runtime.Presenter);
        runtime.Presenter.Changed += () => throw new InvalidOperationException("visual observer failed");
        vm.FallDurationMs = 4500;
        await vm.ApplyAsync();
        Assert.Equal(4500, config.ReadSaved().MusicOverlay.FallDurationMs);
        Assert.Equal(4500, config.Config.MusicOverlay.FallDurationMs);
        Assert.False(vm.IsDirty);
        Assert.Contains("已保存", vm.StatusMessage);
        Assert.Contains("视图应用失败", vm.StatusMessage);
        Assert.DoesNotContain("保存失败", vm.StatusMessage);
        Assert.True(runtime.Presenter.IsFailed);
    });

    [Fact]
    public async Task CandidateSave_PreservesCompatibilityAndOtherSettings_AndRejectsInvalidCandidate()
    {
        using var config = new DeferredConfig();
        var original = config.Config.MusicOverlay;
        original.ReadySuccessCount = 7;
        original.SnapshotIntervalMs = 100;
        config.Config.Hotkey.ToggleAutoPickHotkey = "F7";
        await config.Inner.SaveAsync();
        var snapshot = original.Clone();
        snapshot.ReadySuccessCount = 4;
        snapshot.SnapshotIntervalMs = 200;
        snapshot.FallDurationMs = 4500;
        await config.Inner.SaveMusicOverlayAsync(snapshot);
        var saved = config.ReadSaved();
        Assert.Equal(7, saved.MusicOverlay.ReadySuccessCount);
        Assert.Equal(100, saved.MusicOverlay.SnapshotIntervalMs);
        Assert.Equal("F7", saved.Hotkey.ToggleAutoPickHotkey);
        Assert.Equal(4500, original.FallDurationMs);
        Assert.Same(original, config.Config.MusicOverlay);
        var validJson = File.ReadAllText(config.FilePath);
        snapshot.Layout.Scale = double.NaN;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => config.Inner.SaveMusicOverlayAsync(snapshot));
        Assert.Equal(validJson, File.ReadAllText(config.FilePath));
        Assert.Equal(1, original.Layout.Scale);
    }

    [Fact]
    public void PreviewLateLoads_CannotReplaceNewSelectionOrNotifyAfterDispose() => RunSta(async () =>
    {
        using var config = new DeferredConfig();
        var pending = new TaskCompletionSource<MusicSkin>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var vm = new MusicOverlaySettingsViewModel(config,
            skinLoader: id => id == "slow" ? pending.Task : LoadTestSkin(id));
        var slowLoad = vm.LoadSelectedSkinAsync("slow");
        await vm.LoadSelectedSkinAsync("newer");
        pending.SetResult(new MusicSkin { Id = "slow" });
        await slowLoad;
        Assert.Equal("newer", vm.ActiveSkin.Id);

        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        slowLoad = vm.LoadSelectedSkinAsync("slow");
        vm.Dispose();
        int notifications = 0;
        vm.PropertyChanged += (_, _) => notifications++;
        vm.SkinChanged += _ => notifications++;
        pending.SetResult(new MusicSkin { Id = "slow" });
        await slowLoad;
        Assert.Equal(0, notifications);
        Assert.Same(MusicSkin.Default, vm.ActiveSkin);
    });

    [Fact]
    public void PresenterDisposedDuringSkinLoad_DoesNotReopenWindowOrMisreportCommittedSave() => RunSta(async () =>
    {
        using var config = new DeferredConfig();
        var pending = new TaskCompletionSource<MusicSkin>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var runtime = new Runtime(config, id =>
        {
            if (id != "slow") return LoadTestSkin(id);
            loadEntered.SetResult();
            return pending.Task;
        });
        using var vm = new MusicOverlaySettingsViewModel(config, runtime.Presenter, skinLoader: LoadTestSkin);
        vm.SelectedSkinId = "slow";
        var apply = vm.ApplyAsync();
        await loadEntered.Task;
        runtime.Presenter.Dispose();
        pending.SetResult(new MusicSkin { Id = "slow" });
        await apply;
        Assert.Null(runtime.Presenter.Window);
        Assert.Equal("slow", config.ReadSaved().MusicOverlay.SkinId);
        Assert.Equal("slow", config.Config.MusicOverlay.SkinId);
        Assert.Contains("已保存", vm.StatusMessage);
        Assert.DoesNotContain("失败", vm.StatusMessage);
    });

    [Fact]
    public void LatestSuccessfulSkinReplacesQueue_AndAppliesOnStop() => RunSta(async () =>
    {
        using var config = new DeferredConfig();
        using var runtime = new Runtime(config);
        using var vm = new MusicOverlaySettingsViewModel(config, runtime.Presenter, skinLoader: LoadTestSkin);
        runtime.Playback.LoadTimeline(new PerformanceTimeline
        {
            Events = [new PerformanceEvent { Key = "A", TimeMs = 1000, DurationMs = 100, Kind = MusicNoteKind.Tap }],
            TotalDurationMs = 2500
        });
        runtime.Playback.Play();
        vm.SelectedSkinId = "first";
        await vm.ApplyAsync();
        vm.SelectedSkinId = "second";
        await vm.ApplyAsync();
        Assert.Equal("default", runtime.Presenter.Window!.PlayfieldControl.Skin.Id);
        runtime.Playback.Stop();
        runtime.Foreground = true;
        runtime.Presenter.Tick();
        Assert.Equal("second", runtime.Presenter.Window.PlayfieldControl.Skin.Id);
        Assert.Equal("second", config.ReadSaved().MusicOverlay.SkinId);
    });
}
