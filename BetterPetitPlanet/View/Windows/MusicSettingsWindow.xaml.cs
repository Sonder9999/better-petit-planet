using System;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Overlay;
using BetterPetitPlanet.ViewModel.Pages;
using BetterPetitPlanet.ViewModel.Windows;
using Wpf.Ui.Controls;

namespace BetterPetitPlanet.View.Windows;

public partial class MusicSettingsWindow : FluentWindow
{
    private readonly MusicOverlaySettingsViewModel _overlayViewModel;

    public MusicOverlaySettingsViewModel OverlayViewModel => _overlayViewModel;

    public MusicSettingsWindow(MusicPageViewModel viewModel, MusicOverlaySettingsViewModel? overlayViewModel = null)
    {
        InitializeComponent();
        DataContext = viewModel;

        _overlayViewModel = overlayViewModel ?? new MusicOverlaySettingsViewModel(
            App.GetService<IConfigService>(),
            App.GetService<MusicOverlayPresenter>());

        OverlaySettingsControl.ViewModel = _overlayViewModel;

        Closed += (_, _) =>
        {
            _overlayViewModel.Dispose();
        };
    }
}
