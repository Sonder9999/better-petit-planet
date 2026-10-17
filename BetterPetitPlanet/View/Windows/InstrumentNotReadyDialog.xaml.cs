using System.Windows;
using Wpf.Ui.Controls;

namespace BetterPetitPlanet.View.Windows;

public partial class InstrumentNotReadyDialog : FluentWindow
{
    public InstrumentNotReadyDialog() : this("乐器")
    {
    }

    public InstrumentNotReadyDialog(string instrumentName)
    {
        InitializeComponent();
        SetInstrumentName(instrumentName);
    }

    public void SetInstrumentName(string instrumentName)
    {
        string name = string.IsNullOrWhiteSpace(instrumentName) ? "乐器" : instrumentName.Trim();
        if (TxtTitle != null)
        {
            TxtTitle.Text = $"未在游戏中检测到{name}演奏界面";
        }
        if (TxtSubtitle != null)
        {
            TxtSubtitle.Text = $"请确认是否已在《星布谷地》游戏中拿出{name}";
        }
        if (TxtDescription != null)
        {
            TxtDescription.Text = $"如果您已在游戏中进入了乐器演奏状态，可以直接点击【继续演奏】强制开始；如需返回游戏拿出{name}，请点击【确定】。";
        }
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
