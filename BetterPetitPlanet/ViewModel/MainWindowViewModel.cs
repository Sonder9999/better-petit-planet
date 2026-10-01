using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BetterPetitPlanet.Core.Config;
using System.Windows;

namespace BetterPetitPlanet.ViewModel;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IConfigService _configService;

    [ObservableProperty]
    private string _windowTitle = "BetterPetitPlanet - 更好的星布谷地";

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private WindowState _windowState = WindowState.Normal;

    public MainWindowViewModel(IConfigService configService)
    {
        _configService = configService;
    }

    [RelayCommand]
    private void Closing()
    {
        _configService.Save();
    }
}
