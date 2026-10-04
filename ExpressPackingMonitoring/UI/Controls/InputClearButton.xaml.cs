using System.Windows;
using System.Windows.Controls;

namespace ExpressPackingMonitoring.UI.Controls
{
    /// <summary>
    /// 输入框右侧的"清除"叉号公共控件：主界面扫码框、设置页搜索框与回放的单号精准搜索共用。
    /// 只负责把 <see cref="Target"/> 的文本清空，附带把焦点交回输入框；
    /// 过滤、高亮、占位提示等后续动作仍由各页面已有的 TextChanged 逻辑处理。
    /// </summary>
    public sealed partial class InputClearButton : UserControl
    {
        public static readonly DependencyProperty TargetProperty =
            DependencyProperty.Register(
                nameof(Target),
                typeof(TextBox),
                typeof(InputClearButton),
                new PropertyMetadata(null));

        public static readonly DependencyProperty ButtonSizeProperty =
            DependencyProperty.Register(
                nameof(ButtonSize),
                typeof(double),
                typeof(InputClearButton),
                new PropertyMetadata(28d));

        public static readonly DependencyProperty IconSizeProperty =
            DependencyProperty.Register(
                nameof(IconSize),
                typeof(double),
                typeof(InputClearButton),
                new PropertyMetadata(14d));

        public static readonly DependencyProperty ButtonCornerRadiusProperty =
            DependencyProperty.Register(
                nameof(ButtonCornerRadius),
                typeof(CornerRadius),
                typeof(InputClearButton),
                new PropertyMetadata(new CornerRadius(14)));

        public InputClearButton()
        {
            InitializeComponent();
        }

        /// <summary>要清空的输入框；没设置时点击什么都不做。</summary>
        public TextBox? Target
        {
            get => (TextBox?)GetValue(TargetProperty);
            set => SetValue(TargetProperty, value);
        }

        /// <summary>命中区域边长。默认按主界面扫码框的 28 走，放在更矮的输入框里时按需调小。</summary>
        public double ButtonSize
        {
            get => (double)GetValue(ButtonSizeProperty);
            set => SetValue(ButtonSizeProperty, value);
        }

        /// <summary>叉号图标边长。</summary>
        public double IconSize
        {
            get => (double)GetValue(IconSizeProperty);
            set => SetValue(IconSizeProperty, value);
        }

        /// <summary>圆角。默认取 ButtonSize 的一半，改尺寸时一并调整。</summary>
        public CornerRadius ButtonCornerRadius
        {
            get => (CornerRadius)GetValue(ButtonCornerRadiusProperty);
            set => SetValue(ButtonCornerRadiusProperty, value);
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (Target is not { } target)
                return;

            // 走输入框自己的 TextChanged：各页面的过滤、占位提示、去抖都照常触发。
            if (target.Text.Length > 0)
                target.Text = string.Empty;

            target.Focus();
        }
    }
}
