using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using System.Globalization;
using System.Text.Json;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class DefaultConfigurationTests
{
    [Fact]
    public void NewConfigurationProtectsVideoWebByDefault()
    {
        var config = new AppConfig();

        AppConfig.NormalizeAfterLoad(config);

        Assert.True(config.RequireWebAccessKey);
        Assert.Equal(AppConfig.CurrentWebProtectionSetupVersion, config.WebProtectionSetupVersion);
        Assert.Equal(32, config.WebAccessKey.Length);
    }

    [Fact]
    public void NewConfigurationAcceptsThirdPartyWatermarkByDefault()
    {
        Assert.True(new AppConfig().EnableThirdPartyWatermark);
        Assert.True(JsonSerializer.Deserialize<AppConfig>("{}")!.EnableThirdPartyWatermark);
    }

    /// <summary>空闲降帧默认开着（省资源），用户可以在录像设置里关掉换成始终满帧。</summary>
    [Fact]
    public void NewConfigurationThrottlesIdlePreviewByDefault()
    {
        Assert.True(new AppConfig().EnablePreviewIdleThrottle);
        Assert.True(JsonSerializer.Deserialize<AppConfig>("{}")!.EnablePreviewIdleThrottle);
    }

    /// <summary>接收预览版更新默认关闭：正式机器不能被预览版拉走。</summary>
    [Fact]
    public void NewConfigurationDoesNotReceivePreviewUpdatesByDefault()
    {
        Assert.False(new AppConfig().AllowPrereleaseUpdates);
        Assert.False(JsonSerializer.Deserialize<AppConfig>("{}")!.AllowPrereleaseUpdates);
    }

    [Fact]
    public void NewConfigurationUsesTwoPointFiveSecondZoomDwellByDefault()
    {
        Assert.Equal(2.5, new AppConfig().ZoomDurationSeconds);
        Assert.Equal(2.5, JsonSerializer.Deserialize<AppConfig>("{}")!.ZoomDurationSeconds);
        Assert.Equal(200.0, new AppConfig().ZoomAnimationDurationMs);
        Assert.Equal(200.0, JsonSerializer.Deserialize<AppConfig>("{}")!.ZoomAnimationDurationMs);
    }

    /// <summary>
    /// 放大取景框默认居中、占画面 2/3，等价于老的"居中放大 1.5 倍"；
    /// 平时默认画出放大框，方便确认放大位置。
    /// </summary>
    [Fact]
    public void NewConfigurationUsesCenteredTwoThirdsZoomGuideByDefault()
    {
        var config = new AppConfig();
        var deserialized = JsonSerializer.Deserialize<AppConfig>("{}")!;

        Assert.Equal(AppConfig.DefaultZoomGuideRatio, config.ZoomGuideWidthRatio, 3);
        Assert.Equal(AppConfig.DefaultZoomGuideRatio, config.ZoomGuideHeightRatio, 3);
        Assert.Equal(0, config.ZoomGuideOffsetX);
        Assert.Equal(0, config.ZoomGuideOffsetY);
        Assert.True(config.ShowZoomGuideBox);

        Assert.Equal(AppConfig.DefaultZoomGuideRatio, deserialized.ZoomGuideWidthRatio, 3);
        Assert.Equal(AppConfig.DefaultZoomGuideRatio, deserialized.ZoomGuideHeightRatio, 3);
        Assert.True(deserialized.ShowZoomGuideBox);
        // 放大特写时默认让小窗淡出（特写画面更干净），关掉就恢复原来"小窗一直盖在上面"
        Assert.True(config.HideOverlayDuringZoom);
        Assert.True(deserialized.HideOverlayDuringZoom);
    }

    /// <summary>放大取景框写坏时按识别框同一套规则夹紧，避免算出 0 或负倍率。</summary>
    [Theory]
    [InlineData(0.05, 0.3)]
    [InlineData(-1.0, 0.3)]
    [InlineData(2.0, 1.0)]
    [InlineData(0.5, 0.5)]
    public void ZoomGuideRatiosAreClampedOnLoad(double stored, double expected)
    {
        var config = JsonSerializer.Deserialize<AppConfig>(
            $$"""{"ZoomGuideWidthRatio": {{stored}}, "ZoomGuideHeightRatio": {{stored}}, "ZoomGuideOffsetX": {{stored}}, "ZoomGuideOffsetY": {{stored}}}""")!;

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal(expected, config.ZoomGuideWidthRatio, 3);
        Assert.Equal(expected, config.ZoomGuideHeightRatio, 3);
        Assert.Equal(Math.Clamp(stored, -1.0, 1.0), config.ZoomGuideOffsetX, 3);
        Assert.Equal(Math.Clamp(stored, -1.0, 1.0), config.ZoomGuideOffsetY, 3);
    }

    /// <summary>
    /// 老版本把智能特写停留时间的默认值写成 3 秒、中间版本写成 1 秒，并会被保存进用户配置；
    /// 加载时要迁到现在的默认 2.5 秒，用户自己调过的其他值保持不动。
    /// </summary>
    [Theory]
    [InlineData(3.0, 2.5)]
    [InlineData(1.0, 2.5)]
    [InlineData(2.5, 2.5)]
    [InlineData(2.0, 2.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(4.0, 4.0)]
    public void LegacyZoomDwellDefaultsAreMigratedToCurrentDefaultOnLoad(double stored, double expected)
    {
        var config = JsonSerializer.Deserialize<AppConfig>($$"""{"ZoomDurationSeconds": {{stored}}}""")!;

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal(expected, config.ZoomDurationSeconds);
    }

    [Fact]
    public void ExistingConfigurationEnablesProtectionOnceAndPreservesLaterUserChoice()
    {
        var config = new AppConfig
        {
            RequireWebAccessKey = false,
            WebProtectionSetupVersion = 0
        };

        Assert.True(AppConfig.NormalizeAfterLoad(config));
        Assert.True(config.RequireWebAccessKey);
        Assert.Equal(AppConfig.CurrentWebProtectionSetupVersion, config.WebProtectionSetupVersion);

        config.RequireWebAccessKey = false;
        AppConfig.NormalizeAfterLoad(config);

        Assert.False(config.RequireWebAccessKey);
        Assert.Equal(AppConfig.CurrentWebProtectionSetupVersion, config.WebProtectionSetupVersion);
    }

    [Fact]
    public void DeletedVideoVisibilityMigrationForcesLegacyConfigToHiddenOnce()
    {
        var config = new AppConfig { ShowDeletedVideos = true };

        Assert.True(AppConfig.NormalizeAfterLoad(config));
        Assert.False(config.ShowDeletedVideos);
        Assert.Equal(AppConfig.CurrentDeletedVideoVisibilitySetupVersion,
            config.DeletedVideoVisibilitySetupVersion);

        config.ShowDeletedVideos = true;
        Assert.False(AppConfig.NormalizeAfterLoad(config));
        Assert.True(config.ShowDeletedVideos);
    }
    [Fact]
    public void AppConfig_EnablesAutoStartForNewConfiguration()
    {
        Assert.True(new AppConfig().AutoStartOnBoot);
        Assert.True(JsonSerializer.Deserialize<AppConfig>("{}")!.AutoStartOnBoot);
    }

    [Fact]
    public void AppConfig_PreservesExplicitlyDisabledAutoStart()
    {
        AppConfig config = JsonSerializer.Deserialize<AppConfig>("{\"AutoStartOnBoot\":false}")!;

        AppConfig.NormalizeAfterLoad(config);

        Assert.False(config.AutoStartOnBoot);
    }

    [Fact]
    public void AppConfig_HidesAdvancedSettingsForLegacyConfiguration()
    {
        AppConfig config = JsonSerializer.Deserialize<AppConfig>(
            "{\"VideoCqp\":19,\"ScannerAutoSubmitQuietMs\":345}")!;

        AppConfig.NormalizeAfterLoad(config);

        Assert.False(config.ShowAdvancedSettings);
        Assert.False(config.EnableDirectAacRecording);
        Assert.Equal(19, config.VideoCqp);
        Assert.Equal(345, config.ScannerAutoSubmitQuietMs);
    }

    [Fact]
    public void AppConfig_PreservesAdvancedSettingsVisibilityAndValuesDuringRoundTrip()
    {
        var original = new AppConfig
        {
            ShowAdvancedSettings = true,
            EnableDirectAacRecording = true,
            VideoCqp = 22,
            ScannerAutoSubmitQuietMs = 310
        };

        AppConfig restored = JsonSerializer.Deserialize<AppConfig>(
            JsonSerializer.Serialize(original))!;

        Assert.True(restored.ShowAdvancedSettings);
        Assert.True(restored.EnableDirectAacRecording);
        Assert.Equal(22, restored.VideoCqp);
        Assert.Equal(310, restored.ScannerAutoSubmitQuietMs);
    }

    [Theory]
    [InlineData(1, AppConfig.HighestQualityVideoCqp)]
    [InlineData(17, AppConfig.HighestQualityVideoCqp)]
    [InlineData(18, 18)]
    [InlineData(30, 30)]
    [InlineData(36, 36)]
    [InlineData(51, AppConfig.LowestQualityVideoCqp)]
    public void NormalizeVideoCqp_RestrictsVideoQualityToPracticalRange(int configured, int expected)
    {
        Assert.Equal(expected, AppConfig.NormalizeVideoCqp(configured));
    }

    [Fact]
    public void QualitySlider_MapsEndpointsToPracticalCqpRange()
    {
        var converter = new CqpToQualitySliderConverter();

        Assert.Equal(
            AppConfig.LowestQualityVideoCqp,
            converter.ConvertBack(0d, typeof(int), null!, CultureInfo.InvariantCulture));
        Assert.Equal(
            AppConfig.HighestQualityVideoCqp,
            converter.ConvertBack(100d, typeof(int), null!, CultureInfo.InvariantCulture));
        Assert.Equal(
            33.333,
            (double)converter.Convert(
                AppConfig.DefaultVideoCqp,
                typeof(double),
                null!,
                CultureInfo.InvariantCulture),
            3);
    }

    [Fact]
    public void CreateDefaultStorageLocations_UsesEveryReadyFixedDriveInDescendingDriveLetterOrder()
    {
        var drives = new[]
        {
            new StorageDriveCandidate(@"E:\", true, DriveType.Fixed),
            new StorageDriveCandidate(@"C:\", true, DriveType.Fixed),
            new StorageDriveCandidate(@"D:\", true, DriveType.Fixed),
            new StorageDriveCandidate(@"F:\", true, DriveType.Removable),
            new StorageDriveCandidate(@"G:\", false, DriveType.Fixed)
        };

        List<StorageLocation> locations = AppConfig.CreateDefaultStorageLocations(drives);

        Assert.Equal(
            [@"E:\快递打包视频", @"D:\快递打包视频", @"C:\快递打包视频"],
            locations.Select(location => location.Path));
        Assert.Equal([0, 1, 2], locations.Select(location => location.Priority));
    }

    [Fact]
    public void CreateDefaultStorageLocations_FallsBackToSystemDrive()
    {
        var drives = new[]
        {
            new StorageDriveCandidate(@"C:\", true, DriveType.Fixed),
            new StorageDriveCandidate(@"D:\", false, DriveType.Fixed)
        };

        StorageLocation location = Assert.Single(AppConfig.CreateDefaultStorageLocations(drives));

        Assert.Equal(@"C:\快递打包视频", location.Path);
        Assert.Equal(0, location.Priority);
    }

    [Fact]
    public void NormalizeAfterLoad_PreservesExistingStorageLocations()
    {
        var config = new AppConfig
        {
            StorageLocations =
            [
                new StorageLocation { Path = @"Z:\自定义录像", ReserveGB = 25, Priority = 0 }
            ]
        };

        AppConfig.NormalizeAfterLoad(config);

        StorageLocation location = Assert.Single(config.StorageLocations);
        Assert.Equal(@"Z:\自定义录像", location.Path);
    }

    [Fact]
    public void CreateDefaultStorageLocations_SkipsReadOnlySystemRoot()
    {
        // macOS 的 "/" 是只读系统卷，在它下面拼出来的默认位置永远建不出来：
        // 该根必须被跳过，改用其它本地盘
        var drives = new[]
        {
            new StorageDriveCandidate(@"C:\", true, DriveType.Fixed),
            new StorageDriveCandidate(@"D:\", true, DriveType.Fixed)
        };

        StorageLocation location = Assert.Single(
            AppConfig.CreateDefaultStorageLocations(drives, readOnlySystemRoot: @"C:\"));

        Assert.Equal(@"D:\快递打包视频", location.Path);
        Assert.Equal(0, location.Priority);
    }

    [Fact]
    public void CreateDefaultStorageLocations_WithoutCandidatesUsesPlatformFallback()
    {
        // 可用本地盘都被排除时用兜底根，而不是那条永远建不出来的 /快递打包视频
        string fallbackRoot = Path.Combine(Path.GetTempPath(), "mac-default") + Path.DirectorySeparatorChar;
        StorageLocation location = Assert.Single(
            AppConfig.CreateDefaultStorageLocations(
                Array.Empty<StorageDriveCandidate>(),
                readOnlySystemRoot: "/",
                fallbackRoot: fallbackRoot));

        Assert.Equal(Path.Combine(fallbackRoot, "快递打包视频"), location.Path);
        Assert.Equal(0, location.Priority);
    }

    [Fact]
    public void RemoveUnusableStorageLocations_DropsOnlyDirectChildrenOfReadOnlyRoot()
    {
        var locations = new List<StorageLocation>
        {
            new() { Path = "/快递打包视频", Priority = 0 },
            new() { Path = "/Volumes/外接盘/快递打包视频", Priority = 1 }
        };

        Assert.True(AppConfig.RemoveUnusableStorageLocations(locations, readOnlySystemRoot: "/"));

        StorageLocation remaining = Assert.Single(locations);
        Assert.Equal("/Volumes/外接盘/快递打包视频", remaining.Path);
        Assert.Equal(1, remaining.Priority);
    }

    [Fact]
    public void RemoveUnusableStorageLocations_DoesNothingOnWindowsConfigs()
    {
        var locations = new List<StorageLocation>
        {
            new() { Path = @"D:\快递打包视频", Priority = 0 }
        };

        Assert.False(AppConfig.RemoveUnusableStorageLocations(locations, readOnlySystemRoot: null));
        Assert.Single(locations);
    }

    [Fact]
    public void NormalizeAfterLoad_ClearsLegacySpaceLimitReserve()
    {
        var config = new AppConfig
        {
            StorageLocations =
            [
                new StorageLocation { Path = @"D:\快递打包视频", ReserveGB = 129, Priority = 0 },
                new StorageLocation
                {
                    Path = @"\\192.168.1.249\打包视频",
                    ReserveGB = 37,
                    Priority = 1,
                    IsBackupTarget = true
                }
            ]
        };

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal(
            AppConfig.CurrentStorageReserveSchemaVersion,
            config.StorageReserveSchemaVersion);
        Assert.All(config.StorageLocations, location => Assert.Equal(0, location.ReserveGB));
    }

    [Fact]
    public void NormalizeAfterLoad_KeepsReserveSetAfterMigration()
    {
        // 迁移只清历史值一次；迁移之后用户（或支持）在右键里设过的预留要保留
        string directory = Path.Combine(Path.GetTempPath(), "epm-reserve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var config = new AppConfig
            {
                StorageReserveSchemaVersion = AppConfig.CurrentStorageReserveSchemaVersion,
                StorageLocations =
                [
                    new StorageLocation { Path = directory, ReserveGB = 12, Priority = 0 }
                ]
            };

            AppConfig.NormalizeAfterLoad(config);

            Assert.Equal(12, config.StorageLocations[0].ReserveGB);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ResolveStartupExecutable_PrefersRootLauncherForCleanPackage()
    {
        string processPath = @"D:\Package\app\ExpressPackingMonitoring.exe";
        string launcherPath = @"D:\Package\ExpressPackingMonitoring.exe";

        string result = AutoStartService.ResolveStartupExecutable(
            processPath,
            @"D:\Package\app\",
            path => string.Equals(path, launcherPath, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(launcherPath, result);
    }

    [Fact]
    public void ResolveStartupExecutable_FallsBackToCurrentProcess()
    {
        string processPath = @"D:\Source\bin\ExpressPackingMonitoring.exe";

        string result = AutoStartService.ResolveStartupExecutable(
            processPath,
            @"D:\Source\bin\",
            path => string.Equals(path, processPath, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(processPath, result);
    }
}
