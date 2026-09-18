using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media;

namespace ExpressPackingMonitoring.UI.Controls;

public sealed class SmoothProgressBar : ProgressBar
{
    public static readonly DependencyProperty ResetToMinimumImmediatelyProperty = DependencyProperty.Register(
        nameof(ResetToMinimumImmediately), typeof(bool), typeof(SmoothProgressBar), new PropertyMetadata(false));
    public bool ResetToMinimumImmediately { get => (bool)GetValue(ResetToMinimumImmediatelyProperty); set => SetValue(ResetToMinimumImmediatelyProperty, value); }
    public static readonly DependencyProperty AnimationDurationProperty = DependencyProperty.Register(
        nameof(AnimationDuration), typeof(double), typeof(SmoothProgressBar), new PropertyMetadata(300d));
    public double AnimationDuration { get => (double)GetValue(AnimationDurationProperty); set => SetValue(AnimationDurationProperty, value); }
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
        if (progressBar.ResetToMinimumImmediately && Math.Abs(target - progressBar.Minimum) < double.Epsilon)
        {
            progressBar.BeginAnimation(ValueProperty, null);
            progressBar.SetCurrentValue(ValueProperty, target);
            return;
        }
        progressBar.BeginAnimation(
            ValueProperty,
                new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(Math.Max(1, progressBar.AnimationDuration)))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                },
                HandoffBehavior.SnapshotAndReplace);
    }
}
