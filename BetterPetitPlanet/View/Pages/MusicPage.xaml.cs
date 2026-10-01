using System.Windows.Controls;
using BetterPetitPlanet.ViewModel.Pages;

namespace BetterPetitPlanet.View.Pages;

public partial class MusicPage : UserControl
{
    public MusicPageViewModel ViewModel { get; }

    public MusicPage(MusicPageViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = ViewModel;
        InitializeComponent();
    }
}
