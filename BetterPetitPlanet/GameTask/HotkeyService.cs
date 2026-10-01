using System;
using System.Windows.Forms;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.AutoPick;
using BetterPetitPlanet.GameTask.Music.Service;
using BetterPetitPlanet.HotkeyCapture;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;

namespace BetterPetitPlanet.GameTask;

public sealed class HotkeyService : IDisposable
{
    private readonly ILogger<HotkeyService>? _logger;
    private readonly IConfigService _configService;
    private readonly AutoPickTrigger _autoPickTrigger;
    private readonly MusicPlaybackService _playbackService;
    private readonly GameTaskManager _taskManager;
    private HotkeyHook? _hook;

    public HotkeyService(
        IConfigService configService,
        AutoPickTrigger autoPickTrigger,
        MusicPlaybackService playbackService,
        GameTaskManager taskManager,
        ILogger<HotkeyService>? logger = null)
    {
        _configService = configService;
        _autoPickTrigger = autoPickTrigger;
        _playbackService = playbackService;
        _taskManager = taskManager;
        _logger = logger;
    }

    public void Initialize()
    {
        try
        {
            _hook?.Dispose();
            _hook = new HotkeyHook();
            _hook.KeyPressed += OnKeyPressed;

            RegisterHotkeys();
            _logger?.LogInformation("Global HotkeyService initialized successfully.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to register global hotkeys");
        }
    }

    private void RegisterHotkeys()
    {
        if (_hook == null) return;

        try
        {
            _hook.UnregisterHotKey();
        }
        catch
        {
            // Ignore unregister errors
        }

        var pickKey = ParseKey(_configService.Config.Hotkey.ToggleAutoPickHotkey, Keys.F8);
        var musicKey = ParseKey(_configService.Config.Hotkey.ToggleMusicHotkey, Keys.F9);
        var prevSongKey = ParseKey(_configService.Config.Hotkey.PreviousSongHotkey, Keys.F10);
        var nextSongKey = ParseKey(_configService.Config.Hotkey.NextSongHotkey, Keys.F11);
        var switchInstKey = ParseKey(_configService.Config.Hotkey.SwitchInstrumentHotkey, Keys.F12);

        TryRegisterKey(pickKey, "自动拾取 (F8)");
        TryRegisterKey(musicKey, "自动演奏 (F9)");
        TryRegisterKey(prevSongKey, "上一首歌曲 (F10)");
        TryRegisterKey(nextSongKey, "下一首歌曲 (F11)");
        TryRegisterKey(switchInstKey, "切换乐器 (F12)");
    }

    private void TryRegisterKey(Keys key, string description)
    {
        if (_hook == null) return;
        try
        {
            _hook.RegisterHotKey(User32.HotKeyModifiers.MOD_NONE, key);
            _logger?.LogInformation("快捷键注册成功: {Desc} -> {Key}", description, key);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "快捷键注册失败 (可能已被其他程序占用): {Desc} -> {Key}", description, key);
        }
    }

    private void OnKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        var pickKey = ParseKey(_configService.Config.Hotkey.ToggleAutoPickHotkey, Keys.F8);
        var musicKey = ParseKey(_configService.Config.Hotkey.ToggleMusicHotkey, Keys.F9);
        var prevSongKey = ParseKey(_configService.Config.Hotkey.PreviousSongHotkey, Keys.F10);
        var nextSongKey = ParseKey(_configService.Config.Hotkey.NextSongHotkey, Keys.F11);
        var switchInstKey = ParseKey(_configService.Config.Hotkey.SwitchInstrumentHotkey, Keys.F12);

        if (e.Key == pickKey)
        {
            _autoPickTrigger.IsEnabled = !_autoPickTrigger.IsEnabled;
            _configService.Config.AutoPick.Enabled = _autoPickTrigger.IsEnabled;
            _configService.Save();

            if (_autoPickTrigger.IsEnabled && !_taskManager.IsRunning)
            {
                bool started = _taskManager.Start();
                _logger?.LogInformation("Auto-started GameTaskManager on Hotkey F8 enable: {Started}", started);
            }

            _logger?.LogInformation("Hotkey toggled AutoPick: {State}", _autoPickTrigger.IsEnabled);
        }
        else if (e.Key == musicKey)
        {
            _playbackService.TogglePlayPause();
            _logger?.LogInformation("Hotkey toggled Music playback: {State}", _playbackService.State);
        }
        else if (e.Key == prevSongKey)
        {
            _playbackService.RequestPreviousTrack();
            _logger?.LogInformation("Hotkey requested Previous Track");
        }
        else if (e.Key == nextSongKey)
        {
            _playbackService.RequestNextTrack();
            _logger?.LogInformation("Hotkey requested Next Track");
        }
        else if (e.Key == switchInstKey)
        {
            _playbackService.RequestSwitchInstrument();
            _logger?.LogInformation("Hotkey requested Switch Instrument");
        }
    }

    private static Keys ParseKey(string keyStr, Keys defaultKey)
    {
        if (Enum.TryParse<Keys>(keyStr, true, out var key))
        {
            return key;
        }
        return defaultKey;
    }

    public void Dispose()
    {
        _hook?.Dispose();
        _hook = null;
    }
}
