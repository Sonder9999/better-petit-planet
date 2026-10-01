using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BetterPetitPlanet.Core.Config;

namespace BetterPetitPlanet.ViewModel.Pages;

public partial class SettingsPageViewModel : ObservableObject
{
    private readonly IConfigService _configService;

    [ObservableProperty]
    private string _processName = "PetitPlanet.exe";

    [ObservableProperty]
    private string _gameExecutablePath = string.Empty;

    [ObservableProperty]
    private string _selectedCaptureMode = "WindowsGraphicsCapture";

    public List<string> AvailableCaptureModes { get; } =
    [
        "WindowsGraphicsCapture",
        "PrintWindow"
    ];

    public SettingsPageViewModel(IConfigService configService)
    {
        _configService = configService;
        _processName = _configService.Config.General.ProcessName;
        _gameExecutablePath = _configService.Config.General.GameExecutablePath;

        var savedMode = _configService.Config.General.CaptureMode;
        if (savedMode.StartsWith("PrintWindow", StringComparison.OrdinalIgnoreCase) || savedMode.StartsWith("BitBlt", StringComparison.OrdinalIgnoreCase))
        {
            _selectedCaptureMode = "PrintWindow";
        }
        else
        {
            _selectedCaptureMode = "WindowsGraphicsCapture";
        }
        _configService.Config.General.CaptureMode = _selectedCaptureMode;
    }

    [RelayCommand]
    private void BrowseGamePath()
    {
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            Title = "选择星布谷地游戏客户端程序"
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            GameExecutablePath = dialog.FileName;
            _configService.Config.General.GameExecutablePath = GameExecutablePath;
            _configService.Save();
        }
    }

    [RelayCommand]
    private void OpenLogDirectory()
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "data", "logs");
        if (!Directory.Exists(logDir))
        {
            Directory.CreateDirectory(logDir);
        }
        System.Diagnostics.Process.Start(new ProcessStartInfo
        {
            FileName = logDir,
            UseShellExecute = true
        });
    }

    partial void OnProcessNameChanged(string value)
    {
        _configService.Config.General.ProcessName = value;
        _configService.Save();
    }

    partial void OnSelectedCaptureModeChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        _configService.Config.General.CaptureMode = value.Replace("*", "").Trim();
        _configService.Save();
    }
}
