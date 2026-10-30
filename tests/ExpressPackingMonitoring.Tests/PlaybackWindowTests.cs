using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using System.Globalization;
using System.Reflection;
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

    [Theory]
    [InlineData("zh-Hans", 1, 33, 1603, "第 1 / 33 页，共 1603 条")]
    [InlineData("en-US", 1, 33, 1603, "Page 1 of 33 · 1603 items")]
    [InlineData("ja-JP", 1, 33, 1603, "1 / 33 ページ · 全 1603 件")]
    [InlineData("en-US", 1, 0, 0, "No recordings")]
    public void FormatPageStatus_FollowsCurrentLanguage(
        string culture,
        int currentPage,
        int pageCount,
        int totalVideos,
        string expected)
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(
                expected,
                PlaybackWindow.FormatPageStatus(currentPage, pageCount, totalVideos));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Theory]
    [InlineData(@"\\NAS\share\video.mp4", true)]
    [InlineData("//NAS/share/video.mp4", true)]
    [InlineData(@"Z:\PackingProofVideos\video.mp4", false)]
    [InlineData(@"C:\PackingProofVideos\video.mp4", false)]
    [InlineData("", false)]
    public void PlaybackMediaSource_DetectsUncPaths(string path, bool expected)
    {
        Assert.Equal(expected, PlaybackMediaSource.IsUncPath(path));
    }

    [Fact]
    public void DeleteVideo_KeepsNativePlayerReleaseOffUiThread()
    {
        string playbackSource = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "PlaybackWindow.Playback.cs"));

        Assert.Contains("await Dispatcher.Yield(DispatcherPriority.Input);", playbackSource, StringComparison.Ordinal);
        Assert.Contains("await _playerSemaphore.WaitAsync(TimeSpan.FromSeconds(3))", playbackSource, StringComparison.Ordinal);
        Assert.Contains("await StopAndDisposePlaybackForDeletionAsync();", playbackSource, StringComparison.Ordinal);
        Assert.Contains("await Task.Run(() =>", playbackSource, StringComparison.Ordinal);
        Assert.Contains("player?.Stop();", playbackSource, StringComparison.Ordinal);
        Assert.Contains("source?.Dispose();", playbackSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteVideo_DefersWindowCloseUntilPlayerCleanupCompletes()
    {
        string windowSource = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "PlaybackWindow.xaml.cs"));
        string playbackSource = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "PlaybackWindow.Playback.cs"));

        Assert.Contains("if (_deleteInProgress)", windowSource, StringComparison.Ordinal);
        Assert.Contains("_closeAfterDelete = true;", windowSource, StringComparison.Ordinal);
        Assert.Contains("e.Cancel = true;", windowSource, StringComparison.Ordinal);
        Assert.Contains("_deleteInProgress = false;", playbackSource, StringComparison.Ordinal);
        Assert.Contains(
            "Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Background);",
            playbackSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowClose_KeepsNativePlayerReleaseOffUiThread()
    {
        string windowSource = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "PlaybackWindow.xaml.cs"));
        string playbackSource = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "PlaybackWindow.Playback.cs"));
        string normalizedPlaybackSource = playbackSource.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("BeginPlaybackShutdown();", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_mediaPlayer.Stop();", windowSource, StringComparison.Ordinal);
        Assert.Contains("private void BeginPlaybackShutdown()", normalizedPlaybackSource, StringComparison.Ordinal);
        Assert.Contains(
            "Task.Run(() =>\n                DisposePlaybackResourcesOnBackground",
            normalizedPlaybackSource,
            StringComparison.Ordinal);
        Assert.Contains("PlayerView.MediaPlayer = null;", normalizedPlaybackSource, StringComparison.Ordinal);
        Assert.Contains("player?.Stop();", normalizedPlaybackSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlayerInitialization_RequiresAnEmbeddedVideoHandleBeforePlayback()
    {
        string windowSource = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "PlaybackWindow.xaml.cs"));

        Assert.Contains("if (_mediaPlayer.Hwnd == IntPtr.Zero)", windowSource, StringComparison.Ordinal);
        Assert.Contains("内置播放器未能连接到回放窗口", windowSource, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("pc", "录像过短，少于 3 秒", "Recording too short; under 3 s")]
    [InlineData("pc", "文件过小，小于 50 KB", "File too small; under 50 KB")]
    [InlineData("pc", "扫码切换（音频异常）", "Switched by scan (audio error)")]
    [InlineData("external", "电脑工位上传", "Uploaded from PC workstation")]
    public void CreateVideoItem_LocalizesStopReason(
        string sourceType,
        string stopReason,
        string expected)
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var record = new VideoRecord
            {
                FilePath = Path.Combine(Path.GetTempPath(), "packingproof-stop-reason.mp4"),
                SourceType = sourceType,
                StopReason = stopReason,
                StorageState = "Local"
            };

            VideoItem item = PlaybackWindow.CreateVideoItem(record);

            Assert.Equal(expected, item.StopReason);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void CreateVideoItem_LocalizesCleanupReasonAndStatus()
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var record = new VideoRecord
            {
                FilePath = Path.Combine(Path.GetTempPath(), "packingproof-cleaned.mp4"),
                StorageState = "Local",
                IsDeleted = true,
                DeletedAt = new DateTime(2026, 10, 18, 0, 54, 0),
                DeleteReason = "录像过短，少于 3 秒"
            };

            VideoItem item = PlaybackWindow.CreateVideoItem(record);

            Assert.Equal("Recording too short; under 3 s", item.DeleteReason);
            Assert.Equal(
                "Cleaned up (Recording too short; under 3 s 10-18 00:54)",
                item.StatusText);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void UnavailableVideoMessages_FollowCurrentLanguage()
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var video = new VideoItem
            {
                OrderId = "MAN_010406",
                DeleteReason = "Recording too short; under 3 s",
                DeletedAt = new DateTime(2026, 10, 18, 1, 4, 7),
                FileSize = "93KB",
                Duration = "1s",
                FullPath = @"D:\videos\MAN_010406.mkv"
            };

            string deleted = PlaybackWindow.FormatDeletedVideoMessage(video);
            string missing = PlaybackWindow.FormatMissingVideoMessage(video);

            Assert.Contains("This recording was overwritten and deleted", deleted, StringComparison.Ordinal);
            Assert.Contains("Order number: MAN_010406", deleted, StringComparison.Ordinal);
            Assert.Contains("Delete reason: Recording too short; under 3 s", deleted, StringComparison.Ordinal);
            Assert.Contains("Original size: 93KB", deleted, StringComparison.Ordinal);
            Assert.Contains("Duration: 1s", deleted, StringComparison.Ordinal);
            Assert.Contains("The video file was deleted or moved externally", missing, StringComparison.Ordinal);
            Assert.Contains(@"Path: D:\videos\MAN_010406.mkv", missing, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
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
        Assert.Equal(
            AppLanguage.Get("已归档（本地副本已清理）"),
            item.StatusText);
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
    public void CreateVideoItem_PendingDeleteStaysStagedAndRemainsDeletable()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "packingproof-playback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "pending.mp4");
        string pending = RecordingDeleteStaging.GetPendingPath(file);
        File.WriteAllBytes(pending, new byte[64]);
        try
        {
            var record = new VideoRecord
            {
                FilePath = file,
                EndTime = DateTime.Now,
                ArchiveStatus = VideoArchiveStatus.LocalOnly,
                StorageState = "Local",
                FileSizeBytes = 64
            };

            VideoItem item = PlaybackWindow.CreateVideoItem(
                record,
                canDeleteRecords: true);

            Assert.True(item.IsMissing);
            Assert.True(item.CanDelete);
            Assert.False(File.Exists(file));
            Assert.True(File.Exists(pending));
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
            Assert.Equal(
                AppLanguage.Get("NAS 副本已循环清理"),
                item.StatusText);
            Assert.Equal(localPath, item.FullPath);
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    [Fact]
    public void ArchiveStatusText_FollowsCurrentLanguage()
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            const string archivePath = @"\\NAS\share\recording.mkv";

            Assert.Equal(
                @"Archive conflict: the network copy differs. Check \\NAS\share\recording.mkv",
                PlaybackWindow.FormatArchiveStatusText(new VideoRecord
                {
                    ArchiveStatus = VideoArchiveStatus.Conflict,
                    ArchivePath = archivePath
                }));
            Assert.Equal(
                @"Archive failed; retrying automatically: \\NAS\share\recording.mkv",
                PlaybackWindow.FormatArchiveStatusText(new VideoRecord
                {
                    ArchiveStatus = VideoArchiveStatus.Failed,
                    ArchivePath = archivePath
                }));
            Assert.Equal(
                @"Archive paused: NAS is full. Clean up \\NAS\share\recording.mkv",
                PlaybackWindow.FormatArchiveStatusText(new VideoRecord
                {
                    ArchiveStatus = VideoArchiveStatus.NASFull,
                    ArchivePath = archivePath
                }));
            Assert.Equal(
                "Archived (local copy cleaned up)",
                PlaybackWindow.FormatArchiveStatusText(new VideoRecord
                {
                    ArchiveStatus = VideoArchiveStatus.LocalDeleted,
                    ArchiveCompletedAt = DateTime.Now
                }));
            Assert.Equal(
                "Local recording cleaned up; not backed up to NAS",
                PlaybackWindow.FormatArchiveStatusText(new VideoRecord
                {
                    ArchiveStatus = VideoArchiveStatus.LocalDeleted
                }));
            Assert.Equal(
                "NAS copy rotated out",
                PlaybackWindow.FormatArchiveStatusText(new VideoRecord
                {
                    ArchiveStatus = VideoArchiveStatus.NasDeleted
                }));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void ArchiveStatusText_AllStatusesLocalizeInEnglish()
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            string[] statuses = typeof(VideoArchiveStatus)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue()!)
                .ToArray();
            foreach (string status in statuses)
            {
                string text = PlaybackWindow.FormatArchiveStatusText(new VideoRecord
                {
                    ArchiveStatus = status,
                    ArchivePath = @"\\NAS\share\recording.mkv",
                    ArchiveCompletedAt = DateTime.Now
                });

                Assert.DoesNotMatch("[\\p{IsCJKUnifiedIdeographs}]", text);
            }

            Assert.DoesNotMatch(
                "[\\p{IsCJKUnifiedIdeographs}]",
                PlaybackWindow.FormatArchiveStatusText(new VideoRecord
                {
                    ArchiveStatus = VideoArchiveStatus.LocalDeleted
                }));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine([directory.FullName, .. relativeParts]);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Repository file not found: {Path.Combine(relativeParts)}");
    }
}
