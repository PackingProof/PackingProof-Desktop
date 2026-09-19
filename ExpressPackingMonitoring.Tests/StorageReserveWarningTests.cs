using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.UI;
using System.IO;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class StorageReserveWarningTests
{
    private static string SystemDriveRecordingPath =>
        Path.Combine(
            Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!,
            "快递打包视频");

    private const string NetworkRecordingPath = @"\\NAS\share\快递打包视频";
    private const string OtherDriveRecordingPath = @"Q:\快递打包视频";

    [Fact]
    public void Evaluate_NoWarningWhenReserveStaysAtRecommendation()
    {
        var locations = new[]
        {
            new StorageLocation { Path = SystemDriveRecordingPath, ReserveGB = 10 },
            new StorageLocation { Path = NetworkRecordingPath, ReserveGB = 5 },
            // 0 表示用户没有单独设置，走默认值，同样不需要提示
            new StorageLocation { Path = OtherDriveRecordingPath, ReserveGB = 0 }
        };

        Assert.Null(StorageReserveWarningPolicy.Evaluate(locations));
    }

    [Fact]
    public void Evaluate_IgnoresEmptyLocations()
    {
        var locations = new[]
        {
            new StorageLocation { Path = "", ReserveGB = 1 },
            new StorageLocation { Path = "   ", ReserveGB = 1 }
        };

        Assert.Null(StorageReserveWarningPolicy.Evaluate(locations));
        Assert.Null(StorageReserveWarningPolicy.Evaluate(null));
    }

    [Fact]
    public void Evaluate_WarnsWhenReserveIsLoweredToFloorOnSystemDrive()
    {
        var locations = new[]
        {
            new StorageLocation { Path = SystemDriveRecordingPath, ReserveGB = 2 }
        };

        StorageReserveWarning warning =
            StorageReserveWarningPolicy.Evaluate(locations)!;

        Assert.Equal(1, warning.LocationCount);
        Assert.Equal(2, warning.SmallestReserveGB);
        Assert.Equal(10, warning.RecommendedReserveGB);
        Assert.Contains("预留空间偏小", warning.Message, StringComparison.Ordinal);
        Assert.Contains("建议至少 10 GB", warning.Message, StringComparison.Ordinal);
        Assert.Contains(SystemDriveRecordingPath, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_UsesSystemDriveRecommendationForDirectoryOnSystemDrive()
    {
        // 系统盘上的录像目录按系统盘算：5GB 已经是其他盘的建议值，但低于系统盘的 10GB
        var locations = new[]
        {
            new StorageLocation { Path = SystemDriveRecordingPath, ReserveGB = 5 }
        };

        StorageReserveWarning warning =
            StorageReserveWarningPolicy.Evaluate(locations)!;

        Assert.Equal(10, warning.RecommendedReserveGB);
    }

    [Fact]
    public void Evaluate_ReportsCountAndSmallestReserve()
    {
        var locations = new[]
        {
            new StorageLocation { Path = SystemDriveRecordingPath, ReserveGB = 3 },
            new StorageLocation { Path = NetworkRecordingPath, ReserveGB = 1 },
            new StorageLocation { Path = OtherDriveRecordingPath, ReserveGB = 4 }
        };

        StorageReserveWarning warning =
            StorageReserveWarningPolicy.Evaluate(locations)!;

        Assert.Equal(3, warning.LocationCount);
        Assert.Equal(1, warning.SmallestReserveGB);
        Assert.Equal(10, warning.RecommendedReserveGB);
        Assert.Equal(NetworkRecordingPath, warning.SmallestLocationPath);
        Assert.Contains("3 个位置", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_KeepsOnlyTightLocationsInCount()
    {
        var locations = new[]
        {
            new StorageLocation { Path = SystemDriveRecordingPath, ReserveGB = 2 },
            new StorageLocation { Path = OtherDriveRecordingPath, ReserveGB = 0 }
        };

        StorageReserveWarning warning =
        StorageReserveWarningPolicy.Evaluate(locations)!;

        Assert.Equal(1, warning.LocationCount);
        Assert.Equal(SystemDriveRecordingPath, warning.SmallestLocationPath);
    }
}
