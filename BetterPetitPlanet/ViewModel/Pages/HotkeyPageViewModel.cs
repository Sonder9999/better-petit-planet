using System;
using System.Threading.Tasks;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace BetterPetitPlanet.ViewModel.Pages;

public partial class HotkeyPageViewModel : ObservableObject
{
    private readonly IConfigService _config;
    private readonly HotkeyService? _hotkeys;

    [ObservableProperty] private string _toggleAutoPickHotkey = "F8";
    [ObservableProperty] private string _toggleMusicHotkey = "F9";
    [ObservableProperty] private string _previousSongHotkey = "F10";
    [ObservableProperty] private string _nextSongHotkey = "F11";
    [ObservableProperty] private string _toggleMusicTimelineHotkey = "Ctrl+F9";
    [ObservableProperty] private string _toggleMusicOverlayHotkey = "Ctrl+F12";

    [ObservableProperty] private string _statusText = "修改后点击“应用快捷键”";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _isDirty;

    public HotkeyPageViewModel(IConfigService configService, HotkeyService? hotkeys = null)
    {
        _config = configService ?? throw new ArgumentNullException(nameof(configService));
        _hotkeys = hotkeys;

        var active = _hotkeys?.ActiveConfig ?? _config.Config.Hotkey;
        UpdateFromConfig(active);
        IsDirty = false;
    }

    private void UpdateFromConfig(HotkeyConfig c)
    {
        ToggleAutoPickHotkey = c.ToggleAutoPickHotkey;
        ToggleMusicHotkey = c.ToggleMusicHotkey;
        PreviousSongHotkey = c.PreviousSongHotkey;
        NextSongHotkey = c.NextSongHotkey;
        ToggleMusicTimelineHotkey = c.ToggleMusicTimelineHotkey;
        ToggleMusicOverlayHotkey = c.ToggleMusicOverlayHotkey;
    }

    partial void OnToggleAutoPickHotkeyChanged(string value) => MarkDirty();
    partial void OnToggleMusicHotkeyChanged(string value) => MarkDirty();
    partial void OnPreviousSongHotkeyChanged(string value) => MarkDirty();
    partial void OnNextSongHotkeyChanged(string value) => MarkDirty();
    partial void OnToggleMusicTimelineHotkeyChanged(string value) => MarkDirty();
    partial void OnToggleMusicOverlayHotkeyChanged(string value) => MarkDirty();

    private void MarkDirty()
    {
        IsDirty = true;
    }

    [RelayCommand]
    public async Task ApplyHotkeysAsync()
    {
        var proposed = new HotkeyConfig
        {
            ToggleAutoPickHotkey = ToggleAutoPickHotkey,
            ToggleMusicHotkey = ToggleMusicHotkey,
            PreviousSongHotkey = PreviousSongHotkey,
            NextSongHotkey = NextSongHotkey,
            ToggleMusicTimelineHotkey = ToggleMusicTimelineHotkey,
            ToggleMusicOverlayHotkey = ToggleMusicOverlayHotkey
        };

        try
        {
            HotkeyService.Validate(proposed);
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusText = "快捷键校验失败：" + ex.Message;
            Log.Warning(ex, "Hotkey configuration rejected during validation");
            return;
        }

        if (_hotkeys != null)
        {
            var result = await _hotkeys.ApplyHotkeysAsync(proposed);
            if (result.Success)
            {
                HasError = false;
                StatusText = result.ErrorMessage;
                IsDirty = false;
                UpdateFromConfig(result.ActiveConfig);
            }
            else
            {
                HasError = true;
                StatusText = result.ErrorMessage;
                // Roll back fields to actual active configuration
                UpdateFromConfig(result.ActiveConfig);
                IsDirty = false;
            }
        }
        else
        {
            var lastSavedConfig = CloneConfig(_config.Config.Hotkey);
            _config.Config.Hotkey = proposed;
            try
            {
                await _config.SaveAsync();
                HasError = false;
                StatusText = "快捷键已成功保存";
                IsDirty = false;
            }
            catch (Exception ex)
            {
                _config.Config.Hotkey = CloneConfig(lastSavedConfig);
                UpdateFromConfig(lastSavedConfig);
                HasError = true;
                StatusText = "快捷键保存失败：" + ex.Message + "，已恢复到上一次保存配置";
                IsDirty = false;
                Log.Warning(ex, "Failed to save hotkey configuration without HotkeyService");
            }
        }
    }

    [RelayCommand]
    public void ResetToDefault()
    {
        var def = new HotkeyConfig();
        UpdateFromConfig(def);
        IsDirty = true;
        HasError = false;
        StatusText = "已重置为默认值，点击“应用快捷键”保存生效";
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
}
