using Microsoft.Win32;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// 选择录像保存文件夹：支持本地任意文件夹与 UNC 网络路径（\\NAS\共享）。
    /// </summary>
    public sealed partial class StoragePathSelectionDialog : Window
    {
        public string? SelectedPath { get; private set; }

        public StoragePathSelectionDialog(
            string? initialPath = null,
            string? title = null,
            string? hint = null)
        {
            InitializeComponent();
            Title = title ?? "选择录像保存文件夹";
            DialogTitleText.Text = Title;
            DialogHintText.Text = hint
                ?? "可以选择本地磁盘中的任意文件夹，也可以输入网络共享路径，例如：\\\\192.168.1.100\\共享目录\\快递打包视频";
            BrowseButton.Content = "浏览…";
            OkButton.Content = "确定";
            CancelButton.Content = "取消";
            PathTextBox.Text = initialPath?.Trim() ?? "";
            OkButton.IsEnabled = !string.IsNullOrWhiteSpace(PathTextBox.Text);
            PathTextBox.TextChanged += (_, _) =>
                OkButton.IsEnabled = !string.IsNullOrWhiteSpace(PathTextBox.Text);

            Loaded += (_, _) =>
            {
                PathTextBox.Focus();
                PathTextBox.CaretIndex = PathTextBox.Text.Length;
            };
        }

        internal static bool TryNormalizePath(
            string? path,
            out string normalizedPath,
            out string errorMessage)
        {
            try
            {
                string candidate = path?.Trim().Trim('"') ?? "";
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    normalizedPath = "";
                    errorMessage = "请输入文件夹路径";
                    return false;
                }

                if (!Path.IsPathFullyQualified(candidate))
                {
                    normalizedPath = "";
                    errorMessage = @"请输入完整路径，例如 D:\录像 或 \\NAS\共享目录";
                    return false;
                }

                normalizedPath = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(candidate));
                errorMessage = "";
                return true;
            }
            catch (Exception ex)
            {
                normalizedPath = "";
                errorMessage = ex.Message;
                return false;
            }
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var folderDialog = new OpenFolderDialog
            {
                Title = "选择录像保存文件夹",
                InitialDirectory = TryGetExistingInitialDirectory()
            };
            if (folderDialog.ShowDialog(this) == true)
                PathTextBox.Text = folderDialog.FolderName;
        }

        private string TryGetExistingInitialDirectory()
        {
            if (TryNormalizePath(PathTextBox.Text, out string normalized, out _)
                && Directory.Exists(normalized))
            {
                return normalized;
            }
            return "";
        }

        private void OkButton_Click(object? sender, RoutedEventArgs e)
        {
            if (!TryNormalizePath(PathTextBox.Text, out string selectedPath, out string errorMessage))
            {
                AppDialog.Error(this, errorMessage, "路径无效");
                return;
            }

            SelectedPath = selectedPath;
            DialogResult = true;
        }
    }
}
