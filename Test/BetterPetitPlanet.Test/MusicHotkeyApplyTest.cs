using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.Core.Process;
using BetterPetitPlanet.GameTask;
using BetterPetitPlanet.GameTask.AutoPick;
using BetterPetitPlanet.GameTask.Music.Service;
using BetterPetitPlanet.HotkeyCapture;
using BetterPetitPlanet.ViewModel.Pages;
using Vanara.PInvoke;
using Xunit;

namespace BetterPetitPlanet.Test;

public sealed class MusicHotkeyApplyTest
{
    private sealed class MemoryConfigService : IConfigService
    {
        public AppConfig Config { get; } = new();
        public int SaveCount { get; private set; }
        public int SaveAttempts { get; private set; }
        public bool ThrowOnSaveAsync { get; set; }
        public bool FailNextSaveAsync { get; set; }
        public bool HoldNextSaveAsync { get; private set; }
        public TaskCompletionSource<bool>? HeldSaveEntered { get; private set; }
        public TaskCompletionSource<bool>? ReleaseHeldSave { get; private set; }

        public void HoldNextSave(bool failAfterRelease = false)
        {
            HoldNextSaveAsync = true;
            FailNextSaveAsync = failAfterRelease;
            HeldSaveEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ReleaseHeldSave = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Save()
        {
            SaveAttempts++;
            SaveCount++;
        }
        public async Task SaveAsync()
        {
            SaveAttempts++;
            if (HoldNextSaveAsync)
            {
                HoldNextSaveAsync = false;
                HeldSaveEntered!.TrySetResult(true);
                await ReleaseHeldSave!.Task;
            }

            if (FailNextSaveAsync)
            {
                FailNextSaveAsync = false;
                throw new InvalidOperationException("test held async save failure");
            }
            if (ThrowOnSaveAsync) throw new InvalidOperationException("test async save failure");
            SaveCount++;
        }
        public void Reload() { }
    }

    private sealed class FakeHotkeyHook : IHotkeyHook
    {
        public event EventHandler<KeyPressedEventArgs>? KeyPressed;
        public List<(User32.HotKeyModifiers Modifiers, Keys Key)> RegisteredKeys { get; } = [];
        public HashSet<(User32.HotKeyModifiers Modifiers, Keys Key)> FailingKeys { get; } = [];
        public HashSet<((User32.HotKeyModifiers Modifiers, Keys Key) Key, int Attempt)> FailingAttempts { get; } = [];
        public HashSet<(User32.HotKeyModifiers Modifiers, Keys Key)> UnregisterUnconfirmedKeys { get; } = [];
        public HashSet<(User32.HotKeyModifiers Modifiers, Keys Key)> UnregisterThrowingKeys { get; } = [];
        private readonly Dictionary<(User32.HotKeyModifiers Modifiers, Keys Key), int> _registrationAttempts = [];

        public void FailRegistrationOnAttempt(string hotkey, int attempt)
        {
            var parsed = MusicHotkey.Parse(hotkey);
            FailingAttempts.Add(((parsed.Modifiers, parsed.Key), attempt));
        }

        public int RegistrationAttemptsFor(string hotkey)
        {
            var parsed = MusicHotkey.Parse(hotkey);
            return _registrationAttempts.GetValueOrDefault((parsed.Modifiers, parsed.Key));
        }

        public void RegisterHotKey(User32.HotKeyModifiers modifier, Keys key)
        {
            var binding = (modifier, key);
            _registrationAttempts.TryGetValue(binding, out var attempts);
            attempts++;
            _registrationAttempts[binding] = attempts;
            if (FailingKeys.Contains(binding) || FailingAttempts.Contains((binding, attempts)))
            {
                throw new InvalidOperationException($"Hotkey already registered: {modifier}+{key}");
            }
            RegisteredKeys.Add((modifier, key));
        }

        public void UnregisterHotKey()
        {
            RegisteredKeys.Clear();
        }

