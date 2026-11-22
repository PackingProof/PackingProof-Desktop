using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExpressPackingMonitoring.UI;
using System.Reflection;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 回放窗口必须能真正构造并走完一次布局。
///
/// 顶栏改成筛选按钮后出现过"打开回放窗口失败"，而编译与纯文本守卫都发现不了：
/// 模板展开、资源键解析、构造函数里访问模板内元素这些错误
/// 只在真正创建窗口的那一刻才抛。
/// </summary>
[Collection("WPF render tests")]
public sealed class PlaybackWindowRenderTests
{
    /// <summary>WPF 控件必须在 STA 线程上创建，测试宿主默认 MTA。</summary>
    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // pack:// 方案要有 Application 实例才注册得上；
                // 测试宿主不是 WPF 程序，这里自己建一个。
                if (Application.Current == null)
                    _ = new Application();
                LoadAppResources();
                action();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "STA 线程执行超时");
        if (failure != null)
        {
            var detail = new System.Text.StringBuilder();
            for (Exception? current = failure; current != null; current = current.InnerException)
                detail.AppendLine($"{current.GetType().Name}: {current.Message}");
            detail.AppendLine(failure.StackTrace);
            throw new Xunit.Sdk.XunitException($"回放窗口构造失败：{detail}");
        }
    }

    /// <summary>与 App.xaml 相同的合并顺序，否则窗口里的资源键解析不到。</summary>
    private static void LoadAppResources()
    {
        string[] files =
        [
            "ColorTokens.xaml", "LightTheme.xaml", "ComboBoxTheme.xaml", "DatePickerTheme.xaml",
            "SpinBoxTheme.xaml", "TextBoxTheme.xaml", "ButtonTheme.xaml", "ScrollBarTheme.xaml",
            "FluentIcons.xaml", "SliderTheme.xaml", "MenuTheme.xaml"
        ];

        var merged = new ResourceDictionary();
        foreach (string file in files)
        {
            merged.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    $"pack://application:,,,/ExpressPackingMonitoring;component/themes/{file.ToLowerInvariant()}",
                    UriKind.Absolute)
            });
        }

        if (Application.Current != null)
            Application.Current.Resources = merged;
    }

    /// <summary>
    /// 没有数据库时也要能打开：来源下拉、筛选角标都不能因为数据库为空而崩。
    /// </summary>
    [Fact]
    public void Constructor_WithoutDatabase_DoesNotThrow()
    {
        RunOnStaThread(() =>
        {
            var window = new PlaybackWindow(Path.GetTempPath(), null, true, "本机");
            window.Measure(new Size(1400, 900));
            window.Arrange(new Rect(0, 0, 1400, 900));
            window.UpdateLayout();
        });
    }

    [Fact]
    public void PreparationStateShowsIndeterminateProgressAndCanBeCleared()
    {
        RunOnStaThread(() =>
        {
            var window = new PlaybackWindow(
                Path.GetTempPath(),
                null,
                true,
                "本机");
            var progress = Assert.IsType<ProgressBar>(
                window.FindName("PlaybackPreparationProgress"));
            var icon = Assert.IsType<System.Windows.Shapes.Path>(
                window.FindName("PlaybackCoverIcon"));
            MethodInfo showPreparing = Assert.IsAssignableFrom<MethodInfo>(
                typeof(PlaybackWindow).GetMethod(
                    "ShowPlaybackPreparing",
                    BindingFlags.Instance | BindingFlags.NonPublic));
            MethodInfo showCover = Assert.IsAssignableFrom<MethodInfo>(
                typeof(PlaybackWindow).GetMethod(
                    "ShowPlaybackCover",
                    BindingFlags.Instance | BindingFlags.NonPublic));
            _ = showPreparing.Invoke(window, ["正在转码，完成后会自动播放"]);
            Assert.Equal(Visibility.Visible, progress.Visibility);
            Assert.True(progress.IsIndeterminate);
            Assert.Equal(Visibility.Collapsed, icon.Visibility);

            _ = showCover.Invoke(window, ["请选择录像开始播放"]);
            Assert.Equal(Visibility.Collapsed, progress.Visibility);
            Assert.Equal(Visibility.Visible, icon.Visibility);
        });
    }

    [Fact]
    public void LongOrderNameUsesFloatingMoreButtonWithoutReservingLayout()
    {
        RunOnStaThread(() =>
        {
            var video = new VideoItem
            {
                DisplayName = "LONG-ORDER-" + new string('8', 72)
            };
            var window = new PlaybackWindow(Path.GetTempPath(), null, true, "本机");
            try
            {
                var list = Assert.IsType<ListView>(window.FindName("VideoList"));
                var row = Assert.IsAssignableFrom<FrameworkElement>(list.ItemTemplate.LoadContent());
                row.DataContext = video;
                row.Measure(new Size(600, double.PositiveInfinity));
                row.Arrange(new Rect(0, 0, 600, row.DesiredSize.Height));
                row.UpdateLayout();

                TextBlock orderText = Assert.Single(
                    FindVisualDescendants<TextBlock>(row),
                    text => string.Equals(text.Text, video.DisplayName, StringComparison.Ordinal));
                Button moreButton = Assert.Single(
                    FindVisualDescendants<Button>(row),
                    button => string.Equals(button.ToolTip as string, "更多", StringComparison.Ordinal));

                Rect textBounds = orderText
                    .TransformToAncestor(row)
                    .TransformBounds(new Rect(new Point(0, 0), orderText.RenderSize));

                Assert.Equal(TextWrapping.NoWrap, orderText.TextWrapping);
                Assert.Equal(TextTrimming.None, orderText.TextTrimming);
                Assert.True(textBounds.Right <= row.ActualWidth + 1);
                Assert.True(
                    textBounds.Right > row.ActualWidth - 108,
                    "长单号应当能够覆盖右侧时长和大小区域");
                Assert.Equal(0d, moreButton.Opacity);
                Assert.False(moreButton.IsHitTestVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ScreenshotThumbnail_DecodesARealPng()
    {
        RunOnStaThread(() =>
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                "packingproof-screenshot-thumbnail-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                byte[] pixels = new byte[640 * 360 * 4];
                BitmapSource source = BitmapSource.Create(
                    640,
                    360,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null,
                    pixels,
                    640 * 4);
                source.Freeze();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using (FileStream stream = File.Create(path))
                    encoder.Save(stream);

                MethodInfo loadThumbnail = Assert.IsAssignableFrom<MethodInfo>(
                    typeof(PlaybackWindow).GetMethod(
                        "LoadScreenshotThumbnail",
                        BindingFlags.Static | BindingFlags.NonPublic));
                var thumbnail = Assert.IsAssignableFrom<BitmapSource>(
                    loadThumbnail.Invoke(null, [path]));
                Assert.Equal(440, thumbnail.PixelWidth);
                Assert.True(thumbnail.IsFrozen);
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        });
    }

    [Fact]
    public void ScreenshotDialog_PortraitSizesPreviewToTheImage()
    {
        RunOnStaThread(() =>
        {
            BitmapSource preview = BitmapSource.Create(
                360,
                640,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                new byte[360 * 640 * 4],
                360 * 4);
            preview.Freeze();

            var dialog = new PlaybackScreenshotDialog(preview);
            Assert.Equal(380, dialog.Width);
            Assert.Equal(135, dialog.PreviewFrame.Width);
            Assert.Equal(240, dialog.PreviewFrame.Height);
            Assert.Equal(135, dialog.PreviewImage.Width);
            Assert.Equal(240, dialog.PreviewImage.Height);
        });
    }

    private static T FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject =>
        FindVisualDescendants<T>(root).First();

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (T descendant in FindVisualDescendants<T>(child))
                yield return descendant;
        }
    }
}
