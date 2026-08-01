using ExpressPackingMonitoring.Services;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace ExpressPackingMonitoring.UI;

public partial class VideoImportDialog : Window
{
    private readonly VideoFolderImportService _importService;
    private readonly string _initialFolder;
    private readonly string _managedRoot;
    private CancellationTokenSource? _importCancellation;
    private bool _allowClose;

    internal VideoImportDialog(
        VideoFolderImportService importService,
        string initialFolder,
        string managedRoot)
    {
        InitializeComponent();
        _importService = importService;
        _initialFolder = initialFolder;
        _managedRoot = managedRoot;
        Closing += VideoImportDialog_Closing;
    }

    internal VideoImportResult? ImportResult { get; private set; }

    internal string SelectedFolder { get; private set; } = "";

    private void ImportShippingButton_Click(object sender, RoutedEventArgs e) =>
        StartImportAsync("发货");

    private void ImportReturnButton_Click(object sender, RoutedEventArgs e) =>
        StartImportAsync("退货");

    private async void StartImportAsync(string mode)
    {
        if (_importCancellation != null)
            return;

        var folderDialog = new OpenFolderDialog
        {
            Title = $"选择要导入的{mode}视频文件夹",
            InitialDirectory = Directory.Exists(_initialFolder) ? _initialFolder : _managedRoot
        };
        if (folderDialog.ShowDialog(this) != true)
            return;

        string selectedFolder = folderDialog.FolderName;
        if (!_importService.IsFolderManaged(selectedFolder))
        {
            bool openFolder = AppDialog.Confirm(
                this,
                "请先把视频放到录像文件夹，再回来导入\n\n导入后请不要自行移动或删除，程序会负责回放、备份和空间管理",
                "只能导入录像文件夹内的视频",
                "打开录像文件夹",
                "返回",
                AppDialogSeverity.Information,
                isDangerous: false);
            if (openFolder)
                OpenManagedFolder();
            return;
        }

        SelectedFolder = selectedFolder;
        _importCancellation = new CancellationTokenSource();
        ShowProgress(mode);
        var progress = new Progress<VideoImportProgress>(value =>
        {
            ImportProgressBar.Value = value.Total == 0
                ? 0
                : value.Processed * 100d / value.Total;
            ImportProgressSummary.Text =
                $"已处理 {value.Processed}/{value.Total} · 成功 {value.Imported} · 跳过 {value.Skipped}";
            ImportCurrentFile.Text = value.CurrentFile;
        });

        try
        {
            ImportResult = await _importService.ImportAsync(
                selectedFolder,
                mode,
                progress,
                _importCancellation.Token);
            _allowClose = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            AppDialog.ShowMessage(this, ex.Message, "导入视频失败", AppDialogSeverity.Warning);
            ShowInstructions();
        }
        finally
        {
            _importCancellation?.Dispose();
            _importCancellation = null;
        }
    }

    private void ShowProgress(string mode)
    {
        DialogTitleText.Text = $"正在导入{mode}录像";
        ImportInstructions.Visibility = Visibility.Collapsed;
        ImportProgressPanel.Visibility = Visibility.Visible;
        ImportShippingButton.Visibility = Visibility.Collapsed;
        ImportReturnButton.Visibility = Visibility.Collapsed;
        CancelButton.Content = "取消导入";
        ImportProgressBar.Value = 0;
        ImportProgressSummary.Text = "正在查找 MP4 视频...";
        ImportCurrentFile.Text = "正在准备...";
    }

    private void ShowInstructions()
    {
        DialogTitleText.Text = "导入录像";
        ImportInstructions.Visibility = Visibility.Visible;
        ImportProgressPanel.Visibility = Visibility.Collapsed;
        ImportShippingButton.Visibility = Visibility.Visible;
        ImportReturnButton.Visibility = Visibility.Visible;
        CancelButton.Content = "取消";
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_importCancellation == null)
        {
            _allowClose = true;
            DialogResult = false;
            return;
        }

        CancelButton.IsEnabled = false;
        CancelButton.Content = "正在停止...";
        _importCancellation.Cancel();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        CancelButton_Click(sender, e);
    }

    private void VideoImportDialog_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _importCancellation == null)
            return;

        e.Cancel = true;
        CancelButton_Click(this, new RoutedEventArgs());
    }

    private void OpenManagedFolder()
    {
        try
        {
            Directory.CreateDirectory(_managedRoot);
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(_managedRoot);
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            AppDialog.ShowMessage(this, $"无法打开录像文件夹：{ex.Message}", "打开失败", AppDialogSeverity.Warning);
        }
    }
}
