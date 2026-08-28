using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace ExpressPackingMonitoring.UI.Controls;

public sealed class SmoothProgressBar : ProgressBar
{
    public static readonly DependencyProperty TargetValueProperty = DependencyProperty.Register(
        nameof(TargetValue),
        typeof(double),
        typeof(SmoothProgressBar),
        new PropertyMetadata(0d, OnTargetValueChanged));

    public double TargetValue
    {
        get => (double)GetValue(TargetValueProperty);
        set => SetValue(TargetValueProperty, value);
    }

    private static void OnTargetValueChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var progressBar = (SmoothProgressBar)dependencyObject;
        double target = Math.Clamp((double)args.NewValue, progressBar.Minimum, progressBar.Maximum);
        double current = progressBar.Value;
        progressBar.SetCurrentValue(ValueProperty, target);
        progressBar.BeginAnimation(
            ValueProperty,
            new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(300)),
            HandoffBehavior.SnapshotAndReplace);
    }
}
