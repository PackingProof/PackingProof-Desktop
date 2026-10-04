using System.Windows;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// 「面单放大」栏的接线：调整放大位置的入口与完成后回到设置页的定位。
    /// 单独放一个分部文件，避免往冻结规模的历史例外 SettingsWindow.xaml.cs 里加行数。
    /// </summary>
    public partial class SettingsWindow
    {
        /// <summary>宿主有没有主画面（打印工位没有）：没有就不显示"调整放大位置"入口。</summary>
        public bool CanAdjustZoomGuide => Context?.RequestZoomGuideEdit != null;

        /// <summary>设置页重开时定位到"面单放大"那一栏。</summary>
        public void SelectZoomTab()
        {
            // 只看能力：这一栏本身是按 CanRecordPcVideo 显隐的，
            // 读元素 Visibility 会受绑定求值时机影响。
            if (Capabilities.CanRecordPcVideo)
                SettingsTabControl.SelectedItem = ZoomTabItem;
        }

        /// <summary>
        /// 点"调整放大位置"：先把当前改动应用掉，再关掉设置窗口回主画面框选。
        /// 设置窗口是模态的（藏着它主窗口仍是禁用状态），必须真的关掉才能去主画面操作；
        /// 框选完成或按 Esc 后主窗口会自动把设置页重新打开。
        /// </summary>
        private async void BtnAdjustZoomGuide_Click(object sender, RoutedEventArgs e)
        {
            if (!await SaveAndApplyAsync())
                return;

            Context?.RequestZoomGuideEdit?.Invoke();
            Close();
        }
    }
}
