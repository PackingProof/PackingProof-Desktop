using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace ExpressPackingMonitoring.UI;

public partial class ModeTransitionButton : UserControl
{
    private string _mode = "发货";
    private Border? _blueLayer;
    private Border? _orangeLayer;
    private TextBlock? _modeText;
    private Path? _modeIcon;

    public ModeTransitionButton()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => ApplyMode(_mode, animate: false);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged oldNotifier)
            oldNotifier.PropertyChanged -= OnViewModelPropertyChanged;
        if (e.NewValue is INotifyPropertyChanged newNotifier)
            newNotifier.PropertyChanged += OnViewModelPropertyChanged;
        if (e.NewValue is object viewModel)
        {
            string? mode = viewModel.GetType().GetProperty("CurrentMode")?.GetValue(viewModel) as string;
            if (!string.IsNullOrWhiteSpace(mode)) ApplyMode(mode, animate: false);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "CurrentMode") return;
        string? mode = sender?.GetType().GetProperty("CurrentMode")?.GetValue(sender) as string;
        if (!string.IsNullOrWhiteSpace(mode)) Dispatcher.InvokeAsync(() => ApplyMode(mode, animate: true));
    }

    private void ApplyMode(string mode, bool animate)
    {
        _mode = mode == "退货" ? "退货" : "发货";
        if (_blueLayer == null || _orangeLayer == null) return;
        _blueLayer.RenderTransform ??= new TranslateTransform();
        _orangeLayer.RenderTransform ??= new TranslateTransform();
        var target = _mode == "退货" ? _orangeLayer : _blueLayer;
        var other = ReferenceEquals(target, _orangeLayer) ? _blueLayer : _orangeLayer;
        target.Visibility = Visibility.Visible;
        other.Visibility = Visibility.Visible;
        if (!animate)
        {
            target.Opacity = 1;
            other.Opacity = 0;
            target.RenderTransform = new TranslateTransform();
            other.RenderTransform = new TranslateTransform();
        }
        else
        {
            other.Opacity = 1;
            target.Opacity = 1;
            var transform = (TranslateTransform)target.RenderTransform;
            transform.X = _mode == "退货" ? 160 : -160;
            var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(420))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                };
            animation.Completed += (_, _) => other.Opacity = 0;
            transform.BeginAnimation(TranslateTransform.XProperty, animation);
        }
        _modeText!.Text = _mode;
        _modeIcon!.Data = FindResource(_mode == "退货" ? "FluentReturnIcon" : "FluentBoxIcon");
    }
}
