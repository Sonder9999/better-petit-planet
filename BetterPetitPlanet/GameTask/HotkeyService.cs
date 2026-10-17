using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.AutoPick;
using BetterPetitPlanet.GameTask.Music.Overlay;
using BetterPetitPlanet.GameTask.Music.Service;
using BetterPetitPlanet.HotkeyCapture;
using Microsoft.Extensions.Logging;

namespace BetterPetitPlanet.GameTask;

public sealed class HotkeyService : IDisposable
{
    private readonly ILogger<HotkeyService>? _logger;
    private readonly IConfigService _configService;
    private readonly AutoPickTrigger? _autoPickTrigger;
    private readonly MusicPlaybackService? _playbackService;
    private readonly GameTaskManager? _taskManager;
    private readonly MusicSessionCoordinator? _coordinator;
    private readonly MusicOverlayPresenter? _presenter;
    private readonly Func<IHotkeyHook> _hookFactory;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private static readonly string[] BindingActionIds =
    [
        nameof(HotkeyConfig.ToggleAutoPickHotkey),
        nameof(HotkeyConfig.ToggleMusicHotkey),
        nameof(HotkeyConfig.PreviousSongHotkey),
        nameof(HotkeyConfig.NextSongHotkey),
        nameof(HotkeyConfig.ToggleMusicTimelineHotkey),
        nameof(HotkeyConfig.ToggleMusicOverlayHotkey)
    ];

    private IHotkeyHook? _hook;
    private HotkeyConfig _activeConfig = CreateEmptyConfig();
    private Dictionary<MusicHotkey, ActiveHotkeyBinding> _activeBindings = [];
    private Dictionary<MusicHotkey, ActiveHotkeyBinding> _unconfirmedBindings = [];
    private bool _disposed;

    public HotkeyConfig ActiveConfig => CloneConfig(_activeConfig);

