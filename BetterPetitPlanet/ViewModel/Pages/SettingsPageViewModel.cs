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

    public SettingsPageViewModel(IConfigService configService)
    {
        _configService = configService;
        _processName = _configService.Config.General.ProcessName;
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
}
