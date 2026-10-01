using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask;
using BetterPetitPlanet.GameTask.AutoPick;
using Serilog;

namespace BetterPetitPlanet.ViewModel.Pages;

public partial class TriggerSettingsPageViewModel : ObservableObject, IDisposable
{
    private readonly IConfigService _configService;
    private readonly AutoPickTrigger? _autoPickTrigger;
    private readonly GameTaskManager? _taskManager;
    private bool _isSyncingFromTrigger;

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
        AutoPickTrigger? autoPickTrigger = null,
        GameTaskManager? taskManager = null)
    {
        _configService = configService;
        _autoPickTrigger = autoPickTrigger;
        _taskManager = taskManager;
        LoadFromConfig();

        if (_autoPickTrigger != null)
        {
            _autoPickTrigger.StateChanged += OnAutoPickTriggerStateChanged;
        }
    }

    public void SyncWithTrigger()
    {
        if (_autoPickTrigger != null && AutoPickEnabled != _autoPickTrigger.IsEnabled)
        {
            _isSyncingFromTrigger = true;
            try
            {
                AutoPickEnabled = _autoPickTrigger.IsEnabled;
            }
            finally
            {
                _isSyncingFromTrigger = false;
            }
        }
    }

    private void OnAutoPickTriggerStateChanged(bool enabled)
    {
        void Update()
        {
            if (AutoPickEnabled != enabled)
            {
                _isSyncingFromTrigger = true;
                try
                {
                    AutoPickEnabled = enabled;
                }
                finally
                {
                    _isSyncingFromTrigger = false;
                }
            }
        }

        if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.Invoke(Update);
        }
        else
        {
            Update();
        }
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
        if (_isSyncingFromTrigger)
        {
            return;
        }

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

    public void Dispose()
    {
        if (_autoPickTrigger != null)
        {
            _autoPickTrigger.StateChanged -= OnAutoPickTriggerStateChanged;
        }
    }
}
