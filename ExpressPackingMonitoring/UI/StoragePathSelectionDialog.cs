using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// 选择录像保存文件夹：支持本地任意文件夹与 UNC 网络路径（\\NAS\共享）。
    /// </summary>
    public sealed class StoragePathSelectionDialog : Window
    {
        private readonly TextBox _pathTextBox = new();
        private readonly Button _okButton;

        public string? SelectedPath { get; private set; }

        public StoragePathSelectionDialog(
            string? initialPath = null,
            string? title = null,
            string? hint = null)
        {
            Title = title ?? "选择录像保存文件夹";
            Width = 580;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.ToolWindow;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "PanelBackground");
            SetResourceReference(ForegroundProperty, "TextPrimary");

            var root = new StackPanel { Margin = new Thickness(24) };

            var titleText = new TextBlock
            {
                Text = "选择用于保存录像的文件夹",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            root.Children.Add(titleText);

            var hintText = new TextBlock
            {
                Text = hint
                    ?? "可以选择本地磁盘中的任意文件夹，也可以输入网络共享路径，例如：\\\\192.168.1.100\\共享目录\\快递打包视频",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            };
            hintText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            root.Children.Add(hintText);

            var pathGrid = new Grid();
            pathGrid.ColumnDefinitions.Add(new ColumnDefinition());
            pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _pathTextBox.Text = initialPath?.Trim() ?? "";
            _pathTextBox.MinHeight = 36;
            _pathTextBox.VerticalContentAlignment = VerticalAlignment.Center;
            _pathTextBox.Margin = new Thickness(0, 0, 10, 0);
            _pathTextBox.ToolTip = "本地文件夹或 UNC 网络路径";
            pathGrid.Children.Add(_pathTextBox);

            var browseButton = new Button
            {
                Content = "浏览…",
                MinWidth = 88,
                MinHeight = 36
            };
            browseButton.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
            browseButton.Click += BrowseButton_Click;
            Grid.SetColumn(browseButton, 1);
            pathGrid.Children.Add(browseButton);
            root.Children.Add(pathGrid);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };

            _okButton = new Button
            {
                Content = "确定",
                MinWidth = 88,
                Margin = new Thickness(0, 0, 10, 0),
                IsDefault = true,
                IsEnabled = !string.IsNullOrWhiteSpace(_pathTextBox.Text)
            };
            _okButton.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
            _okButton.Click += OkButton_Click;

            var cancelButton = new Button
            {
                Content = "取消",
                MinWidth = 88,
                IsCancel = true
            };
            cancelButton.SetResourceReference(StyleProperty, "SecondaryButtonStyle");

            _pathTextBox.TextChanged += (_, _) =>
                _okButton.IsEnabled = !string.IsNullOrWhiteSpace(_pathTextBox.Text);

            buttons.Children.Add(_okButton);
            buttons.Children.Add(cancelButton);
            root.Children.Add(buttons);
            Content = root;

            Loaded += (_, _) =>
            {
                _pathTextBox.Focus();
                _pathTextBox.CaretIndex = _pathTextBox.Text.Length;
            };
        }

        internal static bool TryNormalizePath(string? path, out string normalizedPath, out string errorMessage)
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

                normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
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
                _pathTextBox.Text = folderDialog.FolderName;
        }

        private string TryGetExistingInitialDirectory()
        {
            if (TryNormalizePath(_pathTextBox.Text, out string normalized, out _)
                && Directory.Exists(normalized))
            {
                return normalized;
            }
            return "";
        }

        private void OkButton_Click(object? sender, RoutedEventArgs e)
        {
            if (!TryNormalizePath(_pathTextBox.Text, out string selectedPath, out string errorMessage))
            {
                AppDialog.Error(this, errorMessage, "路径无效");
                return;
            }

            SelectedPath = selectedPath;
            DialogResult = true;
        }
    }
}
