using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace ExpressPackingMonitoring.UI;

internal enum RecognitionSampleExportOutcome
{
    None,
    Success,
    Cancelled,
    Failed
}

public partial class RecognitionSampleExportProgressDialog : Window
{
    private static readonly TimeSpan MinimumVisibleDuration = TimeSpan.FromMilliseconds(450);
    private readonly string _userDataDir;
    private readonly string _contributorNodeId;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly DispatcherTimer _elapsedTimer;
    private readonly Stopwatch _elapsed = new();
    private bool _started;
    private bool _allowClose;
    private bool _cancellationRequested;

    internal RecognitionSampleExportProgressDialog(
        string userDataDir,
        string contributorNodeId)
    {
        InitializeComponent();
        _userDataDir = userDataDir;
        _contributorNodeId = contributorNodeId;
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) =>
            ElapsedTimeText.Text = AppLanguage.Format(
                "已用时 {0} 秒",
                (int)_elapsed.Elapsed.TotalSeconds);
    }

    internal RecognitionSampleExportOutcome Outcome { get; private set; }
    internal RecordingSamplePackageResult? Result { get; private set; }
    internal string FailureMessage { get; private set; } = "";

    private async void Window_ContentRendered(object? sender, EventArgs e)
    {
        if (_started)
            return;
        _started = true;
        _elapsed.Start();
        _elapsedTimer.Start();
        var progress = new Progress<RecordingSamplePackageProgress>(UpdateProgress);
        try
        {
            Result = await Task.Run(() =>
            {
                var service = new RecordingSamplePackageService(
                    _userDataDir,
                    _contributorNodeId);
                return service.CreatePackage(progress, _cancellation.Token);
            });
            Outcome = RecognitionSampleExportOutcome.Success;
        }
        catch (OperationCanceledException)
        {
            Outcome = RecognitionSampleExportOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            Outcome = RecognitionSampleExportOutcome.Failed;
            FailureMessage = ex.Message;
            RuntimeLog.Error("RecognitionSamples", "生成面单识别样本包失败", ex);
        }
        finally
        {
            if (Outcome == RecognitionSampleExportOutcome.Success)
            {
                TimeSpan remaining = MinimumVisibleDuration - _elapsed.Elapsed;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining);
            }

            _elapsedTimer.Stop();
            _elapsed.Stop();
            _cancellation.Dispose();
            _allowClose = true;
            Close();
        }
    }

    private void UpdateProgress(RecordingSamplePackageProgress progress)
    {
        if (_cancellationRequested)
            return;

        ExportProgressBar.IsIndeterminate = progress.Stage != RecordingSamplePackageStage.Capturing;
        if (!ExportProgressBar.IsIndeterminate)
        {
            ExportProgressBar.TargetValue = Math.Clamp(
                progress.Captured * 100d / Math.Max(1, progress.Target),
                0,
                100);
        }

        ProgressSummaryText.Text = progress.Stage switch
        {
            RecordingSamplePackageStage.Preparing => AppLanguage.Get("正在准备样本"),
            RecordingSamplePackageStage.Packaging => AppLanguage.Get("正在压缩样本"),
            _ => AppLanguage.Format(
                "正在抓取样本 {0}/{1}",
                progress.Captured,
                progress.Target)
        };
    }

    private void CancelExportButton_Click(object sender, RoutedEventArgs e) => RequestCancellation();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        RequestCancellation();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;
        e.Cancel = true;
        RequestCancellation();
    }

    private void RequestCancellation()
    {
        if (_cancellationRequested)
            return;

        _cancellationRequested = true;
        CancelExportButton.IsEnabled = false;
        CancelExportButton.Content = AppLanguage.Get("正在取消");
        ProgressSummaryText.Text = AppLanguage.Get("正在取消");
        ExportProgressBar.IsIndeterminate = true;
        _cancellation.Cancel();
    }
}
