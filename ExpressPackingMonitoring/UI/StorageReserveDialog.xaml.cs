#nullable disable
using ExpressPackingMonitoring.Config;
using System;
using System.Globalization;
using System.Windows;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// 设置某块磁盘的预留空间。这是给整块磁盘留的安全空间（整个磁盘共享，其他软件占用也算在内），
    /// 不是本软件最多占用多少；只在磁盘右键里打开，设置页不露出这个参数。
    /// </summary>
    public sealed partial class StorageReserveDialog : Window
    {
        private readonly StorageLocation _location;

        /// <summary>用户确认后的预留值（GB）；取消时不用读。</summary>
        public double ReserveGB { get; private set; }

        public StorageReserveDialog(StorageLocation location)
        {
            _location = location ?? throw new ArgumentNullException(nameof(location));
            InitializeComponent();

            ReserveGB = StorageSpacePolicy.GetEffectiveReserveGB(_location);
            TargetPathText.Text = _location.Path;
            ReserveTextBox.Text = Math.Ceiling(ReserveGB).ToString("F0", CultureInfo.InvariantCulture);

            Loaded += (_, _) =>
            {
                ReserveTextBox.Focus();
                ReserveTextBox.SelectAll();
            };
        }

        /// <summary>打开预留输入框并写回配置；返回是否改过，调用方据此刷新列表。</summary>
        public static bool TryEdit(Window owner, StorageLocation location)
        {
            var dialog = new StorageReserveDialog(location) { Owner = owner };
            if (dialog.ShowDialog() != true) return false;

            StorageCapacityPolicy.TryApplyReserveGB(location, dialog.ReserveGB);
            return true;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(
                    ReserveTextBox.Text?.Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double reserveGB)
                || !double.IsFinite(reserveGB)
                || reserveGB <= 0)
            {
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            ReserveGB = reserveGB;
            DialogResult = true;
        }
    }
}
