using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

    private void BannerCard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is UIElement element && e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            element.Clip = new RectangleGeometry(
                new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 8, 8);
        }
    }
}
