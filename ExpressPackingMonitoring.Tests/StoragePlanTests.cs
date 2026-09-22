using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using System;
using System.IO;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class StoragePlanTests : IDisposable
{
    private readonly IDisposable _volumeProbe = TestStorageVolumeProbe.Use();

    public void Dispose() => _volumeProbe.Dispose();

    [Fact]
    public void SelectUsableArchiveRoot_PrefersPriorityAndSkipsUnavailable()
    {
        string directory = CreateTempDirectory();
        try
        {
            string usable1 = Path.Combine(directory, "nas1");
            string usable2 = Path.Combine(directory, "nas2");
            string missingDriveRoot = Enumerable.Range('A', 26)
                .Select(letter => ((char)letter).ToString() + ":\\")
                .First(root => !Directory.Exists(root));
            string offline = Path.Combine(missingDriveRoot, "offline");
            Directory.CreateDirectory(usable1);
            Directory.CreateDirectory(usable2);

            var locations = new List<StorageLocation>
            {
                new() { Path = offline, Priority = 0 },
                new() { Path = usable1, Priority = 1 },
                new() { Path = usable2, Priority = 2 }
            };

            Assert.Equal(
                Path.GetFullPath(usable1),
                StorageLocationResolver.SelectUsableArchiveRoot(locations));
            Assert.Equal(
                Path.GetFullPath(usable2),
                StorageLocationResolver.SelectUsableArchiveRoot(
                    locations,
                    excludePath: usable1));
            Assert.Null(StorageLocationResolver.SelectUsableArchiveRoot(
                new List<StorageLocation>
                {
                    new() { Path = offline, Priority = 0 }
                }));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ResolveRecordingPlan_LocalLocationReturnsSameRoot()
    {
        string directory = CreateTempDirectory();
        try
        {
            var config = new AppConfig
            {
                StorageLocations =
                [
                    new StorageLocation { Path = Path.Combine(directory, "本地录像"), Priority = 0 }
                ]
            };

            RecordingStoragePlan plan = StorageLocationResolver.ResolveRecordingPlan(
                config,
                allowDefaultFallback: false);

            Assert.False(plan.RequiresNetworkArchive);
            Assert.Equal("", plan.ArchiveTarget);
            Assert.StartsWith(directory, plan.WorkingRootPath);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void ResolveRecordingPlan_NetworkLocationSelectsLocalWorkingRoot()
    {
        string local = CreateTempDirectory();
        try
        {
            var config = new AppConfig
            {
                StorageLocations =
                [
                    // 网络位置排首位也不影响本地主存储选择
                    new StorageLocation { Path = @"\\nas\share\快递打包视频", Priority = 0 },
                    new StorageLocation { Path = Path.Combine(local, "快递打包视频"), Priority = 1 }
                ]
            };

            RecordingStoragePlan plan = StorageLocationResolver.ResolveRecordingPlan(
                config,
                allowDefaultFallback: false);

            Assert.True(plan.RequiresNetworkArchive);
            Assert.Equal(Path.GetFullPath(Path.Combine(local, "快递打包视频")), plan.WorkingRootPath);
            Assert.Equal(@"\\nas\share\快递打包视频", plan.ArchiveTarget);
        }
        finally
        {
            TryDeleteDirectory(local);
        }
    }

    [Fact]
    public void ResolveRecordingPlan_UsesFirstConfiguredNetworkRootWithoutProbe()
    {
        string local = CreateTempDirectory();
        try
        {
            var config = new AppConfig
            {
                StorageLocations =
                [
                    new StorageLocation { Path = @"\\192.168.1.100\NASSim\快递打包视频", Priority = 0 },
                    new StorageLocation { Path = Path.Combine(local, "快递打包视频"), Priority = 1 }
                ]
            };

            RecordingStoragePlan plan = StorageLocationResolver.ResolveRecordingPlan(
                config,
                allowDefaultFallback: false);

            Assert.True(plan.RequiresNetworkArchive);
            Assert.Equal(@"\\192.168.1.100\NASSim\快递打包视频", plan.ArchiveTarget);
            Assert.StartsWith(local, plan.WorkingRootPath);
        }
        finally
        {
            TryDeleteDirectory(local);
        }
    }

    [Fact]
    public void UiArchiveTargetResolution_DoesNotProbeNetworkStorage()
    {
        string resolverSource = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring", "Services", "StorageLocationResolver.cs"));
        string cardSource = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring", "Services", "ArchiveBackupCardModel.cs"));

        Assert.DoesNotContain("SelectUsableArchiveRoot(config)", resolverSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectUsableArchiveRoot", cardSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveRecordingPlan_NetworkOnlyFallsBackOrThrows()
    {
        var config = new AppConfig
        {
            StorageLocations =
            [
                new StorageLocation { Path = @"\\nas\share\快递打包视频", Priority = 0 }
            ]
        };

        RecordingStoragePlan fallback = StorageLocationResolver.ResolveRecordingPlan(
            config,
            allowDefaultFallback: true);

        Assert.False(fallback.RequiresNetworkArchive);
        // 兜底位置按"哪块盘就放哪块盘的快递打包视频"算，不再落到程序安装目录下
        Assert.Equal(AppConfig.DefaultStoragePath, fallback.WorkingRootPath);
        Assert.Throws<IOException>(() => StorageLocationResolver.ResolveRecordingPlan(
            config,
            allowDefaultFallback: false));
    }

    [Fact]
    public void DefaultStoragePath_IsDriveRootFolderNotProgramDirectory()
    {
        // 兜底保存位置必须与"添加磁盘"同一套风格（<盘根>\快递打包视频），
        // 绝不允许落到程序安装目录下，否则升级、卸载都可能把录像清掉
        string path = AppConfig.DefaultStoragePath;

        Assert.Equal(Path.Combine(AppConfig.DefaultStorageRoot, "快递打包视频"), path);
        Assert.True(Path.IsPathRooted(path));
        Assert.DoesNotContain("Videos", path, StringComparison.OrdinalIgnoreCase);
        Assert.False(
            path.StartsWith(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase),
            path);
    }

    [Fact]
    public void Resolve_ReturnsLocalWorkingRoot()
    {
        string local = CreateTempDirectory();
        try
        {
            var config = new AppConfig
            {
                StorageLocations =
                [
                    new StorageLocation { Path = Path.Combine(local, "快递打包视频"), Priority = 1 },
                    new StorageLocation { Path = @"\\nas\share\快递打包视频", Priority = 0 }
                ]
            };

            Assert.Equal(
                Path.GetFullPath(Path.Combine(local, "快递打包视频")),
                StorageLocationResolver.Resolve(config, allowDefaultFallback: false));
        }
        finally
        {
            TryDeleteDirectory(local);
        }
    }

    [Fact]
    public void ResolveRecordingPlan_UnusableFirstLocalFallsBackToNextLocal()
    {
        string secondDir = CreateTempDirectory();
        try
        {
            var config = new AppConfig
            {
                StorageLocations =
                [
                    // 非法路径模拟“D 盘不可用”，应自动切到下一个本地盘
                    new StorageLocation { Path = @"C:\bad<dir>\录像", Priority = 0 },
                    new StorageLocation { Path = Path.Combine(secondDir, "录像"), Priority = 1 }
                ]
            };

            RecordingStoragePlan plan = StorageLocationResolver.ResolveRecordingPlan(
                config,
                allowDefaultFallback: false);

            Assert.False(plan.RequiresNetworkArchive);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(secondDir, "录像")),
                plan.WorkingRootPath);
        }
        finally
        {
            TryDeleteDirectory(secondDir);
        }
    }

    [Fact]
    public void ResolveRecordingPlan_UnknownLocalPathIsSkippedFailClosed()
    {
        string realLocal = CreateTempDirectory();
        try
        {
            string missingDriveRoot = Enumerable.Range('A', 26)
                .Select(letter => ((char)letter).ToString() + ":\\")
                .First(root => !Directory.Exists(root));
            var config = new AppConfig
            {
                StorageLocations =
                [
                    new StorageLocation { Path = missingDriveRoot + "录像", Priority = 0 },
                    new StorageLocation { Path = Path.Combine(realLocal, "录像"), Priority = 1 }
                ]
            };

            RecordingStoragePlan plan = StorageLocationResolver.ResolveRecordingPlan(
                config,
                allowDefaultFallback: false);

            Assert.False(plan.RequiresNetworkArchive);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(realLocal, "录像")),
                plan.WorkingRootPath);
        }
        finally
        {
            TryDeleteDirectory(realLocal);
        }
    }

    [Fact]
    public void ResolveRecordingPlan_StartupCanIgnoreReserveWhileReceivePathStillRejectsLowSpace()
    {
        string directory = CreateTempDirectory();
        try
        {
            // 只有 500MB 可用的假卷：低于预留值
            StorageLocationResolver.VolumeProbe = _ => new StorageVolumeInfo(
                directory,
                1024L * 1024 * 1024 * 1024,
                512L * 1024 * 1024,
                "test-volume");
            var config = new AppConfig
            {
                StorageLocations =
                [
                    new StorageLocation { Path = directory, Priority = 0 }
                ]
            };

            // 接收侧（默认）：剩余空间低于预留值 → 位置不可用，上传按存储不可用拒收
            Assert.Throws<IOException>(() => StorageLocationResolver.ResolveRecordingPlan(
                config,
                allowDefaultFallback: false));

            // 保存主机启动：位置在、写得了就照常起服务，空间不足由接收侧说明原因
            RecordingStoragePlan plan = StorageLocationResolver.ResolveRecordingPlan(
                config,
                allowDefaultFallback: false,
                requireFreeSpaceAboveReserve: false);

            Assert.Equal(Path.GetFullPath(directory), plan.WorkingRootPath);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void LocalStorageGates_AreFailClosed()
    {
        string driveDialog = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring", "UI", "DriveSelectionDialog.cs"));
        string settings = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring", "UI", "SettingsWindow.xaml.cs"));

        Assert.Contains("ClassifyStorageLocation", driveDialog, StringComparison.Ordinal);
        Assert.Contains("StorageLocationKind.Local", driveDialog, StringComparison.Ordinal);
        Assert.Contains("不能作为本地录像保存位置", settings, StringComparison.Ordinal);
        Assert.Contains("无法确认存储位置类型", settings, StringComparison.Ordinal);
        Assert.Contains("本地磁盘请添加到录像保存位置", settings, StringComparison.Ordinal);
        Assert.Contains("网盘挂载盘不建议作为备份位置", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void StorageSettings_HideSpaceLimitAndKeepReserveOnlyInContextMenu()
    {
        string settings = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring", "UI", "SettingsWindow.xaml"));
        string dialog = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring", "UI", "StorageReserveDialog.xaml"));

        // "空间上限"会被理解成"本软件最多占用多少"，据此反推的预留会把整块盘算成不可用：
        // 保存位置/备份位置不再露出这个参数，默认预留直接用
        Assert.DoesNotContain("StorageCapacityCellTemplate", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"备份空间上限\"", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("容量上限不会提前占用空间", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Binding EffectiveReserveGB", settings, StringComparison.Ordinal);
        // 预留只从磁盘右键改，并且说明这是整块磁盘的预留
        Assert.Contains("StorageLocationRowStyle", settings, StringComparison.Ordinal);
        Assert.Contains("Header=\"设置预留空间…\"", settings, StringComparison.Ordinal);
        Assert.Contains("预留空间是留给整块磁盘的安全空间", dialog, StringComparison.Ordinal);
        Assert.Contains("不是本软件最多占用多少", dialog, StringComparison.Ordinal);
    }

    [Fact]
    public void StorageSettings_WarnWhenReserveFallsBelowRecommendation()
    {
        string settings = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring", "UI", "SettingsWindow.xaml"));
        string settingsCode = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring", "UI", "SettingsWindow.xaml.cs"));

        Assert.Contains(
            "x:Name=\"StorageReserveWarningHint\"",
            settings,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"BackupReserveWarningHint\"",
            settings,
            StringComparison.Ordinal);
        Assert.Contains("RefreshStorageReserveWarnings", settingsCode, StringComparison.Ordinal);
        Assert.Contains(
            "StorageReserveWarningPolicy.Evaluate",
            settingsCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ArchivePathBuilder_LocalRecordingLayout()
    {
        string path = ArchivePathBuilder.BuildLocalRecordingArchivePath(
            @"\\nas\share\快递打包视频",
            new DateTime(2026, 8, 11, 10, 30, 0),
            "SF123_20260811_103000_发货.mkv");

        Assert.Equal(@"\\nas\share\快递打包视频\2026-08-11\SF123_20260811_103000_发货.mkv", path);
        Assert.Equal("", ArchivePathBuilder.BuildLocalRecordingArchivePath("", DateTime.Now, "a.mkv"));
    }

    [Fact]
    public void ArchivePathBuilder_ExternalUploadLayout()
    {
        string pcPath = ArchivePathBuilder.BuildExternalUploadArchivePath(
            @"\\nas\share\快递打包视频",
            "pc",
            "device-abc123456",
            "打包工位1",
            new DateTime(2026, 8, 11, 10, 30, 0),
            "sf123",
            "return",
            "abcdef123456");
        Assert.Equal(
            @"\\nas\share\快递打包视频\电脑上传\device-abc123456\2026-08-11\SF123_20260811_103000_退货.mp4",
            pcPath);

        string mobilePath = ArchivePathBuilder.BuildExternalUploadArchivePath(
            @"\\nas\share\快递打包视频",
            "mobile",
            "phone-xyz",
            "手机1",
            new DateTime(2026, 8, 11, 10, 30, 0),
            "",
            "发货",
            "abcdef");
        Assert.Equal(
            @"\\nas\share\快递打包视频\手机备份\phone-xyz\2026-08-11\未识别面单_20260811_103000_发货.mp4",
            mobilePath);
    }

    [Fact]
    public void GetOrderedBackupLocations_IncludesFlaggedVirtualDiskAndUnc()
    {
        var config = new AppConfig
        {
            StorageLocations =
            [
                new StorageLocation { Path = @"D:\本地录像", Priority = 0 },
                new StorageLocation
                {
                    Path = @"Z:\云盘备份",
                    Priority = 1,
                    IsBackupTarget = true
                },
                new StorageLocation { Path = @"\\nas\share\备份", Priority = 2 }
            ]
        };

        IReadOnlyList<StorageLocation> backups =
            StorageLocationResolver.GetOrderedBackupLocations(config);

        Assert.Equal(2, backups.Count);
        Assert.Equal(@"Z:\云盘备份", backups[0].Path);
        Assert.Equal(@"\\nas\share\备份", backups[1].Path);
    }

    [Fact]
    public void IsBackupLocation_FlagKeepsUnmountedVirtualDiskAsBackup()
    {
        string missingRoot = Enumerable.Range('A', 26)
            .Select(letter => ((char)letter).ToString() + ":\\")
            .First(root => !Directory.Exists(root));
        var location = new StorageLocation
        {
            Path = Path.Combine(missingRoot, "云盘备份"),
            IsBackupTarget = true
        };

        Assert.True(StorageLocationResolver.IsBackupLocation(location));
    }

    [Fact]
    public void StorageOverviewLocations_ExcludeBackupTargets()
    {
        var config = new AppConfig
        {
            StorageLocations =
            [
                new StorageLocation
                {
                    Path = @"D:\录像",
                    Priority = 2
                },
                new StorageLocation
                {
                    Path = @"Z:\NAS备份",
                    Priority = 0,
                    IsBackupTarget = true
                },
                new StorageLocation
                {
                    Path = @"\\nas\share\备份",
                    Priority = 1
                }
            ]
        };

        StorageLocation location = Assert.Single(WebServer.GetStorageOverviewLocations(config));

        Assert.Equal(@"D:\录像", location.Path);
    }

    [Fact]
    public void ResolveRecordingPlan_FlaggedLocalPathIsArchiveTargetNotPrimary()
    {
        string directory = CreateTempDirectory();
        try
        {
            string primary = Path.Combine(directory, "本地录像");
            string flaggedBackup = Path.Combine(directory, "同机备份");
            var config = new AppConfig
            {
                StorageLocations =
                [
                    new StorageLocation { Path = primary, Priority = 0 },
                    new StorageLocation
                    {
                        Path = flaggedBackup,
                        Priority = 1,
                        IsBackupTarget = true
                    }
                ]
            };

            RecordingStoragePlan plan = StorageLocationResolver.ResolveRecordingPlan(
                config,
                allowDefaultFallback: false);

            Assert.Equal(Path.GetFullPath(primary), plan.WorkingRootPath);
            Assert.True(plan.RequiresNetworkArchive);
            Assert.Equal(Path.GetFullPath(flaggedBackup), plan.ArchiveTarget);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "epm-storage-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try { Directory.Delete(directory, recursive: true); } catch { }
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