        public HotkeyUnregisterResult UnregisterHotKey(User32.HotKeyModifiers modifier, Keys key)
        {
            if (UnregisterThrowingKeys.Contains((modifier, key))) throw new InvalidOperationException("test unregister failure");
            if (UnregisterUnconfirmedKeys.Contains((modifier, key))) return HotkeyUnregisterResult.Unknown;
            return RegisteredKeys.Remove((modifier, key))
                ? HotkeyUnregisterResult.Unregistered
                : HotkeyUnregisterResult.NotRegistered;
        }

        public void Dispose()
        {
            UnregisterHotKey();
        }

        public void SimulateKeyPress(User32.HotKeyModifiers modifier, Keys key)
        {
            KeyPressed?.Invoke(this, new KeyPressedEventArgs(modifier, key));
        }
    }

    private static HotkeyService CreateHotkeyService(
        IConfigService configService,
        FakeHotkeyHook hookInstance)
    {
        return new HotkeyService(
            configService,
            logger: null,
            hookFactory: () => hookInstance);
    }

    private static HotkeyConfig CreateHotkeyConfig(string autoPick) => new()
    {
        ToggleAutoPickHotkey = autoPick,
        ToggleMusicHotkey = "F9",
        PreviousSongHotkey = "F10",
        NextSongHotkey = "F11",
        ToggleMusicTimelineHotkey = "Ctrl+F9",
        ToggleMusicOverlayHotkey = "Ctrl+F12"
    };

    [Theory]
    [InlineData("A")]
    [InlineData("S")]
    [InlineData("D")]
    [InlineData("F")]
    [InlineData("J")]
    [InlineData("K")]
    [InlineData("L")]
    public void Validation_RejectsSevenPianoKeysCollision(string pianoKey)
    {
        var config = new HotkeyConfig
        {
            ToggleAutoPickHotkey = pianoKey
        };

        var ex = Assert.Throws<ArgumentException>(() => HotkeyService.Validate(config));
        Assert.Contains("快捷键不能占用七个琴键", ex.Message);
    }

    [Fact]
    public void Validation_RejectsDuplicateHotkeys()
    {
        var config = new HotkeyConfig
        {
            ToggleAutoPickHotkey = "F8",
            ToggleMusicHotkey = "F8" // Duplicate
        };

        var ex = Assert.Throws<ArgumentException>(() => HotkeyService.Validate(config));
        Assert.Contains("快捷键重复", ex.Message);
    }

    [Fact]
    public void RegistrationFailure_RollsBackToPreviousActiveSet()
    {
        var configService = new MemoryConfigService();
        var hook = new FakeHotkeyHook();
        using var hotkeyService = CreateHotkeyService(configService, hook);

        // Initial setup
        hotkeyService.Initialize();
        Assert.Equal(6, hook.RegisteredKeys.Count);

        var initialActive = hotkeyService.ActiveConfig;
        Assert.Equal("F8", initialActive.ToggleAutoPickHotkey);
        Assert.Equal("Ctrl+F12", initialActive.ToggleMusicOverlayHotkey);

        // Make "Ctrl+F7" fail registration (simulate occupied by other process)
        var failingKey = MusicHotkey.Parse("Ctrl+F7");
        hook.FailingKeys.Add((failingKey.Modifiers, failingKey.Key));

        var proposed = new HotkeyConfig
        {
            ToggleAutoPickHotkey = "F8",
            ToggleMusicHotkey = "F9",
            PreviousSongHotkey = "F10",
            NextSongHotkey = "F11",
            ToggleMusicTimelineHotkey = "Ctrl+F9",
            ToggleMusicOverlayHotkey = "Ctrl+F7" // Will fail
        };

        var result = hotkeyService.ApplyHotkeys(proposed);

        // Must report failure
        Assert.False(result.Success);
        Assert.Contains("注册失败", result.ErrorMessage);
        Assert.Contains("已安全回滚到上一次配置", result.ErrorMessage);

        // Active config and configService must remain unchanged
        Assert.Equal("Ctrl+F12", result.ActiveConfig.ToggleMusicOverlayHotkey);
        Assert.Equal("Ctrl+F12", hotkeyService.ActiveConfig.ToggleMusicOverlayHotkey);
        Assert.Equal("Ctrl+F12", configService.Config.Hotkey.ToggleMusicOverlayHotkey);
        Assert.Equal(0, configService.SaveCount);

        // Hook should still have the 6 rolled-back keys registered
        Assert.Equal(6, hook.RegisteredKeys.Count);
        Assert.Equal(6, result.EffectiveBindings.Count);
        Assert.Equal(1, hook.RegistrationAttemptsFor("F8"));
        Assert.Equal(2, hook.RegistrationAttemptsFor("Ctrl+F12"));
    }

