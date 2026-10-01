using System.Windows;
using Wpf.Ui.Controls;

namespace BetterPetitPlanet.View.Windows;

public partial class InstrumentNotReadyDialog : FluentWindow
{
    public InstrumentNotReadyDialog()
    {
        InitializeComponent();
    }

    private void OnContinueClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
