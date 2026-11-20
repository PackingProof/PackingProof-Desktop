using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.UI;

public partial class TransferManagementWindow : Window
{
    private readonly TransferManagementWindowViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;
    private int _refreshErrorLogged;

    internal TransferManagementWindow(
        TransferManagementSnapshotService snapshotService,
        Func<bool>? isUploadPaused = null,
        Func<Task<bool>>? toggleUploadPause = null)
    {
        InitializeComponent();
        DialogWindowChrome.UseCloseButtonOnly(this);
        _viewModel = new TransferManagementWindowViewModel(
            snapshotService,
            isUploadPaused,
            toggleUploadPause);
        DataContext = _viewModel;
        _refreshTimer = new DispatcherTimer(
            DispatcherPriority.Background,
            Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        Loaded += TransferManagementWindow_Loaded;
        Closed += TransferManagementWindow_Closed;
    }

    private async void TransferManagementWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        _refreshTimer.Start();
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e) =>
        await RefreshAsync();

    private async void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: HostDeviceItem item }
            || sender is not Button button)
        {
            return;
        }

        bool confirmed = AppDialog.Confirm(
            this,
            AppLanguage.Format(
                "只会移除设备登记和昵称，不会删除录像文件。确定移除“{0}”吗？",
                item.DisplayName),
            AppLanguage.Get("解绑从机"),
            AppDialogSeverity.Warning,
            confirmText: AppLanguage.Get("解绑"),
            cancelText: AppLanguage.Get("取消"),
            isDangerous: true);
        if (!confirmed)
            return;

        button.IsEnabled = false;
        try
        {
            BackupDeviceRemovalResult result =
                await _viewModel.ForgetDeviceAsync(item);
            if (result.Status == BackupDeviceRemovalStatus.Removed)
                return;

            AppDialog.Warning(
                this,
                DescribeRemovalFailure(result),
                AppLanguage.Get("无法解绑从机"));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(
                "TransferManagement",
                $"Removing known backup device failed: {ex.Message}");
            AppDialog.Error(
                this,
                AppLanguage.Get("设备移除失败，请稍后重试"),
                AppLanguage.Get("无法解绑从机"));
        }
        finally
        {
            if (button.IsLoaded)
                button.IsEnabled = true;
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            await _viewModel.RefreshAsync();
            Interlocked.Exchange(ref _refreshErrorLogged, 0);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _refreshErrorLogged, 1) == 0)
            {
                RuntimeLog.Warn(
                    "TransferManagement",
                    $"Transfer management refresh failed: {ex.Message}");
            }
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        Close();
    }

    private void TransferManagementWindow_Closed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimer_Tick;
        _viewModel.Dispose();
    }

    private static string DescribeRemovalFailure(
        BackupDeviceRemovalResult result) =>
        result.Status switch
        {
            BackupDeviceRemovalStatus.Failed =>
                AppLanguage.Get("设备移除失败，请稍后重试"),
            _ => AppLanguage.Get("设备不存在或已经解绑")
        };
}
