using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class StorageAvailabilityPolicyTests
{
    [Fact]
    public void ExistingStorageLocationIsAvailable()
    {
        string directory = CreateTempDirectory();
        try
        {
            Assert.False(StorageAvailabilityPolicy.IsLocationUnavailable(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DeletedStorageFolderOnExistingVolumeIsFileLossNotMissingDisk()
    {
        // 卷根还在、只是目录被删：必须按文件丢失处理，不能对外说磁盘没接入
        string directory = CreateTempDirectory();
        try
        {
            string missingFolder = Path.Combine(directory, "已删除的录像目录");
            Assert.False(StorageAvailabilityPolicy.IsLocationUnavailable(missingFolder));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingVolumeRootCountsAsUnavailable()
    {
        // 盘符或卷根消失（外接盘被拔掉）才算存储位置访问不了
        string location = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "不存在的卷", "快递打包视频");
        string root = Path.GetPathRoot(location)!;
        Assert.True(StorageAvailabilityPolicy.IsLocationUnavailable(
            location,
            path => !string.Equals(path, root, StringComparison.Ordinal)));
    }

    [Fact]
    public void BlankOrRelativePathIsNeverReportedAsUnavailable()
    {
        Assert.False(StorageAvailabilityPolicy.IsLocationUnavailable(null));
        Assert.False(StorageAvailabilityPolicy.IsLocationUnavailable(""));
        Assert.False(StorageAvailabilityPolicy.IsLocationUnavailable("快递打包视频", _ => false));
    }

    [Fact]
    public void FileUnderUnavailableLocationIsDetected()
    {
        string[] unavailable = [@"D:\快递打包视频"];
        Assert.True(StorageAvailabilityPolicy.IsUnderUnavailableLocation(
            @"D:\快递打包视频\手机备份\手机1\ORDER-1.mp4",
            unavailable));
        Assert.True(StorageAvailabilityPolicy.IsUnderUnavailableLocation(
            @"d:\快递打包视频",
            unavailable));
        Assert.False(StorageAvailabilityPolicy.IsUnderUnavailableLocation(
            @"E:\快递打包视频\手机备份\ORDER-1.mp4",
            unavailable));
        Assert.False(StorageAvailabilityPolicy.IsUnderUnavailableLocation(
            @"D:\快递打包视频备份\ORDER-1.mp4",
            unavailable));
    }

    [Fact]
    public void MissingFileOnAvailableLocationIsNotReportedAsStorageUnavailable()
    {
        // 没有不可用的存储位置时，任何缺失都只能是文件丢失
        string[] unavailable = [];
        Assert.False(StorageAvailabilityPolicy.IsUnderUnavailableLocation(
            @"D:\快递打包视频\手机备份\ORDER-1.mp4",
            unavailable));
        Assert.False(StorageAvailabilityPolicy.IsUnderUnavailableLocation("", [@"D:\快递打包视频"]));
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"epm-storage-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
