using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace ExpressPackingMonitoring.UI;

internal enum OrderNumberExportOutcome
{
    None,
    Success,
    Empty,
    Cancelled,
    Failed
}

public partial class OrderNumberExportProgressDialog : Window
{
    private static readonly TimeSpan MinimumVisibleDuration = TimeSpan.FromMilliseconds(450);
    private readonly VideoDatabase _database;
    private readonly DateTime? _startDate;
    private readonly DateTime? _endDate;
    private readonly string _targetPath;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly DispatcherTimer _elapsedTimer;
    private readonly Stopwatch _elapsed = new();
    private bool _started;
    private bool _allowClose;
    private bool _cancellationRequested;

    internal OrderNumberExportProgressDialog(
        VideoDatabase database,
        DateTime? startDate,
        DateTime? endDate,
        string targetPath)
    {
        InitializeComponent();
        _database = database;
        _startDate = startDate;
        _endDate = endDate;
        _targetPath = targetPath;
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) =>
            ElapsedTimeText.Text = $"已用时 {(int)_elapsed.Elapsed.TotalSeconds} 秒";
    }

    internal OrderNumberExportOutcome Outcome { get; private set; }
    internal int ExportedCount { get; private set; }
    internal string FailureMessage { get; private set; } = "";

    private async void Window_ContentRendered(object? sender, EventArgs e)
    {
        if (_started)
            return;
        _started = true;
        _elapsed.Start();
        _elapsedTimer.Start();
        var progress = new Progress<OrderNumberExportProgress>(UpdateProgress);

        try
        {
            ExportedCount = await Task.Run(() => RunExport(progress, _cancellation.Token));
            Outcome = ExportedCount == 0
                ? OrderNumberExportOutcome.Empty
                : OrderNumberExportOutcome.Success;
        }
        catch (OperationCanceledException)
        {
            Outcome = OrderNumberExportOutcome.Cancelled;
            RuntimeLog.Info("OrderExport", $"用户取消导出，已用时 {_elapsed.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Outcome = OrderNumberExportOutcome.Failed;
            FailureMessage = ex.Message;
            RuntimeLog.Error("OrderExport", "导出单号失败", ex);
        }
        finally
        {
            if (Outcome is OrderNumberExportOutcome.Success or OrderNumberExportOutcome.Empty)
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

    private int RunExport(
        IProgress<OrderNumberExportProgress> progress,
        CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        var stage = Stopwatch.StartNew();
        List<OrderNumberExportSource> sources = _database.QueryOrderNumberExportSources(
            _startDate,
            _endDate,
            cancellationToken,
            progress);
        RuntimeLog.Info("OrderExport", $"读取录像记录 {sources.Count} 条，耗时 {stage.ElapsedMilliseconds}ms");
        if (sources.Count == 0)
            return 0;

        stage.Restart();
        IReadOnlyList<OrderNumberExportRow> rows = OrderNumberExportService.BuildRows(
            sources,
            cancellationToken,
            progress);
        RuntimeLog.Info("OrderExport", $"整理单号记录 {rows.Count} 条，耗时 {stage.ElapsedMilliseconds}ms");
        if (rows.Count == 0)
            return 0;

        stage.Restart();
        OrderNumberExportService.Export(_targetPath, rows, cancellationToken, progress);
        RuntimeLog.Info("OrderExport", $"生成 Excel {rows.Count} 条，耗时 {stage.ElapsedMilliseconds}ms");
        RuntimeLog.Info("OrderExport", $"导出完成，总耗时 {total.ElapsedMilliseconds}ms");
        return rows.Count;
    }

    private void UpdateProgress(OrderNumberExportProgress value)
    {
        if (_cancellationRequested)
            return;

        ProgressSummaryText.Text = value.Message;
        ExportProgressBar.IsIndeterminate = value.IsIndeterminate || value.Total <= 0;
        if (!ExportProgressBar.IsIndeterminate)
        {
            ExportProgressBar.Value = Math.Clamp(
                value.Processed * 100d / value.Total,
                0,
                100);
        }

        ProgressDetailText.Text = value.Total > 0
            ? $"已处理 {Math.Min(value.Processed, value.Total):N0} / {value.Total:N0} 条"
            : "正在准备数据";
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
        CancelExportButton.Content = "正在取消";
        ProgressSummaryText.Text = "正在取消导出";
        ProgressDetailText.Text = "正在清理临时文件，请稍候";
        ExportProgressBar.IsIndeterminate = true;
        _cancellation.Cancel();
    }
}
