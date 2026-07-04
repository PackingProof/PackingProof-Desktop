using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ExpressPackingMonitoring.UI
{
    public sealed class DriveSelectionDialog : Window
    {
        private readonly ListBox _driveList = new();

        public string? SelectedRootPath { get; private set; }

        public DriveSelectionDialog(IEnumerable<string>? usedPaths = null)
        {
            Title = "选择磁盘分区";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.ToolWindow;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "PanelBackground");
            SetResourceReference(ForegroundProperty, "TextPrimary");

            var root = new Grid { Margin = new Thickness(24) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "选择用于保存录像的磁盘",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            root.Children.Add(title);

            var hint = new TextBlock
            {
                Text = "程序会自动使用所选磁盘下的“快递打包视频”目录。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            Grid.SetRow(hint, 1);
            root.Children.Add(hint);

            var content = new StackPanel();
            _driveList.MinHeight = 120;
            _driveList.MaxHeight = 260;
            var drives = LoadDrives(usedPaths);
            _driveList.DisplayMemberPath = nameof(DriveOption.DisplayText);
            _driveList.ItemContainerStyle = CreateDriveItemContainerStyle();
            _driveList.ItemsSource = drives;
            _driveList.SelectedItem = drives.FirstOrDefault(drive => !drive.IsUsed);
            content.Children.Add(_driveList);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };

            var ok = new Button
            {
                Content = "确定",
                MinWidth = 88,
                Margin = new Thickness(0, 0, 10, 0),
                IsEnabled = _driveList.SelectedItem is DriveOption option && !option.IsUsed
            };
            ok.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
            ok.Click += (_, _) =>
            {
                if (_driveList.SelectedItem is not DriveOption selected || selected.IsUsed) return;
                SelectedRootPath = selected.RootPath;
                DialogResult = true;
                Close();
            };
            _driveList.SelectionChanged += (_, _) =>
            {
                ok.IsEnabled = _driveList.SelectedItem is DriveOption option && !option.IsUsed;
            };

            var cancel = new Button
            {
                Content = "取消",
                MinWidth = 88
            };
            cancel.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
            cancel.Click += (_, _) =>
            {
                DialogResult = false;
                Close();
            };

            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            content.Children.Add(buttons);

            Grid.SetRow(content, 2);
            root.Children.Add(content);

            Content = root;
        }

        private static Style CreateDriveItemContainerStyle()
        {
            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(UIElement.IsEnabledProperty, new System.Windows.Data.Binding(nameof(DriveOption.IsSelectable))));
            return style;
        }

        private static List<DriveOption> LoadDrives(IEnumerable<string>? usedPaths)
        {
            var usedRoots = new HashSet<string>(
                (usedPaths ?? Enumerable.Empty<string>())
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(GetDriveRoot),
                StringComparer.OrdinalIgnoreCase);

            return DriveInfo.GetDrives()
                .Where(drive => drive.IsReady)
                .Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable)
                .Select(drive => new DriveOption(drive, usedRoots.Contains(GetDriveRoot(drive.RootDirectory.FullName))))
                .OrderBy(option => option.RootPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string GetDriveRoot(string path)
        {
            try
            {
                string fullPath = Path.GetFullPath(path.Trim());
                return Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? fullPath;
            }
            catch
            {
                return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        private sealed class DriveOption
        {
            public string RootPath { get; }
            public string DisplayText { get; }
            public bool IsUsed { get; }
            public bool IsSelectable => !IsUsed;

            public DriveOption(DriveInfo drive, bool isUsed)
            {
                RootPath = drive.RootDirectory.FullName;
                IsUsed = isUsed;
                string name = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "本地磁盘" : drive.VolumeLabel;
                double freeGb = drive.AvailableFreeSpace / 1073741824.0;
                double totalGb = drive.TotalSize / 1073741824.0;
                string status = isUsed ? "  使用中" : "";
                DisplayText = $"{drive.Name} {name}  可用 {freeGb:F1} / {totalGb:F1} GB{status}";
            }
        }
    }
}
