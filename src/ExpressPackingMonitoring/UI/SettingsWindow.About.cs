using System.Windows;
using ExpressPackingMonitoring.UpdateCore;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// 「关于」页的隐私政策入口。
    /// 单独放一个分部文件，避免往冻结规模的历史例外 SettingsWindow.xaml.cs 里加行数。
    /// </summary>
    public partial class SettingsWindow
    {
        private void OpenPrivacyPolicy_Click(object sender, RoutedEventArgs e)
        {
            OpenExternalUrl(BrandMetadata.PrivacyPolicyUrl);
        }
    }
}
