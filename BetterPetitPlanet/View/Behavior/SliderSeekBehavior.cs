using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Xaml.Behaviors;

namespace BetterPetitPlanet.View.Behavior;

public sealed class SliderSeekBehavior : Behavior<Slider>
{
    private bool _isSeeking;

    public static readonly DependencyProperty BeginCommandProperty = DependencyProperty.Register(
        nameof(BeginCommand),
        typeof(ICommand),
        typeof(SliderSeekBehavior));

    public static readonly DependencyProperty ScrubbingCommandProperty = DependencyProperty.Register(
        nameof(ScrubbingCommand),
        typeof(ICommand),
        typeof(SliderSeekBehavior));

    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command),
        typeof(ICommand),
        typeof(SliderSeekBehavior));

    public ICommand? BeginCommand
    {
        get => (ICommand?)GetValue(BeginCommandProperty);
        set => SetValue(BeginCommandProperty, value);
    }

    public ICommand? ScrubbingCommand
    {
        get => (ICommand?)GetValue(ScrubbingCommandProperty);
        set => SetValue(ScrubbingCommandProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        AssociatedObject.PreviewMouseMove += OnPreviewMouseMove;
        AssociatedObject.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        AssociatedObject.LostMouseCapture += OnLostMouseCapture;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        AssociatedObject.PreviewMouseMove -= OnPreviewMouseMove;
        AssociatedObject.PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;
        AssociatedObject.LostMouseCapture -= OnLostMouseCapture;
        base.OnDetaching();
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (AssociatedObject == null) return;

        _isSeeking = true;
        ExecuteBeginSeek();

        UpdatePositionFromMouse(e);
        AssociatedObject.CaptureMouse();
        e.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSeeking || AssociatedObject == null) return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndSeek(e);
            return;
        }

        UpdatePositionFromMouse(e);
        e.Handled = true;
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSeeking) return;
        EndSeek(e);
        e.Handled = true;
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_isSeeking) return;
        if (Mouse.LeftButton == MouseButtonState.Pressed) return;
        EndSeek(null);
    }

    private void EndSeek(MouseEventArgs? e)
    {
        if (!_isSeeking) return;
        _isSeeking = false;

        if (AssociatedObject?.IsMouseCaptured == true)
        {
            AssociatedObject.ReleaseMouseCapture();
        }

        if (e != null && AssociatedObject != null)
        {
            UpdatePositionFromMouse(e);
        }

        ExecuteCommitSeek(AssociatedObject?.Value ?? 0);
    }

    private void UpdatePositionFromMouse(MouseEventArgs e)
    {
        if (AssociatedObject == null) return;

        var pos = e.GetPosition(AssociatedObject);
        var width = AssociatedObject.ActualWidth;
        if (width <= 0) return;

        var ratio = Math.Clamp(pos.X / width, 0.0, 1.0);
        var targetValue = AssociatedObject.Minimum + (AssociatedObject.Maximum - AssociatedObject.Minimum) * ratio;
        targetValue = Math.Clamp(targetValue, AssociatedObject.Minimum, AssociatedObject.Maximum);

        AssociatedObject.Value = targetValue;
        ExecuteScrubbing(targetValue);
    }

    private void ExecuteBeginSeek()
    {
        if (BeginCommand?.CanExecute(null) == true)
        {
            BeginCommand.Execute(null);
        }
    }

    private void ExecuteScrubbing(double value)
    {
        if (ScrubbingCommand?.CanExecute(value) == true)
        {
            ScrubbingCommand.Execute(value);
        }
    }

    private void ExecuteCommitSeek(double value)
    {
        if (Command?.CanExecute(value) == true)
        {
            Command.Execute(value);
        }
    }
}
