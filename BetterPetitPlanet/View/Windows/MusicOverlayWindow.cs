using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BetterPetitPlanet.Core.Config;
using BetterPetitPlanet.GameTask.Music.Overlay;
using BetterPetitPlanet.View.Controls.Music;

namespace BetterPetitPlanet.View.Windows;

public sealed class MusicOverlayWindow : Window
{
    public MusicPlayfieldControl PlayfieldControl { get; } = new();
    private readonly Border _adjustmentBorder;
    private Point? _dragStart;
    private double _initialX, _initialY;

    public MusicOverlayLayoutConfig? EditableLayout { get; set; }
    public event Action? LayoutMoved;

    public MusicOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;

        var grid = new Grid();
        grid.Children.Add(PlayfieldControl);

        _adjustmentBorder = new Border
        {
            BorderBrush = Brushes.DeepSkyBlue,
            BorderThickness = new Thickness(3),
            Background = new SolidColorBrush(Color.FromArgb(30, 0, 100, 200)),
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "调整位置：拖动轨道区域，完成后在设置中点击结束调整",
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(20),
                FontSize = 20,
                FontWeight = FontWeights.SemiBold
            }
        };

        grid.Children.Add(_adjustmentBorder);
        Content = grid;

        SourceInitialized += (_, _) => GameWindowTracker.SetInteractive(this, false);

        _adjustmentBorder.MouseLeftButtonDown += (_, e) =>
        {
            if (EditableLayout == null) return;
            _dragStart = e.GetPosition(this);
            _initialX = EditableLayout.X;
            _initialY = EditableLayout.Y;
            _adjustmentBorder.CaptureMouse();
        };

        _adjustmentBorder.MouseMove += (_, e) =>
        {
            if (_dragStart is not Point start || EditableLayout is not { } layout) return;
            var point = e.GetPosition(this);
            if (ActualWidth > 0 && ActualHeight > 0)
            {
                layout.X = Math.Clamp(_initialX + (point.X - start.X) / ActualWidth, 0, Math.Max(0, 1 - layout.Width));
                layout.Y = Math.Clamp(_initialY + (point.Y - start.Y) / ActualHeight, 0, Math.Max(0, 1 - layout.Height));
                PlayfieldControl.LayoutConfig = layout;
                PlayfieldControl.InvalidateTracks();
                PlayfieldControl.UpdateSnapshot(PlayfieldControl.CurrentSnapshot);
                LayoutMoved?.Invoke();
            }
        };

        _adjustmentBorder.MouseLeftButtonUp += (_, _) =>
        {
            _dragStart = null;
            _adjustmentBorder.ReleaseMouseCapture();
        };

        Closed += (_, _) =>
        {
            _dragStart = null;
            if (_adjustmentBorder.IsMouseCaptured)
            {
                _adjustmentBorder.ReleaseMouseCapture();
            }
        };
    }

    public void SetAdjusting(bool adjusting)
    {
        GameWindowTracker.SetInteractive(this, adjusting);
        _adjustmentBorder.Visibility = adjusting ? Visibility.Visible : Visibility.Collapsed;
        PlayfieldControl.IsHitTestVisible = false;
    }
}
