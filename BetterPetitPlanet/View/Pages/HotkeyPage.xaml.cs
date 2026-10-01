using System.Windows.Controls;
using BetterPetitPlanet.ViewModel.Pages;

namespace BetterPetitPlanet.View.Pages;

public partial class HotkeyPage : Page
{
    public HotkeyPageViewModel ViewModel { get; }

    public HotkeyPage(HotkeyPageViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = ViewModel;
        InitializeComponent();
    }
}
