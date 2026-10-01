using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask;
using BetterPetitPlanet.GameTask.AutoPick;
using Serilog;

namespace BetterPetitPlanet.ViewModel.Pages;

public partial class TriggerSettingsPageViewModel : ObservableObject
{
    private readonly IConfigService _configService;
    private readonly AutoPickTrigger _autoPickTrigger;
    private readonly GameTaskManager _taskManager;

    [ObservableProperty]
    private bool _autoPickEnabled;

    [ObservableProperty]
    private string _selectedOcrEngine = "DirectML";

    [ObservableProperty]
    private string _selectedPickKey = "F";

    [ObservableProperty]
    private int _pressDurationMs = 40;

    [ObservableProperty]
    private int _cooldownMs = 50;

    public List<string> AvailableOcrEngines { get; } = ["DirectML", "Paddle", "Rapid*"];
    public List<string> AvailablePickKeys { get; } = ["F", "E", "G", "空格"];

    public TriggerSettingsPageViewModel(
        IConfigService configService,
        AutoPickTrigger autoPickTrigger,
        GameTaskManager taskManager)
    {
        _configService = configService;
        _autoPickTrigger = autoPickTrigger;
        _taskManager = taskManager;
        LoadFromConfig();
    }

    private void LoadFromConfig()
    {
        var cfg = _configService.Config.AutoPick;
        AutoPickEnabled = cfg.Enabled;
        if (cfg.OcrEngine.StartsWith("Rapid", System.StringComparison.OrdinalIgnoreCase))
        {
            SelectedOcrEngine = "Rapid*";
        }
        else if (cfg.OcrEngine.StartsWith("Paddle", System.StringComparison.OrdinalIgnoreCase))
        {
            SelectedOcrEngine = "Paddle";
        }
        else
        {
            SelectedOcrEngine = "DirectML";
        }
        SelectedPickKey = cfg.PickKey;
        PressDurationMs = cfg.PressDurationMs;
        CooldownMs = cfg.CooldownMs;

        if (_autoPickTrigger != null)
        {
            _autoPickTrigger.IsEnabled = AutoPickEnabled;
        }
    }

    private void SaveToConfig()
    {
        var cfg = _configService.Config.AutoPick;
        cfg.Enabled = AutoPickEnabled;
        cfg.OcrEngine = SelectedOcrEngine.Replace("*", "").Trim();
        cfg.PickKey = SelectedPickKey;
        cfg.PressDurationMs = PressDurationMs;
        cfg.CooldownMs = CooldownMs;

        if (_autoPickTrigger != null)
        {
            _autoPickTrigger.IsEnabled = AutoPickEnabled;
        }
        _configService.Save();
    }

    partial void OnAutoPickEnabledChanged(bool value)
    {
        SaveToConfig();
        if (value && _taskManager != null && !_taskManager.IsRunning)
        {
            _taskManager.Start();
        }
    }
    partial void OnSelectedOcrEngineChanged(string value) => SaveToConfig();
    partial void OnSelectedPickKeyChanged(string value) => SaveToConfig();
    partial void OnPressDurationMsChanged(int value) => SaveToConfig();
    partial void OnCooldownMsChanged(int value) => SaveToConfig();
}
