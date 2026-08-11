using ExpressPackingMonitoring.Services;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// “录像清理”参数弹窗：按时间清理或按空间释放二选一进入，
    /// 选择预设或自定义参数后返回 ManualCleanupOptions；
    /// 预览、二次确认与执行由设置页统一完成。
    /// </summary>
    public sealed partial class ManualCleanupDialog : Window
    {
        private readonly ManualCleanupKind _kind;

        public ManualCleanupOptions? SelectedOptions { get; private set; }

        public ManualCleanupDialog(ManualCleanupKind kind)
        {
            InitializeComponent();
            _kind = kind;
            if (kind == ManualCleanupKind.ByTime)
            {
                DialogTitleText.Text = "按时间清理本地录像";
                PresetCombo.Items.Add(new ComboBoxItem { Content = "7 天以前", Tag = "7" });
                PresetCombo.Items.Add(new ComboBoxItem { Content = "30 天以前", Tag = "30" });
                PresetCombo.Items.Add(new ComboBoxItem { Content = "90 天以前", Tag = "90" });
                PresetCombo.Items.Add(new ComboBoxItem { Content = "自定义天数", Tag = "0" });
                DaysInput.ToolTip = "请输入 1 到 3650 之间的天数";
                UnitText.Text = "天";
                ModeDescriptionText.Text = "清理结束时间早于所选天数的本地录像（例如 7 天以前）";
            }
            else
            {
                DialogTitleText.Text = "按空间释放本地录像";
                PresetCombo.Items.Add(new ComboBoxItem { Content = "10 GB", Tag = "10" });
                PresetCombo.Items.Add(new ComboBoxItem { Content = "50 GB", Tag = "50" });
                PresetCombo.Items.Add(new ComboBoxItem { Content = "自定义空间", Tag = "-1" });
                SpaceInput.ToolTip = "请输入大于 0 的 GB 数值";
                UnitText.Text = "GB";
                ModeDescriptionText.Text =
                    "按最旧录像优先清理，直到释放所选空间；NAS 中已备份的文件不受影响";
            }
            PresetCombo.SelectedIndex = 0;
            PresetCombo.SelectionChanged += (_, _) => UpdateCustomInputVisibility();
            UpdateCustomInputVisibility();
        }

        private void UpdateCustomInputVisibility()
        {
            bool custom = PresetCombo.SelectedItem is ComboBoxItem { Tag: "0" or "-1" };
            DaysInput.Visibility = _kind == ManualCleanupKind.ByTime && custom
                ? Visibility.Visible
                : Visibility.Collapsed;
            SpaceInput.Visibility = _kind == ManualCleanupKind.BySpace && custom
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
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
            if (PresetCombo.SelectedItem is ComboBoxItem item
                && item.Tag is string tag
                && int.TryParse(tag, out int preset)
                && preset > 0)
            {
                return new ManualCleanupOptions(
                    ManualCleanupKind.ByTime,
                    DateTime.Now.AddDays(-preset),
                    0);
            }

            int days = DaysInput.Value ?? 0;
            if (days < 1 || days > 3650)
                throw new InvalidOperationException("请输入 1 到 3650 之间的天数");
            return new ManualCleanupOptions(
                ManualCleanupKind.ByTime,
                DateTime.Now.AddDays(-days),
                0);
        }

        private ManualCleanupOptions BuildSpaceOptions()
        {
            if (PresetCombo.SelectedItem is ComboBoxItem item
                && item.Tag is string tag
                && double.TryParse(tag, out double preset)
                && preset > 0)
            {
                return new ManualCleanupOptions(
                    ManualCleanupKind.BySpace,
                    DateTime.Now,
                    (long)(preset * 1024L * 1024L * 1024L));
            }

            double gb = SpaceInput.Value ?? 0;
            if (gb <= 0)
                throw new InvalidOperationException("请输入大于 0 的 GB 数值");
            return new ManualCleanupOptions(
                ManualCleanupKind.BySpace,
                DateTime.Now,
                (long)(gb * 1024L * 1024L * 1024L));
        }
    }
}
