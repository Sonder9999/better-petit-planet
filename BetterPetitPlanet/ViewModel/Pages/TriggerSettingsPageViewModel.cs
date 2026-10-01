using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
    private bool _isLoading;

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

    [ObservableProperty]
    private string _newWhitelistKeyword = string.Empty;

    [ObservableProperty]
    private string _newBlacklistKeyword = string.Empty;

    public ObservableCollection<string> WhitelistKeywords { get; } = [];
    public ObservableCollection<string> BlacklistKeywords { get; } = [];

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
        _isLoading = true;
        try
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

            WhitelistKeywords.Clear();
            var rawWhitelist = cfg.Whitelist ?? cfg.Keywords;
            if (rawWhitelist != null)
            {
                foreach (var w in rawWhitelist)
                {
                    var trimmed = w?.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed) && !WhitelistKeywords.Contains(trimmed))
                    {
                        WhitelistKeywords.Add(trimmed);
                    }
                }
            }
            if (WhitelistKeywords.Count == 0)
            {
                WhitelistKeywords.Add("拾取");
            }

            BlacklistKeywords.Clear();
            var rawBlacklist = cfg.Blacklist;
            if (rawBlacklist != null)
            {
                foreach (var b in rawBlacklist)
                {
                    var trimmed = b?.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed) && !BlacklistKeywords.Contains(trimmed))
                    {
                        BlacklistKeywords.Add(trimmed);
                    }
                }
            }
            if (BlacklistKeywords.Count == 0)
            {
                BlacklistKeywords.Add("拾取雪球");
            }

            if (_autoPickTrigger != null)
            {
                _autoPickTrigger.IsEnabled = AutoPickEnabled;
            }
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void SaveToConfig()
    {
        if (_isLoading)
        {
            return;
        }

        var cfg = _configService.Config.AutoPick;
        cfg.Enabled = AutoPickEnabled;
        cfg.OcrEngine = SelectedOcrEngine.Replace("*", "").Trim();
        cfg.PickKey = SelectedPickKey;
        cfg.PressDurationMs = PressDurationMs;
        cfg.CooldownMs = CooldownMs;
        cfg.Whitelist = WhitelistKeywords.ToList();
        cfg.Blacklist = BlacklistKeywords.ToList();

        if (_autoPickTrigger != null)
        {
            _autoPickTrigger.IsEnabled = AutoPickEnabled;
        }
        _configService.Save();
    }

    [RelayCommand]
    private void AddWhitelistKeyword()
    {
        var kw = NewWhitelistKeyword?.Trim();
        if (!string.IsNullOrWhiteSpace(kw) && !WhitelistKeywords.Contains(kw))
        {
            WhitelistKeywords.Add(kw);
            SaveToConfig();
            NewWhitelistKeyword = string.Empty;
        }
    }

    [RelayCommand]
    private void RemoveWhitelistKeyword(string keyword)
    {
        if (!string.IsNullOrWhiteSpace(keyword) && WhitelistKeywords.Remove(keyword))
        {
            SaveToConfig();
        }
    }

    [RelayCommand]
    private void AddBlacklistKeyword()
    {
        var kw = NewBlacklistKeyword?.Trim();
        if (!string.IsNullOrWhiteSpace(kw) && !BlacklistKeywords.Contains(kw))
        {
            BlacklistKeywords.Add(kw);
            SaveToConfig();
            NewBlacklistKeyword = string.Empty;
        }
    }

    [RelayCommand]
    private void RemoveBlacklistKeyword(string keyword)
    {
        if (!string.IsNullOrWhiteSpace(keyword) && BlacklistKeywords.Remove(keyword))
        {
            SaveToConfig();
        }
    }

    partial void OnAutoPickEnabledChanged(bool value)
    {
        if (_isLoading || _isSyncingFromTrigger)
        {
            return;
        }

        SaveToConfig();
        if (value && _taskManager != null && !_taskManager.IsRunning)
        {
            _taskManager.Start();
        }
    }
    partial void OnSelectedOcrEngineChanged(string value) { if (!_isLoading) SaveToConfig(); }
    partial void OnSelectedPickKeyChanged(string value) { if (!_isLoading) SaveToConfig(); }
    partial void OnPressDurationMsChanged(int value) { if (!_isLoading) SaveToConfig(); }
    partial void OnCooldownMsChanged(int value) { if (!_isLoading) SaveToConfig(); }

    public void Dispose()
    {
        if (_autoPickTrigger != null)
        {
            _autoPickTrigger.StateChanged -= OnAutoPickTriggerStateChanged;
        }
    }
}
