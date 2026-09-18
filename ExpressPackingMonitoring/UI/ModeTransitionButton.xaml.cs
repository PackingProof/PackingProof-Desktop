using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ExpressPackingMonitoring.UI;

public partial class ModeTransitionButton : UserControl
{
    private const string ReturnMode = "退货";
    private const string PackMode = "发货";
    private const double FallbackSlideDistance = 160;
    private const double SlideDurationMs = 180;

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact),
        typeof(bool),
        typeof(ModeTransitionButton),
        new PropertyMetadata(false));

    private string _mode = PackMode;

    public ModeTransitionButton()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => ApplyMode(_mode, animate: false);
        // 紧凑态切换会改变宽度，退货以外的静止位置必须跟着宽度重新落位，否则橙色层会露边
        SizeChanged += (_, _) => { if (_mode != ReturnMode) ApplyMode(_mode, animate: false); };
    }

    /// <summary>窄窗口时只保留图标，由主窗口的 IsModeButtonCompact 驱动</summary>
    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
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
        _mode = mode == ReturnMode ? ReturnMode : PackMode;
        ModeIcon.Data = (Geometry)FindResource(_mode == ReturnMode ? "FluentReturnIcon" : "FluentBoxIcon");

        // 退货静止在按钮内（X=0），发货静止在按钮右侧之外（X=宽度）
        double restingX = _mode == ReturnMode
            ? 0
            : (ActualWidth > 0 ? ActualWidth : FallbackSlideDistance);

        if (!animate)
        {
            OrangeSlide.BeginAnimation(TranslateTransform.XProperty, null);
            OrangeSlide.X = restingX;
            return;
        }

        // 不指定 From，连续切换时从当前位置继续，避免来回跳。
        // 用 EaseOut 而不是 EaseInOut：EaseInOut 起步慢，按下去会像没反应。
        var animation = new DoubleAnimation(restingX, TimeSpan.FromMilliseconds(SlideDurationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        OrangeSlide.BeginAnimation(TranslateTransform.XProperty, animation);
    }
}
