using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using System.Windows;

namespace ExpressPackingMonitoring.UI
{
    public partial class SettingsWindow
    {
        private bool _recognitionSamplePackageRunning;

        private void ExportRecognitionSamples_Click(object sender, RoutedEventArgs e)
        {
            if (_recognitionSamplePackageRunning)
                return;

            bool confirmed = AppDialog.Confirm(
                this,
                AppLanguage.Get("将只读取本机录像并抽取少量画面，生成 ZIP 样本包。包内不含录像、数据库、配置或日志，但画面可能包含面单和人物信息，请确认后再发送"),
                AppLanguage.Get("导出样本包"),
                AppDialogSeverity.Warning,
                confirmText: AppLanguage.Get("开始抓取"),
                cancelText: AppLanguage.Get("取消"),
                isDangerous: false);
            if (!confirmed)
                return;

            _recognitionSamplePackageRunning = true;
            ExportRecognitionSamplesButton.IsEnabled = false;
            ExportRecognitionSamplesButton.Content = AppLanguage.Get("正在生成样本包");
            try
            {
                RecognitionSampleExportWorkflow.Run(
                    this,
                    AppPaths.UserDataDir,
                    Config.NodeId);
            }
            finally
            {
                _recognitionSamplePackageRunning = false;
                ExportRecognitionSamplesButton.IsEnabled = true;
                ExportRecognitionSamplesButton.Content = AppLanguage.Get("导出样本包");
            }
        }
    }
}