    [Fact]
    public void Initialize_RegistersLoadedConfigWithoutSavingIt()
    {
        var configService = new MemoryConfigService { ThrowOnSaveAsync = true };
        var hook = new FakeHotkeyHook();
        using var hotkeyService = CreateHotkeyService(configService, hook);

        hotkeyService.Initialize();

        Assert.Equal("F8", hotkeyService.ActiveConfig.ToggleAutoPickHotkey);
        Assert.Equal("Ctrl+F12", hotkeyService.ActiveConfig.ToggleMusicOverlayHotkey);
        Assert.Equal(6, hook.RegisteredKeys.Count);
        Assert.Equal(0, configService.SaveAttempts);
        Assert.Equal(0, configService.SaveCount);
    }

    [Fact]
    public async Task ConcurrentAsyncApplies_WaitForFirstSaveTransaction()
    {
        var configService = new MemoryConfigService();
        var hook = new FakeHotkeyHook();
        using var hotkeyService = CreateHotkeyService(configService, hook);
        hotkeyService.Initialize();
        configService.HoldNextSave();

        var firstApply = hotkeyService.ApplyHotkeysAsync(CreateHotkeyConfig("F7"));
        await configService.HeldSaveEntered!.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondApply = hotkeyService.ApplyHotkeysAsync(CreateHotkeyConfig("F6"));

        Assert.False(secondApply.IsCompleted);
        Assert.Equal("F7", hotkeyService.ActiveConfig.ToggleAutoPickHotkey);
        Assert.Equal("F7", configService.Config.Hotkey.ToggleAutoPickHotkey);
        Assert.Contains((User32.HotKeyModifiers.MOD_NONE, Keys.F7), hook.RegisteredKeys);
        Assert.DoesNotContain((User32.HotKeyModifiers.MOD_NONE, Keys.F6), hook.RegisteredKeys);

        configService.ReleaseHeldSave!.TrySetResult(true);
        var firstResult = await firstApply;
        var secondResult = await secondApply;

        Assert.True(firstResult.Success);
        Assert.True(secondResult.Success);
        Assert.Equal("F6", hotkeyService.ActiveConfig.ToggleAutoPickHotkey);
        Assert.Equal("F6", configService.Config.Hotkey.ToggleAutoPickHotkey);
        Assert.Contains((User32.HotKeyModifiers.MOD_NONE, Keys.F6), hook.RegisteredKeys);
        Assert.DoesNotContain((User32.HotKeyModifiers.MOD_NONE, Keys.F7), hook.RegisteredKeys);
        Assert.Equal(2, configService.SaveAttempts);
        Assert.Equal(2, configService.SaveCount);
    }

