using ExpressPackingMonitoring.Services;
using System;
using System.Windows;
using System.Windows.Controls;
using Xceed.Wpf.Toolkit;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// “录像清理”参数弹窗：按时间清理或按空间释放二选一进入，
    /// 选择预设或自定义参数后返回 ManualCleanupOptions；
    /// 预览、二次确认与执行由设置页统一完成。
    /// </summary>
    public sealed class ManualCleanupDialog : Window
    {
        private readonly ManualCleanupKind _kind;
        private readonly ComboBox _presetCombo;
        private readonly IntegerUpDown _daysInput;
        private readonly DoubleUpDown _spaceInput;

        public ManualCleanupOptions? SelectedOptions { get; private set; }

        public ManualCleanupDialog(ManualCleanupKind kind)
        {
            _kind = kind;
            Title = kind == ManualCleanupKind.ByTime
                ? "按时间清理本地录像"
                : "按空间释放本地录像";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.ToolWindow;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "PanelBackground");
            SetResourceReference(ForegroundProperty, "TextPrimary");

            var root = new Grid { Margin = new Thickness(24) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = Title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            root.Children.Add(title);

            var hint = new TextBlock
            {
                Text = "仅清理电脑本地录像，NAS 中已备份的文件不会受到影响",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            Grid.SetRow(hint, 1);
            root.Children.Add(hint);

            var content = new StackPanel();

            var optionRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 4)
            };
            _presetCombo = new ComboBox
            {
                Width = 220,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (kind == ManualCleanupKind.ByTime)
            {
                _presetCombo.Items.Add(new ComboBoxItem { Content = "7 天以前", Tag = "7" });
                _presetCombo.Items.Add(new ComboBoxItem { Content = "30 天以前", Tag = "30" });
                _presetCombo.Items.Add(new ComboBoxItem { Content = "90 天以前", Tag = "90" });
                _presetCombo.Items.Add(new ComboBoxItem { Content = "自定义天数", Tag = "0" });
                _daysInput = new IntegerUpDown
                {
                    Width = 110,
                    Minimum = 1,
                    Maximum = 3650,
                    Value = 30,
                    ToolTip = "请输入 1 到 3650 之间的天数",
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = Visibility.Collapsed
                };
                _spaceInput = null!;
                optionRow.Children.Add(_daysInput);
                var daysUnit = new TextBlock
                {
                    Text = "天",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0)
                };
                daysUnit.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                optionRow.Children.Add(daysUnit);
            }
            else
            {
                _presetCombo.Items.Add(new ComboBoxItem { Content = "10 GB", Tag = "10" });
                _presetCombo.Items.Add(new ComboBoxItem { Content = "50 GB", Tag = "50" });
                _presetCombo.Items.Add(new ComboBoxItem { Content = "自定义空间", Tag = "-1" });
                _spaceInput = new DoubleUpDown
                {
                    Width = 110,
                    Minimum = 1,
                    Maximum = 100000,
                    Value = 10,
                    FormatString = "F0",
                    ToolTip = "请输入大于 0 的 GB 数值",
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = Visibility.Collapsed
                };
                _daysInput = null!;
                optionRow.Children.Add(_spaceInput);
                var spaceUnit = new TextBlock
                {
                    Text = "GB",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0)
                };
                spaceUnit.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                optionRow.Children.Add(spaceUnit);
            }
            _presetCombo.SelectedIndex = 0;
            _presetCombo.SelectionChanged += (_, _) => UpdateCustomInputVisibility();
            optionRow.Children.Insert(0, _presetCombo);
            content.Children.Add(optionRow);

            var description = new TextBlock
            {
                Text = kind == ManualCleanupKind.ByTime
                    ? "清理结束时间早于所选天数的本地录像（例如 7 天以前）"
                    : "按最旧录像优先清理，直到释放所选空间；NAS 中已备份的文件不受影响",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
            description.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            content.Children.Add(description);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            var ok = new Button
            {
                Content = "开始清理",
                MinWidth = 96,
                Margin = new Thickness(0, 0, 10, 0),
                IsDefault = true
            };
            ok.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
            ok.Click += OkButton_Click;

            var cancel = new Button
            {
                Content = "取消",
                MinWidth = 96,
                IsCancel = true
            };
            cancel.SetResourceReference(StyleProperty, "SecondaryButtonStyle");

            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            content.Children.Add(buttons);

            Grid.SetRow(content, 2);
            root.Children.Add(content);
            Content = root;
        }

        private void UpdateCustomInputVisibility()
        {
            bool custom = _presetCombo.SelectedItem is ComboBoxItem { Tag: "0" or "-1" };
            if (_daysInput != null)
                _daysInput.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            if (_spaceInput != null)
                _spaceInput.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OkButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                SelectedOptions = _kind == ManualCleanupKind.ByTime
                    ? BuildTimeOptions()
                    : BuildSpaceOptions();
                DialogResult = true;
            }
            catch (Exception ex)
            {
                AppDialog.Error(this, ex.Message, "参数无效");
            }
        }

        private ManualCleanupOptions BuildTimeOptions()
        {
            if (_presetCombo.SelectedItem is ComboBoxItem item
                && item.Tag is string tag
                && int.TryParse(tag, out int preset)
                && preset > 0)
            {
                return new ManualCleanupOptions(
                    ManualCleanupKind.ByTime,
                    DateTime.Now.AddDays(-preset),
                    0);
            }

            int days = _daysInput.Value ?? 0;
            if (days < 1 || days > 3650)
                throw new InvalidOperationException("请输入 1 到 3650 之间的天数");
            return new ManualCleanupOptions(
                ManualCleanupKind.ByTime,
                DateTime.Now.AddDays(-days),
                0);
        }

        private ManualCleanupOptions BuildSpaceOptions()
        {
            if (_presetCombo.SelectedItem is ComboBoxItem item
                && item.Tag is string tag
                && double.TryParse(tag, out double preset))
            {
                if (preset > 0)
                {
                    return new ManualCleanupOptions(
                        ManualCleanupKind.BySpace,
                        DateTime.Now,
                        (long)(preset * 1024L * 1024L * 1024L));
                }
            }

            double gb = _spaceInput.Value ?? 0;
            if (gb <= 0)
                throw new InvalidOperationException("请输入大于 0 的 GB 数值");
            return new ManualCleanupOptions(
                ManualCleanupKind.BySpace,
                DateTime.Now,
                (long)(gb * 1024L * 1024L * 1024L));
        }
    }
}
