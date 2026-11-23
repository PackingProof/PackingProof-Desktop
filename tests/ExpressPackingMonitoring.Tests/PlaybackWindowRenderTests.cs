using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
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
        WpfTestHost.Run(() =>
        {
            action();
        }, TimeSpan.FromSeconds(60), "STA 线程执行超时");
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
    public void MediaTabs_DefaultToAllAndFallBackToVideoWithoutPhotoStore()
    {
        RunOnStaThread(() =>
        {
            var videoOnlyWindow = new PlaybackWindow(
                Path.GetTempPath(),
                null,
                true,
                "本机");
            var allWindow = new PlaybackWindow(
                Path.GetTempPath(),
                null,
                true,
                "本机",
                photoStore: new ExpressPackingMonitoring.Data.PhotoStore(
                    Path.Combine(
                        Path.GetTempPath(),
                        "packingproof-media-tabs-" + Guid.NewGuid().ToString("N") + ".db")));
            var importableWindow = new PlaybackWindow(
                Path.GetTempPath(),
                null,
                true,
                videoImportService: new VideoFolderImportService(
                    new VideoDatabase(":memory:"),
                    [Path.GetTempPath()],
                    sourceDeviceId: "test-device",
                    sourceDeviceName: "测试设备"),
                photoStore: new PhotoStore(
                    Path.Combine(
                        Path.GetTempPath(),
                        "packingproof-media-import-" + Guid.NewGuid().ToString("N") + ".db")));
            try
            {
                Button videoOnlyAll = Assert.IsType<Button>(
                    videoOnlyWindow.FindName("AllMediaTabButton"));
                Button videoOnlyVideo = Assert.IsType<Button>(
                    videoOnlyWindow.FindName("VideoMediaTabButton"));
                Assert.Equal(Visibility.Collapsed, videoOnlyAll.Visibility);
                Assert.Equal(
                    videoOnlyWindow.FindResource("AccentBlue"),
                    videoOnlyVideo.Background);

                Button all = Assert.IsType<Button>(
                    allWindow.FindName("AllMediaTabButton"));
                Assert.Equal(Visibility.Visible, all.Visibility);
                Assert.Equal(allWindow.FindResource("AccentBlue"), all.Background);

                Button photo = Assert.IsType<Button>(
                    importableWindow.FindName("PhotoMediaTabButton"));
                Button allFromPhoto = Assert.IsType<Button>(
                    importableWindow.FindName("AllMediaTabButton"));
                Button import = Assert.IsType<Button>(
                    importableWindow.FindName("BtnImportVideos"));
                Assert.Equal(Visibility.Visible, import.Visibility);
                photo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Collapsed, import.Visibility);
                allFromPhoto.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Visible, import.Visibility);
            }
            finally
            {
                videoOnlyWindow.Close();
                allWindow.Close();
                importableWindow.Close();
            }
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
    public void PhotoPreviewSupportsWheelZoomAndLeftButtonPanning()
    {
        RunOnStaThread(() =>
        {
            string photoDbPath = Path.Combine(
                Path.GetTempPath(),
                "packingproof-photo-preview-" + Guid.NewGuid().ToString("N") + ".db");
            var window = new PlaybackWindow(
                Path.GetTempPath(),
                null,
                true,
                "本机",
                photoStore: new PhotoStore(photoDbPath));
            BitmapSource preview = BitmapSource.Create(
                800,
                600,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                new byte[800 * 600 * 4],
                800 * 4);
            preview.Freeze();
            window.ApplyMixedLibraryPreviewForTest(
                null,
                [
                    new PhotoRecord(
                        1,
                        "SF-PREVIEW",
                        "发货",
                        Path.Combine(Path.GetTempPath(), "preview.jpg"),
                        DateTimeOffset.Now,
                        "device-1",
                        "电脑1",
                        800,
                        600,
                        1024,
                        false,
                        "",
                        "",
                        "Completed")
                ],
                preview);

            var viewport = Assert.IsType<Grid>(
                window.FindName("PhotoPreviewViewport"));
            Assert.Equal(Visibility.Visible, viewport.Visibility);
            viewport.Measure(new Size(800, 600));
            viewport.Arrange(new Rect(0, 0, 800, 600));
            viewport.UpdateLayout();
            window.ZoomPhotoPreviewForTest(120, 800, 400);
            Assert.True(window.PhotoPreviewScaleForTest > 1);

            window.PanPhotoPreviewForTest(60, 40);
            Vector translated = window.PhotoPreviewTranslationForTest;
            Assert.NotEqual(0, translated.X);
            Assert.NotEqual(0, translated.Y);

            for (int index = 0; index < 20; index++)
                window.ZoomPhotoPreviewForTest(-120, 800, 400);
            Assert.Equal(1, window.PhotoPreviewScaleForTest, precision: 3);
            Assert.Equal(new Vector(0, 0), window.PhotoPreviewTranslationForTest);
            window.Content = null;
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
