using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class PlaybackWindowTests
{
    [Fact]
    public void PlaybackLayout_UsesTwoRowPaginationAndNamedPlayerHost()
    {
        string xaml = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring", "UI", "PlaybackWindow.xaml"));

        Assert.Contains("x:Name=\"PageStatusText\"", xaml);
        Assert.Contains("<Grid Grid.Row=\"1\">", xaml);
        Assert.Contains("x:Name=\"BtnPreviousPage\" Grid.Column=\"0\"", xaml);
        Assert.Contains("x:Name=\"BtnNextPage\" Grid.Column=\"2\"", xaml);
        Assert.Contains("x:Name=\"PlayerHost\"", xaml);
        Assert.Contains("Width=\"1100\" MinHeight=\"560\" MinWidth=\"920\"", xaml);
        Assert.Contains("x:Name=\"VideoArea\"", xaml);
        Assert.Contains("x:Name=\"PlayerView\"", xaml);
        Assert.Contains(
            "x:Name=\"VideoArea\" Grid.Row=\"0\" Background=\"{DynamicResource VideoSurfaceBackground}\"",
            xaml);
        Assert.Contains("Background=\"{DynamicResource VideoSurfaceBackground}\"", xaml);
        Assert.DoesNotContain(
            "x:Name=\"VideoArea\" Grid.Row=\"0\" Background=\"{DynamicResource PanelBackground}\"",
            xaml);
        Assert.Contains("x:Name=\"PlaybackCover\"", xaml);
        Assert.Contains("x:Name=\"PlaybackCoverText\"", xaml);
        Assert.Contains("Text=\"请选择录像开始播放\"", xaml);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", xaml);
        Assert.Contains("VerticalAlignment=\"Stretch\"", xaml);
        Assert.DoesNotContain("x:Name=\"VideoFrame\"", xaml);
        Assert.DoesNotContain("VideoArea_SizeChanged", xaml);
        Assert.Contains("x:Name=\"TimelineSlider\" Grid.Column=\"1\" MinWidth=\"120\"", xaml);

        string codeBehind = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring", "UI", "PlaybackWindow.xaml.cs"));
        Assert.DoesNotContain("MediaPlayer.Vout", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ShowPlaybackCover(\"正在准备视频...\")", codeBehind, StringComparison.Ordinal);
        Assert.Contains("RevealPlaybackSurfaceAfterFirstFrame();", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CalculateAdaptiveWindowBounds", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("CalculateAspectFitSize", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackLayout_HasHideUnavailableToggleAndSecondRowHiddenHint()
    {
        string xaml = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring", "UI", "PlaybackWindow.xaml"));

        Assert.Contains("x:Name=\"HideUnavailableButton\"", xaml);
        Assert.Contains("x:Name=\"HideUnavailableButtonText\"", xaml);
        Assert.Contains("x:Name=\"HideUnavailableButtonIcon\"", xaml);
        Assert.Contains("FluentEyeOffIcon", xaml);
        Assert.Contains("Click=\"HideUnavailableButton_Click\"", xaml);
        Assert.Contains("Text=\"显示异常记录\"", xaml);
        Assert.DoesNotContain("HideUnavailableCheckBox", xaml, StringComparison.Ordinal);
        Assert.Contains("<ScrollViewer Grid.Row=\"0\"", xaml);
        Assert.DoesNotContain("仅本次回放生效", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HiddenHintPanel\"", xaml);
        Assert.Contains("x:Name=\"HiddenHintText\"", xaml);

        string icons = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring", "Themes", "FluentIcons.xaml"));
        Assert.Contains("x:Key=\"FluentEyeIcon\"", icons);
        Assert.Contains("x:Key=\"FluentEyeOffIcon\"", icons);

        string codeBehind = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring", "UI", "PlaybackWindow.xaml.cs"));
        Assert.Contains("_hideUnavailable = true", codeBehind, StringComparison.Ordinal);
        Assert.Contains("HideUnavailableButton_Click", codeBehind, StringComparison.Ordinal);
        Assert.Contains("\"FluentEyeOffIcon\"", codeBehind, StringComparison.Ordinal);
        Assert.Contains("\"FluentEyeIcon\"", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("HideUnavailable_Changed", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHiddenHintText_IncludesHiddenCountAndReason()
    {
        Assert.Equal("已隐藏 3 条异常记录（文件丢失或已清理）", PlaybackWindow.BuildHiddenHintText(3));
    }

    [Theory]
    [InlineData(3, 3, false, true)]
    [InlineData(2, 3, false, false)]
    [InlineData(3, 3, true, false)]
    public void IsCurrentLoadRequest_AcceptsOnlyLatestOpenWindowRequest(
        int requestVersion,
        int currentVersion,
        bool isClosing,
        bool expected)
    {
        Assert.Equal(expected, PlaybackWindow.IsCurrentLoadRequest(requestVersion, currentVersion, isClosing));
    }

    [Fact]
    public void GetOrderDisplayName_PrefersTrackingNumber()
    {
        string result = PlaybackWindow.GetOrderDisplayName(
            "YT123456789012",
            "ORDER-OLD",
            "FILE-NAME_20260723_发货.mp4");

        Assert.Equal("YT123456789012", result);
    }

    [Fact]
    public void GetOrderDisplayName_FallsBackToOrderId()
    {
        string result = PlaybackWindow.GetOrderDisplayName(
            "",
            "SF123456789012",
            "FILE-NAME_20260723_发货.mp4");

        Assert.Equal("SF123456789012", result);
    }

    [Theory]
    [InlineData("JD123456789012_20260723_120000_发货.mp4", "JD123456789012")]
    [InlineData("YT123456789012.mkv", "YT123456789012")]
    [InlineData("", "未识别面单")]
    public void GetOrderDisplayName_ExtractsFileSystemFallback(string fileName, string expected)
    {
        Assert.Equal(expected, PlaybackWindow.GetOrderDisplayName("", "", fileName));
    }

    [Theory]
    [InlineData("external", "android-1234567890a1b2c3", "手机1", "来源：手机1")]
    [InlineData("EXTERNAL", "", "", "来源：手机设备")]
    [InlineData("external", "", "一号打包手机", "来源：一号打包手机")]
    [InlineData("pc", "pc-1", "一号电脑", "来源：电脑")]
    [InlineData("", "", "", "来源：电脑")]
    public void GetSourceDisplay_UsesBackupDeviceIdentity(
        string sourceType,
        string sourceDeviceId,
        string sourceDeviceName,
        string expected)
    {
        Assert.Equal(
            expected,
            PlaybackWindow.GetSourceDisplay(sourceType, sourceDeviceId, sourceDeviceName));
    }

    [Theory]
    [InlineData("external", "APP 备份", "")]
    [InlineData("external", "APP备份", "")]
    [InlineData("external", "上传完成", "上传完成")]
    [InlineData("pc", "扫码枪停止", "扫码枪停止")]
    public void GetStopReasonDisplay_HidesDuplicatedBackupLabel(
        string sourceType,
        string stopReason,
        string expected)
    {
        Assert.Equal(expected, PlaybackWindow.GetStopReasonDisplay(sourceType, stopReason));
    }

    [Fact]
    public void FileLocator_SelectsNormalizedFileWithoutOpeningFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"packingproof-locate-{Guid.NewGuid():N}");
        string file = Path.Combine(folder, "video.mp4");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(file, [1]);
        try
        {
            string? selected = null;
            bool opened = false;
            FileLocationResult result = WindowsShellFileLocator.Locate(
                file,
                path => { selected = path; return true; },
                _ => opened = true);

            Assert.Equal(FileLocationResult.Selected, result);
            Assert.Equal(Path.GetFullPath(file), selected);
            Assert.False(opened);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void FileLocator_OpensContainingFolderWhenSelectionFails()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"packingproof-locate-{Guid.NewGuid():N}");
        string file = Path.Combine(folder, "video.mp4");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(file, [1]);
        try
        {
            string? openedFolder = null;
            FileLocationResult result = WindowsShellFileLocator.Locate(
                file,
                _ => false,
                path => openedFolder = path);

            Assert.Equal(FileLocationResult.OpenedFolder, result);
            Assert.Equal(folder, openedFolder);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void CreateVideoItem_ArchivedLocalDeleted_UsesArchivePath()
    {
        var record = new VideoRecord
        {
            FilePath = Path.Combine(Path.GetTempPath(), "packingproof-missing-local.mp4"),
            ArchivePath = @"\\NAS\share\2026-08-11\SF123.mp4",
            ArchiveStatus = VideoArchiveStatus.LocalDeleted,
            ArchiveCompletedAt = DateTime.Now,
            StorageState = "Local",
            FileSizeBytes = 2048,
            TrackingNumber = "SF123"
        };

        VideoItem item = PlaybackWindow.CreateVideoItem(record);

        Assert.False(item.IsMissing);
        Assert.False(item.IsDeleted);
        Assert.Equal(@"\\NAS\share\2026-08-11\SF123.mp4", item.FullPath);
        Assert.True(item.IsArchiveWarning);
        Assert.Equal("已归档（本地副本已清理）", item.StatusText);
        Assert.Null(item.File);
    }

    [Fact]
    public void CreateVideoItem_LocalFileExists_UsesLocalPath()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "packingproof-playback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "local.mp4");
        File.WriteAllBytes(file, new byte[64]);
        try
        {
            var record = new VideoRecord
            {
                FilePath = file,
                ArchiveStatus = VideoArchiveStatus.LocalOnly,
                StorageState = "Local",
                FileSizeBytes = 1
            };

            VideoItem item = PlaybackWindow.CreateVideoItem(record);

            Assert.False(item.IsMissing);
            Assert.Equal(file, item.FullPath);
            Assert.NotNull(item.File);
            Assert.Equal(64L, item.File!.Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CreateVideoItem_NoLocalNoArchive_IsMissing()
    {
        var record = new VideoRecord
        {
            FilePath = Path.Combine(Path.GetTempPath(), "packingproof-missing-never-archived.mp4"),
            ArchivePath = "",
            ArchiveStatus = VideoArchiveStatus.LocalOnly,
            StorageState = "Local"
        };

        VideoItem item = PlaybackWindow.CreateVideoItem(record);

        Assert.True(item.IsMissing);
        Assert.Equal(record.FilePath, item.FullPath);
        Assert.Null(item.File);
    }

    [Fact]
    public void CreateVideoItem_UnverifiedArchiveWithoutLocalCopy_IsMissing()
    {
        var record = new VideoRecord
        {
            FilePath = Path.Combine(Path.GetTempPath(), "packingproof-missing-pending.mp4"),
            ArchivePath = @"\\NAS\share\2026-08-11\SF123.mp4",
            ArchiveStatus = VideoArchiveStatus.Pending,
            StorageState = "Local"
        };

        VideoItem item = PlaybackWindow.CreateVideoItem(record);

        Assert.True(item.IsMissing);
    }

    [Fact]
    public void CreateVideoItem_NasDeletedShowsRollingCleanupStatus()
    {
        string localPath = Path.Combine(
            Path.GetTempPath(),
            "packingproof-nasdeleted-local.mp4");
        File.WriteAllText(localPath, "x");
        try
        {
            var record = new VideoRecord
            {
                FilePath = localPath,
                ArchivePath = @"\\NAS\share\2026-08-11\SF.mp4",
                ArchiveStatus = VideoArchiveStatus.NasDeleted,
                ArchiveCompletedAt = DateTime.Now,
                StorageState = "Local",
                FileSizeBytes = 2048,
                TrackingNumber = "SF"
            };

            VideoItem item = PlaybackWindow.CreateVideoItem(record);

            Assert.False(item.IsMissing);
            Assert.Equal("NAS 副本已循环清理", item.StatusText);
            Assert.Equal(localPath, item.FullPath);
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    [Fact]
    public void PlaybackListUsesSingleCreateVideoItemFactory()
    {
        string codeBehind = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring", "UI", "PlaybackWindow.xaml.cs"));

        Assert.Contains("videos.Add(CreateVideoItem(record));", codeBehind, StringComparison.Ordinal);
        Assert.Contains("VideoItem item = CreateVideoItem(record);", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaybackFileResolver.ResolvePlaybackPath", codeBehind, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        foreach (string startPath in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            DirectoryInfo? directory = new(startPath);
            while (directory != null)
            {
                string candidate = Path.Combine([directory.FullName, .. relativeParts]);
                if (File.Exists(candidate))
                    return candidate;
                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"无法定位仓库文件：{Path.Combine(relativeParts)}");
    }
}