    [Fact]
    public async Task Dispose_WaitsForPendingApplyTransactionBeforeReleasingHook()
    {
        var configService = new MemoryConfigService();
        var hook = new FakeHotkeyHook();
        using var hotkeyService = CreateHotkeyService(configService, hook);
        hotkeyService.Initialize();
        configService.HoldNextSave();

        var applyTask = hotkeyService.ApplyHotkeysAsync(CreateHotkeyConfig("F7"));
        await configService.HeldSaveEntered!.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeTask = Task.Run(() =>
        {
            disposeStarted.TrySetResult(true);
            hotkeyService.Dispose();
        });
        await disposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotSame(disposeTask, await Task.WhenAny(disposeTask, Task.Delay(100)));
        Assert.Equal("F7", hotkeyService.ActiveConfig.ToggleAutoPickHotkey);
        Assert.Contains((User32.HotKeyModifiers.MOD_NONE, Keys.F7), hook.RegisteredKeys);
        Assert.Equal(6, hook.RegisteredKeys.Count);

        configService.ReleaseHeldSave!.TrySetResult(true);
        var applyResult = await applyTask;
        await disposeTask;

        Assert.True(applyResult.Success);
        Assert.Equal(string.Empty, hotkeyService.ActiveConfig.ToggleAutoPickHotkey);
        Assert.Empty(hook.RegisteredKeys);
    }

