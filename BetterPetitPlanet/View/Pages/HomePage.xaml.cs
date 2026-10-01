using System.Windows.Controls;
using BetterPetitPlanet.ViewModel.Pages;

namespace BetterPetitPlanet.View.Pages;

public partial class HomePage : Page
{
    public HomePageViewModel ViewModel { get; }

    public HomePage(HomePageViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = ViewModel;
        InitializeComponent();
    }
}
