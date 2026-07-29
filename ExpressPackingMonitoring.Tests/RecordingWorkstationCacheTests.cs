using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class RecordingWorkstationCacheTests
{
    private const long GiB = StorageSpacePolicy.BytesPerGiB;

    [Fact]
    public void SelectPreferredDrive_FiltersUnsafeCandidatesAndUsesLargestSafeNonSystemDrive()
    {
        RecordingCacheDriveCandidate[] candidates =
        [
            Candidate(@"C:\", isSystem: true, totalGb: 500, availableGb: 300),
            Candidate(@"D:\", isSystem: false, totalGb: 200, availableGb: 100),
            Candidate(@"E:\", isSystem: false, totalGb: 400, availableGb: 170),
            Candidate(@"F:\", isSystem: false, totalGb: 1000, availableGb: 900, driveType: DriveType.Network),
            Candidate(@"G:\", isSystem: false, totalGb: 1000, availableGb: 900, driveType: DriveType.Removable),
            Candidate(@"H:\", isSystem: false, totalGb: 1000, availableGb: 900, isReady: false),
            Candidate(@"I:\", isSystem: false, totalGb: 1000, availableGb: 900, isWritable: false)
        ];

        RecordingCacheDriveCandidate selected =
            RecordingWorkstationCachePolicy.SelectPreferredDrive(candidates)!.Value;

        Assert.Equal(@"E:\", selected.RootPath);
    }

    [Fact]
    public void SelectPreferredDrive_FallsBackToSystemDriveWhenNoNonSystemDriveCanFitRecording()
    {
        RecordingCacheDriveCandidate[] candidates =
        [
            Candidate(@"C:\", isSystem: true, totalGb: 500, availableGb: 100),
            Candidate(@"D:\", isSystem: false, totalGb: 100, availableGb: 5),
            Candidate(@"E:\", isSystem: false, totalGb: 100, availableGb: 40, isWritable: false)
        ];

        RecordingCacheDriveCandidate selected =
            RecordingWorkstationCachePolicy.SelectPreferredDrive(candidates)!.Value;

        Assert.Equal(@"C:\", selected.RootPath);
    }

    [Fact]
    public void SelectPreferredDrive_ReturnsNoneWhenNoFixedDriveHasPackagingHeadroom()
    {
        RecordingCacheDriveCandidate[] candidates =
        [
            Candidate(@"C:\", isSystem: true, totalGb: 100, availableGb: 31),
            Candidate(@"D:\", isSystem: false, totalGb: 100, availableGb: 21)
        ];

        Assert.Null(RecordingWorkstationCachePolicy.SelectPreferredDrive(candidates));
    }

    [Fact]
    public void CalculateSpace_TreatsOneHundredGbAsLimitWithoutPreallocatingDisk()
    {
        RecordingCacheSpaceSnapshot snapshot =
            RecordingWorkstationCachePolicy.CalculateSpace(
                cacheBytes: 10 * GiB,
                configuredLimitBytes: 100 * GiB,
                availableBytes: 50 * GiB,
                reserveBytes: 20 * GiB);

        Assert.Equal(40 * GiB, snapshot.SafeCapacityBytes);
        Assert.Equal(40 * GiB, snapshot.EffectiveLimitBytes);
        Assert.Equal(30 * GiB, snapshot.RemainingBytes);
    }

    [Fact]
    public void CalculateSpace_KeepsConfiguredLimitWhenDiskCanSafelySupportIt()
    {
        RecordingCacheSpaceSnapshot snapshot =
            RecordingWorkstationCachePolicy.CalculateSpace(
                cacheBytes: 20 * GiB,
                configuredLimitBytes: 100 * GiB,
                availableBytes: 200 * GiB,
                reserveBytes: 20 * GiB);

        Assert.Equal(100 * GiB, snapshot.EffectiveLimitBytes);
        Assert.Equal(80 * GiB, snapshot.RemainingBytes);
    }

    [Fact]
    public void CreateCleanupPlan_DeletesOldestVerifiedFilesToWideLowWatermark()
    {
        DateTime now = new(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);
        RecordingCacheSpaceSnapshot snapshot =
            RecordingWorkstationCachePolicy.CalculateSpace(
                cacheBytes: 95 * GiB,
                configuredLimitBytes: 100 * GiB,
                availableBytes: 50 * GiB,
                reserveBytes: 20 * GiB);
        RecordingCacheCleanupItem[] verified =
        [
            new(3, now.AddHours(-1), 10 * GiB),
            new(1, now.AddHours(-3), 10 * GiB),
            new(2, now.AddHours(-2), 10 * GiB),
            new(4, now, 10 * GiB)
        ];

        RecordingCacheCleanupPlan plan =
            RecordingWorkstationCachePolicy.CreateCleanupPlan(
                snapshot,
                2 * GiB,
                verified);

        Assert.Equal([1L, 2L, 3L], plan.ItemIds);
        Assert.Equal(70 * GiB, plan.TargetCacheBytes);
        Assert.Equal(65 * GiB, plan.ProjectedCacheBytes);
    }

    [Fact]
    public void CreateCleanupPlan_LeavesShortfallWhenVerifiedFilesCannotReleaseEnough()
    {
        DateTime now = new(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);
        RecordingCacheSpaceSnapshot snapshot =
            RecordingWorkstationCachePolicy.CalculateSpace(
                cacheBytes: 99 * GiB,
                configuredLimitBytes: 100 * GiB,
                availableBytes: 21 * GiB,
                reserveBytes: 20 * GiB);

        RecordingCacheCleanupPlan plan =
            RecordingWorkstationCachePolicy.CreateCleanupPlan(
                snapshot,
                2 * GiB,
                [new(7, now.AddHours(-1), GiB / 2)]);

        Assert.Equal([7L], plan.ItemIds);
        Assert.Equal(70 * GiB, plan.TargetCacheBytes);
        Assert.True(plan.ProjectedCacheBytes > plan.TargetCacheBytes);
    }

    [Fact]
    public void ConfigureInitialLocation_PreservesExistingUserPath()
    {
        var config = new AppConfig
        {
            DeploymentPreset = DeploymentPresets.RecordingWorkstation,
            StorageLocations =
            [
                new StorageLocation
                {
                    Path = @"Z:\用户选择的缓存",
                    Priority = 0
                }
            ]
        };

        bool changed = RecordingWorkstationCachePolicy.ConfigureInitialLocation(
            config,
            preserveExistingLocation: true);

        Assert.False(changed);
        Assert.Equal(
            @"Z:\用户选择的缓存",
            Assert.Single(config.StorageLocations).Path);
    }

    [Fact]
    public void RecordingWorkstationCacheDefaults_ToOneHundredGbWithoutChangingExplicitLimit()
    {
        var defaults = new AppConfig();
        Assert.Equal("KeepWithinSize", defaults.RecordingCachePolicy);
        Assert.Equal(100, defaults.RecordingCacheMaxGB);

        var configured = new AppConfig
        {
            DeploymentPreset = DeploymentPresets.RecordingWorkstation,
            DeploymentSchemaVersion = DeploymentPresets.CurrentSchemaVersion,
            RecordingCacheMaxGB = 40
        };

        AppConfig.NormalizeAfterLoad(configured);

        Assert.Equal(40, configured.RecordingCacheMaxGB);
    }

    [Theory]
    [InlineData(DeploymentPresets.RecordingWorkstation, "KeepDays", "KeepWithinSize")]
    [InlineData(DeploymentPresets.RecordingHost, "KeepDays", "KeepDays")]
    [InlineData(DeploymentPresets.MobileBackupHost, "DeleteImmediately", "DeleteImmediately")]
    public void NormalizeAfterLoad_IsolatesCachePolicyByRole(
        string preset,
        string configuredPolicy,
        string expectedPolicy)
    {
        var config = new AppConfig
        {
            DeploymentPreset = preset,
            DeploymentSchemaVersion = DeploymentPresets.CurrentSchemaVersion,
            RecordingCachePolicy = configuredPolicy
        };

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal(expectedPolicy, config.RecordingCachePolicy);
    }

    [Fact]
    public void RecordingWorkstationCacheUi_ShowsOneLocationAndOneLimitWithoutRetentionControls()
    {
        string settings = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "SettingsWindow.xaml");
        int cacheTabStart = settings.IndexOf(
            "<TabItem x:Name=\"RecordingCacheTabItem\"",
            StringComparison.Ordinal);
        int cacheTabEnd = settings.IndexOf(
            "<TabItem Header=\"存储管理\"",
            cacheTabStart,
            StringComparison.Ordinal);
        string cacheTab = settings[cacheTabStart..cacheTabEnd];

        Assert.Contains("录像会先保存在本机", cacheTab, StringComparison.Ordinal);
        Assert.Contains("本地缓存位置", cacheTab, StringComparison.Ordinal);
        Assert.Contains("缓存最多占用", cacheTab, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding Config.RecordingCacheMaxGB, Mode=TwoWay}\"", cacheTab, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding CurrentDiskUsagePercent, Mode=OneWay}\"", cacheTab, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding CurrentDiskUsageText, Mode=OneWay}\"", cacheTab, StringComparison.Ordinal);
        Assert.Contains("Capabilities.CanConfigureRecordingCache, Mode=OneWay", cacheTab, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RecordingCacheTabItem\"", cacheTab, StringComparison.Ordinal);
        Assert.DoesNotContain("保留天数", cacheTab, StringComparison.Ordinal);
        Assert.DoesNotContain("立即删除", cacheTab, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordingCachePolicy", cacheTab, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordingCacheKeepDays", cacheTab, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingWorkstationCacheFlow_CleansVerifiedFilesBeforeBlockingOrStopping()
    {
        string recording = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Recording.cs");
        string cleanup = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Cleanup.cs");
        string transfer = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Transfer.cs");

        int preflightMaintenance = recording.IndexOf(
            "RunRecordingCacheMaintenance(",
            recording.IndexOf("EnsureRecordingCacheSpaceForNewRecordingAsync", StringComparison.Ordinal),
            StringComparison.Ordinal);
        int preflightDialog = recording.IndexOf(
            "AppDialog.Confirm(",
            preflightMaintenance,
            StringComparison.Ordinal);
        Assert.True(preflightMaintenance >= 0 && preflightMaintenance < preflightDialog);

        int runtimeMaintenance = cleanup.IndexOf(
            "RunRecordingCacheMaintenance(",
            StringComparison.Ordinal);
        int runtimeStop = cleanup.IndexOf(
            "QueueRecordingCacheEmergencyStop();",
            runtimeMaintenance,
            StringComparison.Ordinal);
        Assert.True(runtimeMaintenance >= 0 && runtimeMaintenance < runtimeStop);

        Assert.Contains("_recordingTransferStore.GetUploadedWithLocalCache()", transfer, StringComparison.Ordinal);
        Assert.Contains("task.RemoteVideoRecordId is not long remoteRecordId", transfer, StringComparison.Ordinal);
        Assert.Contains(".OrderBy(item => item.CreatedAt)", ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Services",
            "RecordingWorkstationCachePolicy.cs"), StringComparison.Ordinal);
        Assert.Contains("if (IsRecordingWorkstation)", cleanup, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingWorkstationCacheLocationPicker_AllowsOnlyFixedDrives()
    {
        string settingsSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "SettingsWindow.xaml.cs");

        Assert.Contains("new DriveSelectionDialog(", settingsSource, StringComparison.Ordinal);
        Assert.Contains("fixedDrivesOnly: true", settingsSource, StringComparison.Ordinal);
        Assert.Contains(
            "if (!drive.IsReady || drive.DriveType != DriveType.Fixed)",
            settingsSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CacheSpacePrompt_OpensTheCacheSettingsTabDirectly()
    {
        string recording = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Recording.cs");
        string mainViewModel = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.cs");
        string settings = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "SettingsWindow.xaml.cs");

        Assert.Contains("OpenSettings(selectRecordingCache: true);", recording, StringComparison.Ordinal);
        Assert.Contains("settingsWin.SelectRecordingCacheTab();", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("SettingsTabControl.SelectedItem = RecordingCacheTabItem;", settings, StringComparison.Ordinal);
    }

    private static RecordingCacheDriveCandidate Candidate(
        string root,
        bool isSystem,
        int totalGb,
        int availableGb,
        DriveType driveType = DriveType.Fixed,
        bool isReady = true,
        bool isWritable = true) =>
        new(
            root,
            Path.Combine(root, "快递打包视频"),
            isReady,
            driveType,
            isWritable,
            isSystem,
            totalGb * GiB,
            availableGb * GiB);

    private static string ReadRepositoryFile(params string[] relativeParts)
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            Path.Combine(relativeParts));
        return File.ReadAllText(path, Encoding.UTF8);
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (File.Exists(Path.Combine(directory, "ExpressPackingMonitoring.sln")))
                return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }
        throw new DirectoryNotFoundException("未找到仓库根目录");
    }
}
