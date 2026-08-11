using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using System;
using System.IO;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class StoragePlanTests
{
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
        Assert.Throws<IOException>(() => StorageLocationResolver.ResolveRecordingPlan(
            config,
            allowDefaultFallback: false));
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
            @"\\nas\share\快递打包视频\电脑上传\打包工位1-123456\2026-08-11\SF123_20260811_103000_退货.mp4",
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
            @"\\nas\share\快递打包视频\手机备份\手机1-ONEXYZ\2026-08-11\未识别面单_20260811_103000_发货.mp4",
            mobilePath);
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
