using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ExpressPackingMonitoring.UI.Controls
{
    /// <summary>
    /// 状态小卡片公共控件：图标 + 标题 + 短状态 + 详情/内容区。
    /// 主界面、录像文件备份主机窗口与录像从机界面共用，保证三处视觉一致。
    /// </summary>
    public sealed partial class StatusCard : UserControl
    {
        public static readonly DependencyProperty CardIconProperty =
            DependencyProperty.Register(
                nameof(CardIcon),
                typeof(Geometry),
                typeof(StatusCard),
                new PropertyMetadata(null));

        public static readonly DependencyProperty CardTitleProperty =
            DependencyProperty.Register(
                nameof(CardTitle),
                typeof(string),
                typeof(StatusCard),
                new PropertyMetadata(""));

        public static readonly DependencyProperty ShortStatusTextProperty =
            DependencyProperty.Register(
                nameof(ShortStatusText),
                typeof(string),
                typeof(StatusCard),
                new PropertyMetadata(""));

        public static readonly DependencyProperty ShortStatusToolTipProperty =
            DependencyProperty.Register(
                nameof(ShortStatusToolTip),
                typeof(string),
                typeof(StatusCard),
                new PropertyMetadata(""));

        public static readonly DependencyProperty DetailTextProperty =
            DependencyProperty.Register(
                nameof(DetailText),
                typeof(string),
                typeof(StatusCard),
                new PropertyMetadata("", OnDetailTextChanged));

        public static readonly DependencyProperty CardContentProperty =
            DependencyProperty.Register(
                nameof(CardContent),
                typeof(object),
                typeof(StatusCard),
                new PropertyMetadata(null, OnCardContentChanged));

        public static readonly DependencyProperty DeviceItemsSourceProperty =
            DependencyProperty.Register(
                nameof(DeviceItemsSource),
                typeof(System.Collections.IEnumerable),
                typeof(StatusCard),
                new PropertyMetadata(null, OnDeviceItemsSourceChanged));

        public StatusCard()
        {
            InitializeComponent();
            UpdateContentVisibility();
        }

        public Geometry? CardIcon
        {
            get => (Geometry?)GetValue(CardIconProperty);
            set => SetValue(CardIconProperty, value);
        }

        public string CardTitle
        {
            get => (string)GetValue(CardTitleProperty);
            set => SetValue(CardTitleProperty, value);
        }

        public string ShortStatusText
        {
            get => (string)GetValue(ShortStatusTextProperty);
            set => SetValue(ShortStatusTextProperty, value);
        }

        public string ShortStatusToolTip
        {
            get => (string)GetValue(ShortStatusToolTipProperty);
            set => SetValue(ShortStatusToolTipProperty, value);
        }

        public string DetailText
        {
            get => (string)GetValue(DetailTextProperty);
            set => SetValue(DetailTextProperty, value);
        }

        public object? CardContent
        {
            get => GetValue(CardContentProperty);
            set => SetValue(CardContentProperty, value);
        }

        public System.Collections.IEnumerable? DeviceItemsSource
        {
            get => (System.Collections.IEnumerable?)GetValue(DeviceItemsSourceProperty);
            set => SetValue(DeviceItemsSourceProperty, value);
        }

        private static void OnDetailTextChanged(
            DependencyObject d,
            DependencyPropertyChangedEventArgs e) =>
            ((StatusCard)d).UpdateContentVisibility();

        private static void OnCardContentChanged(
            DependencyObject d,
            DependencyPropertyChangedEventArgs e) =>
            ((StatusCard)d).UpdateContentVisibility();

        private static void OnDeviceItemsSourceChanged(
            DependencyObject d,
            DependencyPropertyChangedEventArgs e) =>
            ((StatusCard)d).UpdateContentVisibility();

        private void UpdateContentVisibility()
        {
            bool hasDevices = DeviceItemsSource != null;
            DeviceItemsControl.ItemsSource = DeviceItemsSource;
            DeviceItemsControl.Visibility = hasDevices
                ? Visibility.Visible
                : Visibility.Collapsed;

            bool hasContent = !hasDevices && CardContent != null;
            ContentHost.Content = hasContent ? CardContent : null;
            ContentHost.Visibility = hasContent
                ? Visibility.Visible
                : Visibility.Collapsed;
            DetailTextBlock.Visibility = !hasDevices && !hasContent && !string.IsNullOrEmpty(DetailText)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }
}
