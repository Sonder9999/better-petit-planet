using CommunityToolkit.Mvvm.ComponentModel;
using BetterPetitPlanet.Core.Config;

namespace BetterPetitPlanet.ViewModel.Pages;

public partial class HotkeyPageViewModel : ObservableObject
{
    private readonly IConfigService _configService;

    [ObservableProperty]
    private string _toggleAutoPickHotkey = "F8";

    [ObservableProperty]
    private string _toggleMusicHotkey = "F9";

    [ObservableProperty]
    private string _previousSongHotkey = "F10";

    [ObservableProperty]
    private string _nextSongHotkey = "F11";

    [ObservableProperty]
    private string _switchInstrumentHotkey = "F12";

    public HotkeyPageViewModel(IConfigService configService)
    {
        _configService = configService;
        _toggleAutoPickHotkey = _configService.Config.Hotkey.ToggleAutoPickHotkey;
        _toggleMusicHotkey = _configService.Config.Hotkey.ToggleMusicHotkey;
        _previousSongHotkey = _configService.Config.Hotkey.PreviousSongHotkey;
        _nextSongHotkey = _configService.Config.Hotkey.NextSongHotkey;
        _switchInstrumentHotkey = _configService.Config.Hotkey.SwitchInstrumentHotkey;
    }

    partial void OnToggleAutoPickHotkeyChanged(string value)
    {
        _configService.Config.Hotkey.ToggleAutoPickHotkey = value;
        _configService.Save();
    }

    partial void OnToggleMusicHotkeyChanged(string value)
    {
        _configService.Config.Hotkey.ToggleMusicHotkey = value;
        _configService.Save();
    }

    partial void OnPreviousSongHotkeyChanged(string value)
    {
        _configService.Config.Hotkey.PreviousSongHotkey = value;
        _configService.Save();
    }

    partial void OnNextSongHotkeyChanged(string value)
    {
        _configService.Config.Hotkey.NextSongHotkey = value;
        _configService.Save();
    }

    partial void OnSwitchInstrumentHotkeyChanged(string value)
    {
        _configService.Config.Hotkey.SwitchInstrumentHotkey = value;
        _configService.Save();
    }
}
