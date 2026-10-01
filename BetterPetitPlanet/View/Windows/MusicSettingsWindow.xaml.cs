using System;
using BetterPetitPlanet.ViewModel.Pages;
using Wpf.Ui.Controls;

namespace BetterPetitPlanet.View.Windows;

public partial class MusicSettingsWindow : FluentWindow
{
    public MusicSettingsWindow(MusicPageViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
