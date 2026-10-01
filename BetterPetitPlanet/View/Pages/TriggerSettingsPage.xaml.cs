using System.Windows.Controls;
using BetterPetitPlanet.ViewModel.Pages;

namespace BetterPetitPlanet.View.Pages;

public partial class TriggerSettingsPage : Page
{
    public TriggerSettingsPageViewModel ViewModel { get; }

    public TriggerSettingsPage(TriggerSettingsPageViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = ViewModel;
        InitializeComponent();
    }
}
