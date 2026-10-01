using System.Windows.Controls;
using BetterPetitPlanet.ViewModel.Pages;

namespace BetterPetitPlanet.View.Pages;

public partial class SettingsPage : Page
{
    public SettingsPageViewModel ViewModel { get; }

    public SettingsPage(SettingsPageViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = ViewModel;
        InitializeComponent();
    }
}
