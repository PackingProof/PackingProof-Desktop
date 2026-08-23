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
    public void PlaybackLayout_HasOrderExportButtonAndNoHiddenHint()
    {
        string xaml = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring", "UI", "PlaybackWindow.xaml"));

        Assert.Contains("x:Name=\"ExportOrderNumbersButton\"", xaml);
        Assert.Contains("x:Name=\"ExportOrderNumbersButtonText\"", xaml);
        Assert.Contains("x:Name=\"ExportOrderNumbersButtonIcon\"", xaml);
        Assert.Contains("Data=\"{StaticResource FluentSaveIcon}\"", xaml);
        Assert.Contains("Click=\"ExportOrderNumbersButton_Click\"", xaml);
        Assert.Contains("Text=\"导出单号\"", xaml);
        Assert.DoesNotContain("异常记录", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("HideUnavailableCheckBox", xaml, StringComparison.Ordinal);
        Assert.Contains("<ScrollViewer Grid.Row=\"0\"", xaml);
        Assert.DoesNotContain("仅本次回放生效", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"HiddenHintPanel\"", xaml);
        Assert.DoesNotContain("x:Name=\"HiddenHintText\"", xaml);

        string codeBehind = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring", "UI", "PlaybackWindow.xaml.cs"));
        Assert.Contains("_excludeUnavailableRecords = !showDeletedVideos", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("HideUnavailableButton", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("异常记录", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("HideUnavailable_Changed", codeBehind, StringComparison.Ordinal);
        Assert.Contains("LocateExportedOrderFile(saveDialog.FileName);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("WindowsShellFileLocator.Locate(filePath)", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderNumberExportProgressDialog_ReusesExistingDialogStyleAndProgressControls()
    {
        string xaml = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring", "UI", "OrderNumberExportProgressDialog.xaml"));

        Assert.Contains("Width=\"520\"", xaml);
        Assert.Contains("WindowStyle=\"None\"", xaml);
        Assert.Contains("AllowsTransparency=\"True\"", xaml);
        Assert.Contains("CornerRadius=\"14\"", xaml);
        Assert.Contains("Background=\"{DynamicResource PanelBackground}\"", xaml);
        Assert.Contains("x:Name=\"ExportProgressBar\"", xaml);
        Assert.Contains("Height=\"6\"", xaml);
        Assert.Contains("x:Name=\"ElapsedTimeText\"", xaml);
        Assert.Contains("Grid.Column=\"1\"", xaml);
        Assert.Contains("HorizontalAlignment=\"Right\"", xaml);
        Assert.DoesNotContain("软件正在处理，请耐心等待", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CancelExportButton\"", xaml);
        Assert.Contains("Style=\"{StaticResource SecondaryButtonStyle}\"", xaml);
        Assert.DoesNotContain("FontFamily=", xaml, StringComparison.Ordinal);

        string codeBehind = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring", "UI", "PlaybackWindow.xaml.cs"));
        int stopIndex = codeBehind.IndexOf("await StopPlaybackForExportAsync();", StringComparison.Ordinal);
        int dialogIndex = codeBehind.IndexOf("new OrderNumberExportProgressDialog", StringComparison.Ordinal);
        Assert.True(stopIndex >= 0 && dialogIndex > stopIndex);
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

    [Theory]
    [InlineData(false, "", false)]
    [InlineData(false, "ORDER-123", true)]
    [InlineData(true, "", true)]
    public void SearchAllowsCleanedRecordsEvenWhenSettingHidesThem(
        bool showDeletedVideos,
        string keyword,
        bool expected)
    {
        Assert.Equal(
            expected,
            PlaybackWindow.ShouldIncludeDeletedVideos(showDeletedVideos, keyword));
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
    [InlineData("external", "android-1234567890a1b2c3", "手机1", "手机1")]
    [InlineData("EXTERNAL", "", "", "手机设备")]
    [InlineData("external", "", "一号打包手机", "一号打包手机")]
    [InlineData("pc", "pc-1", "一号电脑", "电脑")]
    [InlineData("", "", "", "电脑")]
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
    [InlineData("", "", "", null, "打包电脑", "打包电脑")]
    [InlineData("pc", "pc-1", "DESKTOP-ABC", null, "一号电脑", "一号电脑")]
    [InlineData("", "", "", null, "", "电脑")]
    [InlineData("external", "android-1", "手机1", null, "打包电脑", "手机1")]
    public void GetSourceDisplay_UsesLocalComputerNickname(
        string sourceType,
        string sourceDeviceId,
        string sourceDeviceName,
        string? sourceDeviceKind,
        string localComputerName,
        string expected)
    {
        Assert.Equal(
            expected,
            PlaybackWindow.GetSourceDisplay(
                sourceType,
                sourceDeviceId,
                sourceDeviceName,
                sourceDeviceKind,
                localComputerName));
    }

    [Fact]
    public void CreateVideoItem_PcRecordUsesComputerNickname()
    {
        var record = new VideoRecord
        {
            FilePath = Path.Combine(Path.GetTempPath(), "packingproof-pc-nickname.mp4"),
            SourceType = "pc",
            SourceDeviceId = "pc-1",
            SourceDeviceName = "DESKTOP-ABC",
            StorageState = "Local",
            FileSizeBytes = 1
        };

        VideoItem item = PlaybackWindow.CreateVideoItem(record, "打包电脑");

        Assert.Equal("打包电脑", item.SourceDisplay);
    }

    [Fact]
    public void PlaybackLayout_HasModeColorStripWithoutSourcePrefix()
    {
        string xaml = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring", "UI", "PlaybackWindow.xaml"));
        string codeBehind = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring", "UI", "PlaybackWindow.xaml.cs"));

        Assert.Contains("x:Name=\"ModeStrip\"", xaml);
        Assert.Contains("TargetName=\"ModeStrip\"", xaml);
        Assert.Contains("{DynamicResource AccentOrange}", xaml);
        Assert.Contains("Binding Mode", xaml);
        Assert.Contains("Value=\"退货\"", xaml);
        Assert.DoesNotContain("来源：", codeBehind, StringComparison.Ordinal);
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

        Assert.Contains("videos.Add(CreateVideoItem(record, _computerName));", codeBehind, StringComparison.Ordinal);
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
