using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Xaml.Behaviors;

namespace BetterPetitPlanet.View.Behavior;

public sealed class SliderSeekBehavior : Behavior<Slider>
{
    private bool _isPointerSeeking;

    public static readonly DependencyProperty BeginCommandProperty = DependencyProperty.Register(
        nameof(BeginCommand),
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

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        AssociatedObject.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        AssociatedObject.LostMouseCapture += OnLostMouseCapture;
    }

    protected override void OnDetaching()
    {
        AssociatedObject.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        AssociatedObject.PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;
        AssociatedObject.LostMouseCapture -= OnLostMouseCapture;
        base.OnDetaching();
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPointerSeeking = true;
        ExecuteBeginSeek();

        if (FindVisualParent<Thumb>(e.OriginalSource as DependencyObject) != null)
        {
            return;
        }

        SeekToPointerPosition(e);
        _isPointerSeeking = false;
        ExecuteSeek();
        e.Handled = true;
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPointerSeeking)
        {
            return;
        }

        _isPointerSeeking = false;
        ExecuteSeek();
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_isPointerSeeking || Mouse.LeftButton == MouseButtonState.Pressed)
        {
            return;
        }

        _isPointerSeeking = false;
        ExecuteSeek();
    }

    private void ExecuteBeginSeek()
    {
        if (BeginCommand?.CanExecute(null) == true)
        {
            BeginCommand.Execute(null);
        }
    }

    private void ExecuteSeek()
    {
        if (AssociatedObject == null) return;
        var value = AssociatedObject.Value;
        if (Command?.CanExecute(value) == true)
        {
            Command.Execute(value);
        }
    }

    private void SeekToPointerPosition(MouseEventArgs e)
    {
        if (AssociatedObject == null) return;

        var position = e.GetPosition(AssociatedObject);
        var width = AssociatedObject.ActualWidth;
        if (width <= 0) return;

        var ratio = Math.Clamp(position.X / width, 0.0, 1.0);
        var targetValue = AssociatedObject.Minimum + (AssociatedObject.Maximum - AssociatedObject.Minimum) * ratio;
        AssociatedObject.Value = targetValue;
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent)
            {
                return parent;
            }
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
}
