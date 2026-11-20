using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Threading;

namespace ExpressPackingMonitoring.UI.Controls;

public sealed class SmoothProgressBar : ProgressBar
{
    private FrameworkElement? _track;

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

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_track != null)
            _track.SizeChanged -= Track_SizeChanged;
        _track = GetTemplateChild("PART_Track") as FrameworkElement;
        if (_track != null)
            _track.SizeChanged += Track_SizeChanged;
        Loaded -= SmoothProgressBar_Loaded;
        Loaded += SmoothProgressBar_Loaded;
        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(UpdateIndicatorWidth));
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        UpdateIndicatorWidth();
        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(UpdateIndicatorWidth));
    }

    private static void OnTargetValueChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var progressBar = (SmoothProgressBar)dependencyObject;
        double target = Math.Clamp((double)args.NewValue, progressBar.Minimum, progressBar.Maximum);
        double current = progressBar.Value;
        if (!progressBar.IsLoaded)
        {
            progressBar.BeginAnimation(ValueProperty, null);
            progressBar.SetCurrentValue(ValueProperty, target);
            return;
        }
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

    private void Track_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateIndicatorWidth();

    private void SmoothProgressBar_Loaded(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(UpdateIndicatorWidth));

    private void UpdateIndicatorWidth()
    {
        if (_track is not { ActualWidth: > 0 }
            || GetTemplateChild("PART_Indicator") is not FrameworkElement indicator)
        {
            return;
        }

        double range = Maximum - Minimum;
        double fraction = range <= 0
            ? 0
            : Math.Clamp((Value - Minimum) / range, 0, 1);
        indicator.Width = _track.ActualWidth * fraction;
    }
}