    public HotkeyService(
        IConfigService configService,
        AutoPickTrigger? autoPickTrigger = null,
        MusicPlaybackService? playbackService = null,
        GameTaskManager? taskManager = null,
        ILogger<HotkeyService>? logger = null,
        MusicSessionCoordinator? coordinator = null,
        MusicOverlayPresenter? presenter = null,
        Func<IHotkeyHook>? hookFactory = null)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _autoPickTrigger = autoPickTrigger;
        _playbackService = playbackService;
        _taskManager = taskManager;
        _logger = logger;
        _coordinator = coordinator;
        _presenter = presenter;
        _hookFactory = hookFactory ?? (() => new HotkeyHook());

    }

    public static void Validate(HotkeyConfig config) => MusicHotkey.Validate(config);

    public void Initialize()
    {
        _applyGate.Wait();
        try
        {
            if (_disposed) return;

            // Config has already been loaded. Register it without making startup depend on
            // a second persistence operation.
            var result = ApplyHotkeysCore(CloneConfig(_configService.Config.Hotkey));
            if (!result.Success)
            {
                _logger?.LogWarning("Initial hotkey registration encountered issues: {Error}", result.ErrorMessage);
            }
            else
            {
                _logger?.LogInformation("Global HotkeyService initialized successfully.");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize global hotkeys");
        }
        finally
        {
            _applyGate.Release();
        }
    }

    public async Task<HotkeyApplyResult> ApplyHotkeysAsync(HotkeyConfig proposedConfig)
    {
        await _applyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return CreateApplyResult(false, "快捷键服务已释放", null);
            }

            var snapshot = CaptureApplySnapshot();
            var result = ApplyHotkeysCore(proposedConfig);
            if (!result.Success)
            {
                return result;
            }

            try
            {
                await _configService.SaveAsync().ConfigureAwait(false);
                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to persist applied hotkey configuration");
                return RollBackAfterSaveFailure(snapshot, ex);
            }
        }
        finally
        {
            _applyGate.Release();
        }
    }

    public HotkeyApplyResult ApplyHotkeys(HotkeyConfig proposedConfig)
    {
        _applyGate.Wait();
        try
        {
            if (_disposed)
            {
                return CreateApplyResult(false, "快捷键服务已释放", null);
            }

            var snapshot = CaptureApplySnapshot();
            var result = ApplyHotkeysCore(proposedConfig);
            if (!result.Success)
            {
                return result;
            }

            try
            {
                // Keep the public API synchronous while waiting for the save result so callers
                // can receive a failure and runtime bindings can be restored consistently.
                _configService.SaveAsync().GetAwaiter().GetResult();
                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to persist applied hotkey configuration");
                return RollBackAfterSaveFailure(snapshot, ex);
            }
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private ApplySnapshot CaptureApplySnapshot() => new(
        CloneBindings(_activeBindings),
        CloneBindings(_unconfirmedBindings),
        CloneConfig(_configService.Config.Hotkey));

    private HotkeyApplyResult RollBackAfterSaveFailure(ApplySnapshot snapshot, Exception exception)
    {
        var actualBindings = CloneBindings(_activeBindings);
        var unconfirmedBindings = CloneBindings(_unconfirmedBindings);
        var rollbackIssues = RollbackBindings(snapshot.ActiveBindings, actualBindings, unconfirmedBindings);
        _activeBindings = actualBindings;
        _unconfirmedBindings = unconfirmedBindings;
        _activeConfig = ConfigFromBindings(actualBindings);
        _configService.Config.Hotkey = CloneConfig(snapshot.PersistedConfig);

        var restored = BindingsEqual(snapshot.ActiveBindings, actualBindings) &&
                       BindingsEqual(snapshot.UnconfirmedBindings, unconfirmedBindings);
        var errorMessage = "快捷键保存失败：" + exception.Message;
        errorMessage += restored
            ? "，已恢复到上一次生效配置"
            : FormatIncompleteRollback(actualBindings, unconfirmedBindings);

        if (!restored && rollbackIssues.Count > 0)
        {
            errorMessage += "；恢复失败：" + string.Join("；", rollbackIssues);
        }

        return CreateApplyResult(false, errorMessage, null);
    }

    private HotkeyApplyResult ApplyHotkeysCore(HotkeyConfig proposedConfig)
    {
        // 1. Pre-validation of proposed configuration
        try
        {
            Validate(proposedConfig);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Hotkey configuration rejected during validation");
            return CreateApplyResult(false, "快捷键校验失败：" + ex.Message, null);
        }

        var proposedBindings = CreateBindings(proposedConfig);

        if (_unconfirmedBindings.Count > 0)
        {
            return CreateApplyResult(
                false,
                "当前部分快捷键无法确认是否仍生效：" + FormatBindings(_unconfirmedBindings) + "。请重启应用后再修改快捷键。",
                null);
        }

        // 2. Avoid disturbing a complete, identical active set.
        if (_hook != null && IsEqual(proposedConfig, _activeConfig) &&
            proposedBindings.Count == _activeBindings.Count)
        {
            return CreateApplyResult(true, "快捷键设置无变更，保持当前生效配置", null);
        }

        // 3. Ensure hook instance exists
        if (_hook == null)
        {
            _hook = _hookFactory();
            _hook.KeyPressed += OnKeyPressed;
        }

        var previousBindings = CloneBindings(_activeBindings);
        var actualBindings = CloneBindings(_activeBindings);
        var removedBindings = actualBindings.Keys.Where(key => !proposedBindings.ContainsKey(key)).ToArray();
        var addedBindings = proposedBindings.Keys.Where(key => !actualBindings.ContainsKey(key)).ToArray();

        // Keep every binding that is still part of the proposal. Only keys that leave the
        // effective set are released, so unchanged bindings remain available throughout apply.
        foreach (var key in removedBindings)
        {
            var binding = actualBindings[key];
            try
            {
                var unregisterResult = _hook.UnregisterHotKey(key.Modifiers, key.Key);
                if (unregisterResult == HotkeyUnregisterResult.Unknown)
                {
                    actualBindings.Remove(key);
                    _unconfirmedBindings[key] = binding;
                    _logger?.LogWarning("Unregister state is unknown for {Name} -> {Key}", binding.Name, binding.Text);
                    return RollBackAfterFailure(
                        "快捷键移除失败：" + binding.Name + " (" + binding.Text + ") 无法确认注销",
                        binding.Name,
                        previousBindings,
                        actualBindings,
                        _unconfirmedBindings);
                }

                actualBindings.Remove(key);
            }
            catch (Exception ex)
            {
                actualBindings.Remove(key);
                _unconfirmedBindings[key] = binding;
                _logger?.LogWarning(ex, "Unregister failed for {Name} -> {Key}", binding.Name, binding.Text);
                return RollBackAfterFailure(
                    "快捷键移除失败：" + binding.Name + " (" + binding.Text + ") 无法注销",
                    binding.Name,
                    previousBindings,
                    actualBindings,
                    _unconfirmedBindings);
            }
        }

        foreach (var key in addedBindings)
        {
            var binding = proposedBindings[key];
            try
            {
                _hook.RegisterHotKey(key.Modifiers, key.Key);
                actualBindings.Add(key, binding);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Registration failed for {Name} -> {Key}. Restoring previous effective bindings.", binding.Name, binding.Text);
                return RollBackAfterFailure(
                    $"快捷键注册失败：{binding.Name} ({binding.Text}) 注册失败 (可能已被其他程序独占)",
                    binding.Name,
                    previousBindings,
                    actualBindings,
                    _unconfirmedBindings);
            }
        }

        // 5. Success: update active config
        _activeBindings = proposedBindings;
        _unconfirmedBindings.Clear();
        _activeConfig = CloneConfig(proposedConfig);
        _configService.Config.Hotkey = CloneConfig(proposedConfig);

        _logger?.LogInformation("Global hotkeys successfully registered: {@Config}", _activeConfig);
        return CreateApplyResult(true, "快捷键已成功注册并生效", null);
    }

    private HotkeyApplyResult RollBackAfterFailure(
        string failureMessage,
        string? failedKeyName,
        Dictionary<MusicHotkey, ActiveHotkeyBinding> previousBindings,
        Dictionary<MusicHotkey, ActiveHotkeyBinding> actualBindings,
        Dictionary<MusicHotkey, ActiveHotkeyBinding> unconfirmedBindings)
    {
        var rollbackIssues = RollbackBindings(previousBindings, actualBindings, unconfirmedBindings);
        _activeBindings = actualBindings;
        _unconfirmedBindings = unconfirmedBindings;
        _activeConfig = ConfigFromBindings(actualBindings);

        var restored = BindingsEqual(previousBindings, actualBindings) && unconfirmedBindings.Count == 0;
        var errorMessage = restored
            ? failureMessage + "，已安全回滚到上一次配置"
            : failureMessage + FormatIncompleteRollback(actualBindings, unconfirmedBindings);

        if (!restored && rollbackIssues.Count > 0)
        {
            errorMessage += "；恢复失败：" + string.Join("；", rollbackIssues);
        }

        return CreateApplyResult(false, errorMessage, failedKeyName);
    }

    private List<string> RollbackBindings(
        Dictionary<MusicHotkey, ActiveHotkeyBinding> targetBindings,
        Dictionary<MusicHotkey, ActiveHotkeyBinding> actualBindings,
        Dictionary<MusicHotkey, ActiveHotkeyBinding> unconfirmedBindings)
    {
        var issues = new List<string>();
        if (_hook == null) return issues;

        foreach (var key in actualBindings.Keys.Where(key => !targetBindings.ContainsKey(key)).ToArray())
        {
            try
            {
                var unregisterResult = _hook.UnregisterHotKey(key.Modifiers, key.Key);
                if (unregisterResult is HotkeyUnregisterResult.Unregistered or HotkeyUnregisterResult.NotRegistered)
                {
                    actualBindings.Remove(key);
                }
                else
                {
                    var binding = actualBindings[key];
                    actualBindings.Remove(key);
                    unconfirmedBindings[key] = binding;
                    issues.Add(binding.Name + " (" + binding.Text + ") 无法确认是否仍生效");
                }
            }
            catch (Exception ex)
            {
                var binding = actualBindings[key];
                actualBindings.Remove(key);
                unconfirmedBindings[key] = binding;
                _logger?.LogError(ex, "Failed to unregister a proposed binding during rollback: {Hotkey}", binding.Text);
                issues.Add(binding.Name + " (" + binding.Text + ") 无法确认是否仍生效");
            }
        }

        foreach (var (key, binding) in targetBindings)
        {
            if (unconfirmedBindings.ContainsKey(key))
            {
                continue;
            }

            if (actualBindings.ContainsKey(key))
            {
                // The OS registration is still present; restore its old action association.
                actualBindings[key] = binding;
                continue;
            }

            try
            {
                _hook.RegisterHotKey(key.Modifiers, key.Key);
                actualBindings.Add(key, binding);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to restore previous binding during rollback: {Name} -> {Key}", binding.Name, binding.Text);
                issues.Add(binding.Name + " (" + binding.Text + ") 注册恢复失败");
            }
        }

        return issues;
    }

    private async void OnKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        try
        {
            var key = new MusicHotkey(e.Key, e.Modifier);
            if (!_activeBindings.TryGetValue(key, out var binding)) return;

            if (binding.ActionId == nameof(HotkeyConfig.ToggleAutoPickHotkey))
            {
                if (_autoPickTrigger != null)
                {
                    _autoPickTrigger.IsEnabled = !_autoPickTrigger.IsEnabled;
                    _configService.Config.AutoPick.Enabled = _autoPickTrigger.IsEnabled;
                    await _configService.SaveAsync();

                    if (_autoPickTrigger.IsEnabled && _taskManager != null && !_taskManager.IsRunning)
                    {
                        _taskManager.Start();
                    }
                    _logger?.LogInformation("Hotkey toggled AutoPick: {State}", _autoPickTrigger.IsEnabled);
                }
            }
            else if (binding.ActionId == nameof(HotkeyConfig.ToggleMusicHotkey))
            {
                if (_coordinator != null)
                {
                    await _coordinator.ToggleAutomaticAsync();
                }
                else if (_playbackService != null)
                {
                    _playbackService.TogglePlayPause();
                }
                _logger?.LogInformation("Hotkey toggled automatic Music playback");
            }
            else if (binding.ActionId == nameof(HotkeyConfig.ToggleMusicTimelineHotkey))
            {
                if (_coordinator != null)
                {
                    await _coordinator.ToggleTimelineAsync();
                }
                else if (_playbackService != null)
                {
                    _playbackService.TogglePlayPause();
                }
                _logger?.LogInformation("Hotkey toggled Music timeline");
            }
            else if (binding.ActionId == nameof(HotkeyConfig.ToggleMusicOverlayHotkey))
            {
                if (_presenter != null)
                {
                    await _presenter.ToggleEnabledAsync();
                }
                _logger?.LogInformation("Hotkey toggled Music overlay visibility");
            }
            else if (binding.ActionId == nameof(HotkeyConfig.PreviousSongHotkey))
            {
                _playbackService?.RequestPreviousTrack();
                _logger?.LogInformation("Hotkey requested Previous Track");
            }
            else if (binding.ActionId == nameof(HotkeyConfig.NextSongHotkey))
            {
                _playbackService?.RequestNextTrack();
                _logger?.LogInformation("Hotkey requested Next Track");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Hotkey action failed: {Key} {Modifier}", e.Key, e.Modifier);
        }
    }

    private static bool IsEqual(HotkeyConfig a, HotkeyConfig b)
    {
        return string.Equals(a.ToggleAutoPickHotkey, b.ToggleAutoPickHotkey, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(a.ToggleMusicHotkey, b.ToggleMusicHotkey, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(a.PreviousSongHotkey, b.PreviousSongHotkey, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(a.NextSongHotkey, b.NextSongHotkey, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(a.ToggleMusicTimelineHotkey, b.ToggleMusicTimelineHotkey, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(a.ToggleMusicOverlayHotkey, b.ToggleMusicOverlayHotkey, StringComparison.OrdinalIgnoreCase);
    }

    private static HotkeyConfig CloneConfig(HotkeyConfig source) => new()
    {
        ToggleAutoPickHotkey = source.ToggleAutoPickHotkey,
        ToggleMusicHotkey = source.ToggleMusicHotkey,
        PreviousSongHotkey = source.PreviousSongHotkey,
        NextSongHotkey = source.NextSongHotkey,
        ToggleMusicTimelineHotkey = source.ToggleMusicTimelineHotkey,
        ToggleMusicOverlayHotkey = source.ToggleMusicOverlayHotkey
    };

    private static HotkeyConfig CreateEmptyConfig() => new()
    {
        ToggleAutoPickHotkey = string.Empty,
        ToggleMusicHotkey = string.Empty,
        PreviousSongHotkey = string.Empty,
        NextSongHotkey = string.Empty,
        ToggleMusicTimelineHotkey = string.Empty,
        ToggleMusicOverlayHotkey = string.Empty
    };

    private static Dictionary<MusicHotkey, ActiveHotkeyBinding> CreateBindings(HotkeyConfig config)
    {
        var entries = MusicHotkey.Entries(config);
        var bindings = new Dictionary<MusicHotkey, ActiveHotkeyBinding>();
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (string.IsNullOrWhiteSpace(entry.Text)) continue;
            var key = MusicHotkey.Parse(entry.Text);
            bindings.Add(key, new ActiveHotkeyBinding(entry.Name, BindingActionIds[i], entry.Text));
        }
        return bindings;
    }

    private static Dictionary<MusicHotkey, ActiveHotkeyBinding> CloneBindings(
        Dictionary<MusicHotkey, ActiveHotkeyBinding> source) => new(source);

    private static bool BindingsEqual(
        Dictionary<MusicHotkey, ActiveHotkeyBinding> left,
        Dictionary<MusicHotkey, ActiveHotkeyBinding> right)
    {
        return left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var binding) && pair.Value == binding);
    }

    private static string FormatBindings(Dictionary<MusicHotkey, ActiveHotkeyBinding> bindings)
    {
        var ordered = bindings.Values
            .OrderBy(binding => Array.IndexOf(BindingActionIds, binding.ActionId))
            .ThenBy(binding => binding.Text, StringComparer.OrdinalIgnoreCase)
            .Select(binding => binding.Name + "=" + binding.Text)
            .ToArray();
        return ordered.Length == 0 ? "（无快捷键生效）" : string.Join("、", ordered);
    }

    private static string FormatIncompleteRollback(
        Dictionary<MusicHotkey, ActiveHotkeyBinding> actualBindings,
        Dictionary<MusicHotkey, ActiveHotkeyBinding> unconfirmedBindings)
    {
        var message = "，上一次配置恢复不完整。实际生效快捷键：" + FormatBindings(actualBindings);
        if (unconfirmedBindings.Count > 0)
        {
            message += "；无法确认是否仍生效：" + FormatBindings(unconfirmedBindings);
        }
        return message;
    }

    private static HotkeyConfig ConfigFromBindings(Dictionary<MusicHotkey, ActiveHotkeyBinding> bindings)
    {
        var config = CreateEmptyConfig();
        foreach (var binding in bindings.Values.OrderBy(binding => Array.IndexOf(BindingActionIds, binding.ActionId)))
        {
            switch (binding.ActionId)
            {
                case nameof(HotkeyConfig.ToggleAutoPickHotkey) when string.IsNullOrEmpty(config.ToggleAutoPickHotkey):
                    config.ToggleAutoPickHotkey = binding.Text;
                    break;
                case nameof(HotkeyConfig.ToggleMusicHotkey) when string.IsNullOrEmpty(config.ToggleMusicHotkey):
                    config.ToggleMusicHotkey = binding.Text;
                    break;
                case nameof(HotkeyConfig.PreviousSongHotkey) when string.IsNullOrEmpty(config.PreviousSongHotkey):
                    config.PreviousSongHotkey = binding.Text;
                    break;
                case nameof(HotkeyConfig.NextSongHotkey) when string.IsNullOrEmpty(config.NextSongHotkey):
                    config.NextSongHotkey = binding.Text;
                    break;
                case nameof(HotkeyConfig.ToggleMusicTimelineHotkey) when string.IsNullOrEmpty(config.ToggleMusicTimelineHotkey):
                    config.ToggleMusicTimelineHotkey = binding.Text;
                    break;
                case nameof(HotkeyConfig.ToggleMusicOverlayHotkey) when string.IsNullOrEmpty(config.ToggleMusicOverlayHotkey):
                    config.ToggleMusicOverlayHotkey = binding.Text;
                    break;
            }
        }
        return config;
    }

    private HotkeyApplyResult CreateApplyResult(bool success, string errorMessage, string? failedKeyName)
    {
        var effectiveBindings = _activeBindings.Values
            .OrderBy(binding => Array.IndexOf(BindingActionIds, binding.ActionId))
            .ThenBy(binding => binding.Text, StringComparer.OrdinalIgnoreCase)
            .Select(binding => new HotkeyBinding(binding.Name, binding.Text))
            .ToArray();

        return new HotkeyApplyResult(success, errorMessage, failedKeyName, CloneConfig(_activeConfig))
        {
            EffectiveBindings = effectiveBindings,
            UnconfirmedBindings = _unconfirmedBindings.Values
                .OrderBy(binding => Array.IndexOf(BindingActionIds, binding.ActionId))
                .ThenBy(binding => binding.Text, StringComparer.OrdinalIgnoreCase)
                .Select(binding => new HotkeyBinding(binding.Name, binding.Text))
                .ToArray()
        };
    }

    private sealed record ActiveHotkeyBinding(string Name, string ActionId, string Text);
    private sealed record ApplySnapshot(
        Dictionary<MusicHotkey, ActiveHotkeyBinding> ActiveBindings,
        Dictionary<MusicHotkey, ActiveHotkeyBinding> UnconfirmedBindings,
        HotkeyConfig PersistedConfig);

    public void Dispose()
    {
        _applyGate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _hook?.Dispose();
            _hook = null;
            _activeBindings.Clear();
            _unconfirmedBindings.Clear();
            _activeConfig = CreateEmptyConfig();
        }
        finally
        {
            _applyGate.Release();
        }
    }
}
