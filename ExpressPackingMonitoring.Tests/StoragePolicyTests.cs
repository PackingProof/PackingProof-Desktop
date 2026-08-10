using ExpressPackingMonitoring.Config;
using System.IO;
using System.Text.Json;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class StoragePolicyTests
{
    public static TheoryData<long, long, Func<long, long>> ReserveCases => new()
    {
        { 100L * StorageSpacePolicy.BytesPerGiB, 30L * StorageSpacePolicy.BytesPerGiB, total => StorageSpacePolicy.CalculateMinimumReserveBytes(total, isSystemDrive: true) },
        { 500L * StorageSpacePolicy.BytesPerGiB, 50L * StorageSpacePolicy.BytesPerGiB, total => StorageSpacePolicy.CalculateMinimumReserveBytes(total, isSystemDrive: true) },
        { 100L * StorageSpacePolicy.BytesPerGiB, 20L * StorageSpacePolicy.BytesPerGiB, total => StorageSpacePolicy.CalculateMinimumReserveBytes(total, isSystemDrive: false) },
        { 500L * StorageSpacePolicy.BytesPerGiB, 25L * StorageSpacePolicy.BytesPerGiB, total => StorageSpacePolicy.CalculateMinimumReserveBytes(total, isSystemDrive: false) },
        { 100L * StorageSpacePolicy.BytesPerGiB, 10L * StorageSpacePolicy.BytesPerGiB, StorageSpacePolicy.CalculateNetworkMinimumReserveBytes },
        { 500L * StorageSpacePolicy.BytesPerGiB, 10L * StorageSpacePolicy.BytesPerGiB, StorageSpacePolicy.CalculateNetworkMinimumReserveBytes },
        { 1000L * StorageSpacePolicy.BytesPerGiB, 20L * StorageSpacePolicy.BytesPerGiB, StorageSpacePolicy.CalculateNetworkMinimumReserveBytes }
    };

    [Theory]
    [MemberData(nameof(ReserveCases))]
    public void CalculateMinimumReserveBytes_AppliesKindPolicy(
        long totalBytes,
        long expectedBytes,
        Func<long, long> calculator)
    {
        Assert.Equal(expectedBytes, calculator(totalBytes));
    }

    [Fact]
    public void IsNetworkPath_DetectsUncAndLocalPaths()
    {
        Assert.True(StorageVolumeInfo.IsNetworkPath(@"\\server\share\dir"));
        Assert.False(StorageVolumeInfo.IsNetworkPath(Path.GetTempPath()));
        Assert.False(StorageVolumeInfo.IsNetworkPath(""));
    }

    [Fact]
    public void VolumeId_IsFilledForLocalRootAndEmptyForUnc()
    {
        string root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
        string volumeId = StorageVolumeInfo.GetVolumeIdForRoot(root);
        Assert.StartsWith(@"\\?\Volume{", volumeId);
        Assert.EndsWith("}", volumeId);
        Assert.Equal("", StorageVolumeInfo.GetVolumeIdForRoot(@"\\server\share\"));
    }

    [Fact]
    public void TryGet_ReadsLocalVolumeCapacity()
    {
        string path = Path.Combine(Path.GetTempPath(), "epm-volume-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(path);
            Assert.True(StorageVolumeInfo.TryGet(path, out StorageVolumeInfo volume));
            Assert.True(volume.TotalSize > 0);
            Assert.True(volume.AvailableFreeSpace > 0);
            Assert.False(string.IsNullOrWhiteSpace(volume.RootPath));
        }
        finally
        {
            try { Directory.Delete(path, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RefreshVolumeId_FillsOnceAndKeepsTimestamp()
    {
        string root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
        var location = new StorageLocation { Path = Path.Combine(root, "epm-volume-meta") };

        Assert.True(StorageLocationMetadata.RefreshVolumeId(location));
        Assert.StartsWith(@"\\?\Volume{", location.VolumeId);
        Assert.NotNull(location.LastVerifiedAt);

        DateTime? firstVerifiedAt = location.LastVerifiedAt;
        Assert.False(StorageLocationMetadata.RefreshVolumeId(location));
        Assert.Equal(firstVerifiedAt, location.LastVerifiedAt);
    }

    [Fact]
    public void RefreshVolumeId_SkipsNetworkLocations()
    {
        var location = new StorageLocation { Path = @"\\server\share\dir" };
        Assert.False(StorageLocationMetadata.RefreshVolumeId(location));
        Assert.Equal("", location.VolumeId);
    }

    [Fact]
    public void StorageLocation_VolumeIdRoundTripsThroughJson()
    {
        var location = new StorageLocation
        {
            Path = @"D:\快递打包视频",
            VolumeId = @"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}",
            LastVerifiedAt = new DateTime(2026, 8, 11, 10, 0, 0)
        };

        string json = JsonSerializer.Serialize(location);
        StorageLocation restored = JsonSerializer.Deserialize<StorageLocation>(json)!;

        Assert.Equal(location.VolumeId, restored.VolumeId);
        Assert.Equal(location.LastVerifiedAt, restored.LastVerifiedAt);
    }

    [Fact]
    public void NormalizeAfterLoad_FillsBufferAndVolumeMetadata()
    {
        string root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
        var config = new AppConfig
        {
            StorageLocations =
            [
                new StorageLocation { Path = Path.Combine(root, "epm-normalize-meta") }
            ],
            LocalRecordingBufferPath = ""
        };

        AppConfig.NormalizeAfterLoad(config);

        Assert.False(string.IsNullOrWhiteSpace(config.LocalRecordingBufferPath));
        Assert.StartsWith(@"\\?\Volume{", config.StorageLocations[0].VolumeId);
        Assert.NotNull(config.StorageLocations[0].LastVerifiedAt);
    }

    [Fact]
    public void TryResolveUncPath_UncPassthrough()
    {
        Assert.True(StorageVolumeInfo.TryResolveUncPath(
            @"\\NAS\share\folder",
            out string uncPath));
        Assert.Equal(@"\\NAS\share\folder", uncPath);
    }

    [Fact]
    public void TryResolveUncPath_LocalPathFails()
    {
        Assert.False(StorageVolumeInfo.TryResolveUncPath(Path.GetTempPath(), out _));
    }

    [Fact]
    public void TryResolveUncPath_MappedDriveJoinsWithoutDoubleSeparator()
    {
        Assert.True(StorageVolumeInfo.TryResolveUncPath(
            @"Z:\folder",
            out string uncPath,
            root => @"\\NAS\share"));
        Assert.Equal(@"\\NAS\share\folder", uncPath);

        Assert.True(StorageVolumeInfo.TryResolveUncPath(
            @"Z:\",
            out string rootOnly,
            root => @"\\NAS\share"));
        Assert.Equal(@"\\NAS\share", rootOnly);
    }

    [Fact]
    public void TryResolveUncPath_UnresolvableRootKeepsOriginal()
    {
        Assert.False(StorageVolumeInfo.TryResolveUncPath(
            @"Z:\folder",
            out _,
            root => null));
        Assert.False(StorageVolumeInfo.TryResolveUncPath(
            @"Z:\folder",
            out _,
            root => @"C:\not-a-remote"));
    }
}