    [Fact]
    public async Task FailedFirstSave_RollsBackBeforeSynchronousSecondApply()
    {
        var configService = new MemoryConfigService();
        var hook = new FakeHotkeyHook();
        using var hotkeyService = CreateHotkeyService(configService, hook);
        hotkeyService.Initialize();
        configService.HoldNextSave(failAfterRelease: true);

        var firstApply = hotkeyService.ApplyHotkeysAsync(CreateHotkeyConfig("F7"));
        await configService.HeldSaveEntered!.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondApply = Task.Run(() => hotkeyService.ApplyHotkeys(CreateHotkeyConfig("F6")));

        Assert.False(secondApply.IsCompleted);
        Assert.Equal("F7", hotkeyService.ActiveConfig.ToggleAutoPickHotkey);
        Assert.Contains((User32.HotKeyModifiers.MOD_NONE, Keys.F7), hook.RegisteredKeys);

        configService.ReleaseHeldSave!.TrySetResult(true);
        var firstResult = await firstApply;
        var secondResult = await secondApply;

        Assert.False(firstResult.Success);
        Assert.Contains("保存失败", firstResult.ErrorMessage);
        Assert.Contains("已恢复到上一次生效配置", firstResult.ErrorMessage);
        Assert.True(secondResult.Success);
        Assert.Equal("F6", hotkeyService.ActiveConfig.ToggleAutoPickHotkey);
        Assert.Equal("F6", configService.Config.Hotkey.ToggleAutoPickHotkey);
        Assert.Contains((User32.HotKeyModifiers.MOD_NONE, Keys.F6), hook.RegisteredKeys);
        Assert.DoesNotContain((User32.HotKeyModifiers.MOD_NONE, Keys.F7), hook.RegisteredKeys);
        Assert.Equal(2, configService.SaveAttempts);
        Assert.Equal(1, configService.SaveCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnregisterUnknownState_IsExposedAndNeverReportedAsCompleteRollback(bool throwDuringUnregister)
    {
        var configService = new MemoryConfigService();
        var hook = new FakeHotkeyHook();
        using var hotkeyService = CreateHotkeyService(configService, hook);
        hotkeyService.Initialize();
        if (throwDuringUnregister)
        {
            hook.UnregisterThrowingKeys.Add((User32.HotKeyModifiers.MOD_NONE, Keys.F8));
        }
        else
        {
            hook.UnregisterUnconfirmedKeys.Add((User32.HotKeyModifiers.MOD_NONE, Keys.F8));
        }

        var result = hotkeyService.ApplyHotkeys(CreateHotkeyConfig("F7"));

        Assert.False(result.Success);
        Assert.DoesNotContain("已安全回滚到上一次配置", result.ErrorMessage);
        Assert.Contains("无法确认是否仍生效", result.ErrorMessage);
        Assert.Equal(string.Empty, result.ActiveConfig.ToggleAutoPickHotkey);
        Assert.Empty(result.EffectiveBindings.Where(binding => binding.Name == "自动拾取"));
        Assert.Contains(result.UnconfirmedBindings, binding => binding.Name == "自动拾取" && binding.Key == "F8");

        var vm = new HotkeyPageViewModel(configService, hotkeyService);
        vm.ToggleAutoPickHotkey = "F6";
        await vm.ApplyHotkeysAsync();

        Assert.True(vm.HasError);
        Assert.Contains("无法确认是否仍生效", vm.StatusText);
        Assert.Equal("F8", configService.Config.Hotkey.ToggleAutoPickHotkey);
    }

    [Fact]
    public void RegistrationFailure_WhenOldBindingCannotBeRestored_ReportsActualPartialBindings()
    {
        var configService = new MemoryConfigService();
        var hook = new FakeHotkeyHook();
        using var hotkeyService = CreateHotkeyService(configService, hook);
        hotkeyService.Initialize();

        // The proposal fails at Ctrl+F7, then restoration of the replaced Ctrl+F12 binding fails.
        var failedProposal = MusicHotkey.Parse("Ctrl+F7");
        hook.FailingKeys.Add((failedProposal.Modifiers, failedProposal.Key));
        hook.FailRegistrationOnAttempt("Ctrl+F12", 2);
        var savesBeforeApply = configService.SaveCount;

        var result = hotkeyService.ApplyHotkeys(new HotkeyConfig
        {
            ToggleAutoPickHotkey = "F8",
            ToggleMusicHotkey = "F9",
            PreviousSongHotkey = "F10",
            NextSongHotkey = "F11",
            ToggleMusicTimelineHotkey = "Ctrl+F9",
            ToggleMusicOverlayHotkey = "Ctrl+F7"
        });

        Assert.False(result.Success);
        Assert.Equal(string.Empty, result.ActiveConfig.ToggleMusicOverlayHotkey);
        Assert.Equal("F9", result.ActiveConfig.ToggleMusicHotkey);
        Assert.DoesNotContain("已安全回滚到上一次配置", result.ErrorMessage);
        Assert.Contains(
            "实际生效快捷键：自动拾取=F8、自动演奏=F9、上一首=F10、下一首=F11、谱面启停=Ctrl+F9",
            result.ErrorMessage);
        Assert.DoesNotContain("悬浮窗显示=Ctrl+F12", result.ErrorMessage);
        Assert.DoesNotContain("悬浮窗显示=Ctrl+F7", result.ErrorMessage);
        Assert.Equal(5, hook.RegisteredKeys.Count);
        Assert.Equal(5, result.EffectiveBindings.Count);
        Assert.Contains(result.EffectiveBindings, binding => binding.Name == "自动拾取" && binding.Key == "F8");
        Assert.DoesNotContain(result.EffectiveBindings, binding => binding.Name == "悬浮窗显示");

        // Failed applies leave the persisted configuration and save count untouched.
        Assert.Equal("F8", configService.Config.Hotkey.ToggleAutoPickHotkey);
        Assert.Equal("Ctrl+F12", configService.Config.Hotkey.ToggleMusicOverlayHotkey);
        Assert.Equal(savesBeforeApply, configService.SaveCount);
    }

    [Fact]
    public async Task ViewModel_PartialRollback_ReportsActualEffectiveBindings()
    {
        var configService = new MemoryConfigService();
        var hook = new FakeHotkeyHook();
        using var hotkeyService = CreateHotkeyService(configService, hook);
        hotkeyService.Initialize();

        var proposalFailure = MusicHotkey.Parse("Alt+F12");
        hook.FailingKeys.Add((proposalFailure.Modifiers, proposalFailure.Key));
        hook.FailRegistrationOnAttempt("Ctrl+F12", 2);
        var vm = new HotkeyPageViewModel(configService, hotkeyService);
        var savesBeforeApply = configService.SaveCount;
        vm.ToggleMusicOverlayHotkey = "Alt+F12";

        await vm.ApplyHotkeysAsync();

        Assert.True(vm.HasError);
        Assert.Contains(
            "实际生效快捷键：自动拾取=F8、自动演奏=F9、上一首=F10、下一首=F11、谱面启停=Ctrl+F9",
            vm.StatusText);
        Assert.DoesNotContain("已安全回滚到上一次配置", vm.StatusText);
        Assert.DoesNotContain("悬浮窗显示=Ctrl+F12", vm.StatusText);
        Assert.DoesNotContain("悬浮窗显示=Alt+F12", vm.StatusText);
        Assert.Contains("自动演奏=F9", vm.StatusText);
        Assert.Equal(string.Empty, vm.ToggleMusicOverlayHotkey);
        Assert.Equal("F9", vm.ToggleMusicHotkey);
        Assert.Equal(5, hook.RegisteredKeys.Count);
        Assert.Equal(savesBeforeApply, configService.SaveCount);
    }

    [Fact]
    public async Task ViewModel_SaveFailure_RestoresRuntimeAndLastPersistedConfig()
    {
        var configService = new MemoryConfigService();
        var hook = new FakeHotkeyHook();
        using var hotkeyService = CreateHotkeyService(configService, hook);
        hotkeyService.Initialize();
        var vm = new HotkeyPageViewModel(configService, hotkeyService);
        var successfulSavesBeforeApply = configService.SaveCount;
        configService.ThrowOnSaveAsync = true;
        vm.ToggleAutoPickHotkey = "F7";

        await vm.ApplyHotkeysAsync();

        Assert.True(vm.HasError);
        Assert.Contains("保存失败", vm.StatusText);
        Assert.Contains("已恢复到上一次生效配置", vm.StatusText);
        Assert.Equal("F8", vm.ToggleAutoPickHotkey);
        Assert.Equal("F8", hotkeyService.ActiveConfig.ToggleAutoPickHotkey);
        Assert.Equal("F8", configService.Config.Hotkey.ToggleAutoPickHotkey);
        Assert.Contains((User32.HotKeyModifiers.MOD_NONE, Keys.F8), hook.RegisteredKeys);
        Assert.DoesNotContain((User32.HotKeyModifiers.MOD_NONE, Keys.F7), hook.RegisteredKeys);
        Assert.Equal(6, hook.RegisteredKeys.Count);
        Assert.Equal(successfulSavesBeforeApply, configService.SaveCount);
        Assert.Equal(successfulSavesBeforeApply + 1, configService.SaveAttempts);
    }

    [Fact]
    public async Task ViewModel_SaveFailureWithPartialRuntimeRestore_ReportsActualBindings()
    {
        var configService = new MemoryConfigService();
        var hook = new FakeHotkeyHook();
        using var hotkeyService = CreateHotkeyService(configService, hook);
        hotkeyService.Initialize();
        hook.FailRegistrationOnAttempt("F8", 2);
        var vm = new HotkeyPageViewModel(configService, hotkeyService);
        var successfulSavesBeforeApply = configService.SaveCount;
        configService.ThrowOnSaveAsync = true;
        vm.ToggleAutoPickHotkey = "F7";

        await vm.ApplyHotkeysAsync();

        Assert.True(vm.HasError);
        Assert.Contains("保存失败", vm.StatusText);
        Assert.Contains("恢复不完整", vm.StatusText);
        Assert.Contains("实际生效快捷键：自动演奏=F9、上一首=F10、下一首=F11、谱面启停=Ctrl+F9、悬浮窗显示=Ctrl+F12", vm.StatusText);
        Assert.Equal(string.Empty, vm.ToggleAutoPickHotkey);
        Assert.Equal("F8", configService.Config.Hotkey.ToggleAutoPickHotkey);
        Assert.DoesNotContain((User32.HotKeyModifiers.MOD_NONE, Keys.F8), hook.RegisteredKeys);
        Assert.DoesNotContain((User32.HotKeyModifiers.MOD_NONE, Keys.F7), hook.RegisteredKeys);
        Assert.Equal(5, hook.RegisteredKeys.Count);
        Assert.Equal(successfulSavesBeforeApply, configService.SaveCount);
        Assert.Equal(successfulSavesBeforeApply + 1, configService.SaveAttempts);
    }

    [Fact]
    public void SynchronousSaveFailure_ReturnsFailureAndRestoresRuntimeAndConfig()
    {
        var configService = new MemoryConfigService();
        var hook = new FakeHotkeyHook();
        using var hotkeyService = CreateHotkeyService(configService, hook);
        hotkeyService.Initialize();
        var successfulSavesBeforeApply = configService.SaveCount;
        configService.ThrowOnSaveAsync = true;

        var result = hotkeyService.ApplyHotkeys(new HotkeyConfig
        {
            ToggleAutoPickHotkey = "F7",
            ToggleMusicHotkey = "F9",
            PreviousSongHotkey = "F10",
            NextSongHotkey = "F11",
            ToggleMusicTimelineHotkey = "Ctrl+F9",
            ToggleMusicOverlayHotkey = "Ctrl+F12"
        });

        Assert.False(result.Success);
        Assert.Contains("保存失败", result.ErrorMessage);
        Assert.Contains("已恢复到上一次生效配置", result.ErrorMessage);
        Assert.Equal("F8", result.ActiveConfig.ToggleAutoPickHotkey);
        Assert.Equal("F8", configService.Config.Hotkey.ToggleAutoPickHotkey);
        Assert.Contains((User32.HotKeyModifiers.MOD_NONE, Keys.F8), hook.RegisteredKeys);
        Assert.DoesNotContain((User32.HotKeyModifiers.MOD_NONE, Keys.F7), hook.RegisteredKeys);
        Assert.Equal(6, hook.RegisteredKeys.Count);
        Assert.Equal(successfulSavesBeforeApply, configService.SaveCount);
        Assert.Equal(successfulSavesBeforeApply + 1, configService.SaveAttempts);
    }

    [Fact]
    public async Task ViewModelWithoutHotkeyService_SaveFailureRestoresDraftAndConfig()
    {
        var configService = new MemoryConfigService();
        configService.Config.Hotkey.ToggleAutoPickHotkey = "F7";
        await configService.SaveAsync();
        var vm = new HotkeyPageViewModel(configService);
        var successfulSavesBeforeApply = configService.SaveCount;
        var saveAttemptsBeforeApply = configService.SaveAttempts;
        configService.ThrowOnSaveAsync = true;
        vm.ToggleAutoPickHotkey = "F6";

        await vm.ApplyHotkeysAsync();

        Assert.True(vm.HasError);
        Assert.Contains("保存失败", vm.StatusText);
        Assert.Contains("已恢复到上一次保存配置", vm.StatusText);
        Assert.DoesNotContain("快捷键已成功保存", vm.StatusText);
        Assert.Equal("F7", vm.ToggleAutoPickHotkey);
        Assert.Equal("F7", configService.Config.Hotkey.ToggleAutoPickHotkey);
        Assert.False(vm.IsDirty);
        Assert.Equal(successfulSavesBeforeApply, configService.SaveCount);
        Assert.Equal(saveAttemptsBeforeApply + 1, configService.SaveAttempts);
    }

    [Fact]
    public async Task ViewModel_AtomicRollback_UpdatesUIStateAndVisibility()
    {
        var configService = new MemoryConfigService();
        var hook = new FakeHotkeyHook();
        using var hotkeyService = CreateHotkeyService(configService, hook);
        hotkeyService.Initialize();

        // Simulate conflict for Alt+F12
        var failing = MusicHotkey.Parse("Alt+F12");
        hook.FailingKeys.Add((failing.Modifiers, failing.Key));

        var vm = new HotkeyPageViewModel(configService, hotkeyService);
        Assert.False(vm.HasError);
        Assert.False(vm.IsDirty);
        Assert.Equal("Ctrl+F12", vm.ToggleMusicOverlayHotkey);

        // User edits textbox
        vm.ToggleMusicOverlayHotkey = "Alt+F12";
        Assert.True(vm.IsDirty);

        // User applies hotkeys
        await vm.ApplyHotkeysAsync();

        // Must show error, not pretend to succeed
        Assert.True(vm.HasError);
        Assert.Contains("注册失败", vm.StatusText);
        Assert.Contains("已安全回滚到上一次配置", vm.StatusText);

        // Textbox value must be rolled back to actual effective hotkey
        Assert.Equal("Ctrl+F12", vm.ToggleMusicOverlayHotkey);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task ViewModel_ValidationFailure_BlocksRegistrationAndDisplaysError()
    {
        var configService = new MemoryConfigService();
        var hook = new FakeHotkeyHook();
        using var hotkeyService = CreateHotkeyService(configService, hook);
        hotkeyService.Initialize();

        var vm = new HotkeyPageViewModel(configService, hotkeyService);

        // Set to a piano key
        vm.ToggleAutoPickHotkey = "A";

        await vm.ApplyHotkeysAsync();

        Assert.True(vm.HasError);
        Assert.Contains("快捷键不能占用七个琴键", vm.StatusText);
    }

    [Fact]
    public async Task SuccessfulRegistration_UpdatesActiveConfigAndConfigService()
    {
        var configService = new MemoryConfigService();
        var hook = new FakeHotkeyHook();
        using var hotkeyService = CreateHotkeyService(configService, hook);
        hotkeyService.Initialize();

        var vm = new HotkeyPageViewModel(configService, hotkeyService);

        vm.ToggleAutoPickHotkey = "F7";
        vm.ToggleMusicTimelineHotkey = "Alt+F9";

        await vm.ApplyHotkeysAsync();

        Assert.False(vm.HasError);
        Assert.Contains("快捷键已成功注册并生效", vm.StatusText);
        Assert.Equal("F7", hotkeyService.ActiveConfig.ToggleAutoPickHotkey);
        Assert.Equal("Alt+F9", hotkeyService.ActiveConfig.ToggleMusicTimelineHotkey);
        Assert.Equal("F7", configService.Config.Hotkey.ToggleAutoPickHotkey);
        Assert.Equal("Alt+F9", configService.Config.Hotkey.ToggleMusicTimelineHotkey);
        Assert.True(configService.SaveCount > 0);
    }

    [Fact]
    public void ResetToDefault_RestoresStandardKeysInDraft()
    {
        var configService = new MemoryConfigService();
        configService.Config.Hotkey.ToggleAutoPickHotkey = "F7";
        configService.Config.Hotkey.ToggleMusicOverlayHotkey = "Ctrl+F8";

        var vm = new HotkeyPageViewModel(configService);
        Assert.Equal("F7", vm.ToggleAutoPickHotkey);
        Assert.Equal("Ctrl+F8", vm.ToggleMusicOverlayHotkey);

        vm.ResetToDefault();

        Assert.Equal("F8", vm.ToggleAutoPickHotkey);
        Assert.Equal("F9", vm.ToggleMusicHotkey);
        Assert.Equal("F10", vm.PreviousSongHotkey);
        Assert.Equal("F11", vm.NextSongHotkey);
        Assert.Equal("Ctrl+F9", vm.ToggleMusicTimelineHotkey);
        Assert.Equal("Ctrl+F12", vm.ToggleMusicOverlayHotkey);
        Assert.True(vm.IsDirty);
        Assert.False(vm.HasError);
    }
}
