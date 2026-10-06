using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class PlaybackWindowTests
{
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
    public void CreateVideoItem_DeleteFollowsWorkstationCapabilityAndRecordState()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"packingproof-delete-{Guid.NewGuid():N}");
        string file = Path.Combine(folder, "video.mp4");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(file, [1]);
        try
        {
            var record = new VideoRecord
            {
                Id = 7,
                FilePath = file,
                OrderId = "单号A",
                EndTime = DateTime.Now,
                ArchiveStatus = VideoArchiveStatus.LocalOnly
            };

            VideoItem allowed = PlaybackWindow.CreateVideoItem(record, canDeleteRecords: true);
            VideoItem viewer = PlaybackWindow.CreateVideoItem(record, canDeleteRecords: false);
            VideoItem recording = PlaybackWindow.CreateVideoItem(
                new VideoRecord
                {
                    Id = 8,
                    FilePath = file,
                    ArchiveStatus = VideoArchiveStatus.LocalOnly
                },
                canDeleteRecords: true);

            Assert.True(allowed.CanDelete);
            Assert.Equal(7, allowed.RecordId);
            Assert.Equal("", allowed.DeleteBlockedHint);

            Assert.False(viewer.CanDelete);
            Assert.Equal("当前工位只能查看，不能删除录像", viewer.DeleteBlockedHint);

            Assert.False(recording.CanDelete);
            Assert.Contains("正在录制", recording.DeleteBlockedHint);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
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
}
