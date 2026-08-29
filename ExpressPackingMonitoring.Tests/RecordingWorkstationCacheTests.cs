using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;
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
    public void CalculateSpace_SeparatesConfiguredLimitFromPhysicalEmergencyHeadroom()
    {
        RecordingCacheSpaceSnapshot snapshot =
            RecordingWorkstationCachePolicy.CalculateSpace(
                cacheBytes: 99 * GiB,
                configuredLimitBytes: 100 * GiB,
                availableBytes: 100 * GiB,
                reserveBytes: 20 * GiB);

        Assert.Equal(GiB, snapshot.RemainingBytes);
        Assert.Equal(80 * GiB, snapshot.PhysicalRemainingBytes);
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

    [Theory]
    [InlineData(50, 100, 50, 20, false)]
    [InlineData(79, 100, 50, 20, false)]
    [InlineData(80, 100, 50, 20, true)]
    [InlineData(90, 100, 50, 20, true)]
    [InlineData(99, 100, 21, 20, true)]
    public void RequiresCleanup_OnlyEntersVerifiedFileScanWhenNeeded(
        int cacheGb,
        int limitGb,
        int availableGb,
        int reserveGb,
        bool expected)
    {
        RecordingCacheSpaceSnapshot snapshot =
            RecordingWorkstationCachePolicy.CalculateSpace(
                cacheGb * GiB,
                limitGb * GiB,
                availableGb * GiB,
                reserveGb * GiB);

        Assert.Equal(
            expected,
            RecordingWorkstationCachePolicy.RequiresCleanup(snapshot, 2 * GiB));
    }

    [Theory]
    [InlineData(22, 20, 2, true)]
    [InlineData(21, 20, 2, false)]
    [InlineData(1, 20, 2, false)]
    public void PhysicalHeadroomGate_UsesOnlyDriveSpaceAndReserve(
        int availableGb,
        int reserveGb,
        int requiredGb,
        bool expected)
    {
        Assert.Equal(
            expected,
            RecordingWorkstationCachePolicy.HasRequiredPhysicalHeadroom(
                availableGb * GiB,
                reserveGb * GiB,
                requiredGb * GiB));
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
            "<TabItem Header=\"录像设置\"",
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
        string recording = RepositorySource.ReadMainViewModelParts(
            "Recording",
            "Ffmpeg",
            "Audio",
            "Conversion");
        string cleanup = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Cleanup.cs");
        string transfer = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Transfer.cs");

        int preflightGate = recording.IndexOf(
            "EnsureRecordingStorageHeadroomForNewRecording", StringComparison.Ordinal);
        int preflightDialog = recording.IndexOf(
            "AppDialog.Confirm(",
            preflightGate,
            StringComparison.Ordinal);
        Assert.True(preflightGate >= 0 && preflightGate < preflightDialog);
        Assert.Contains(
            "RecordingWorkstationCachePolicy.HasRequiredPhysicalHeadroom(",
            recording,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RunRecordingCacheMaintenance(",
            recording,
            StringComparison.Ordinal);

        int runtimeMaintenance = cleanup.IndexOf(
            "RunRecordingCacheMaintenance(",
            StringComparison.Ordinal);
        int runtimeStop = cleanup.IndexOf(
            "QueueRecordingCacheEmergencyStop();",
            runtimeMaintenance,
            StringComparison.Ordinal);
        Assert.True(runtimeMaintenance >= 0 && runtimeMaintenance < runtimeStop);
        Assert.Contains("result.Snapshot.PhysicalRemainingBytes", cleanup, StringComparison.Ordinal);

        Assert.Contains("GetCacheCleanupCandidateBatch(", transfer, StringComparison.Ordinal);
        Assert.Contains("RecordingCacheCleanupBatchSize", transfer, StringComparison.Ordinal);
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
        string recording = RepositorySource.ReadMainViewModelParts(
            "Recording",
            "Ffmpeg",
            "Audio",
            "Conversion");
        string mainViewModel = RepositorySource.ReadMainViewModel();
        string settings = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "SettingsWindow.xaml.cs");

        Assert.Contains("OpenSettings(selectRecordingCache: true);", recording, StringComparison.Ordinal);
        Assert.Contains("settingsWin.SelectRecordingCacheTab();", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("SettingsTabControl.SelectedItem = RecordingCacheTabItem;", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalVideoInventory_TracksFinalPathAndSizeWithoutDirectoryScanning()
    {
        string root = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(root, "videos.db");
            string mkvPath = Path.Combine(root, "recording.mkv");
            string mp4Path = Path.Combine(root, "recording.mp4");
            File.WriteAllBytes(mkvPath, new byte[128]);
            using var database = new VideoDatabase(databasePath);
            long recordId = database.InsertVideoRecord(
                "ORDER-1",
                "发货",
                "",
                "",
                mkvPath,
                DateTime.Now.AddMinutes(-1));

            database.UpdateVideoRecordOnStop(
                recordId,
                DateTime.Now,
                60,
                128,
                "手动");

            StorageVideoFile initial = Assert.Single(database.GetLocalVideoFileInventory());
            Assert.Equal(Path.GetFullPath(mkvPath), initial.FilePath);
            Assert.Equal(128, initial.FileSizeBytes);

            File.WriteAllBytes(mp4Path, new byte[64]);
            database.UpdateVideoFilePath(mkvPath, mp4Path);

            StorageVideoFile converted = Assert.Single(database.GetLocalVideoFileInventory());
            Assert.Equal(Path.GetFullPath(mp4Path), converted.FilePath);
            Assert.Equal(64, converted.FileSizeBytes);
            Assert.Equal(64, database.GetVideoById(recordId).FileSizeBytes);
            database.Dispose();
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public void LocalVideoInventory_ReconciliationCountsUnknownFilesAndPreservesOtherRoots()
    {
        string root = CreateTempDirectory();
        string otherRoot = CreateTempDirectory();
        try
        {
            using var database = new VideoDatabase(Path.Combine(root, "videos.db"));
            string unknownPath = Path.Combine(root, "unknown.mp4");
            string stalePath = Path.Combine(root, "stale.mp4");
            string otherPath = Path.Combine(otherRoot, "other.mp4");
            database.ReplaceLocalVideoFileInventory(
                root,
                [new StorageVideoFile { FilePath = stalePath, FileSizeBytes = 20 }]);
            database.ReplaceLocalVideoFileInventory(
                otherRoot,
                [new StorageVideoFile { FilePath = otherPath, FileSizeBytes = 30 }]);

            database.ReplaceLocalVideoFileInventory(
                root,
                [new StorageVideoFile { FilePath = unknownPath, FileSizeBytes = 40 }]);

            StorageVideoFile[] files = database.GetLocalVideoFileInventory().ToArray();
            Assert.DoesNotContain(files, file =>
                string.Equals(file.FilePath, stalePath, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(files, file =>
                string.Equals(file.FilePath, unknownPath, StringComparison.OrdinalIgnoreCase)
                && file.FileSizeBytes == 40);
            Assert.Contains(files, file =>
                string.Equals(file.FilePath, otherPath, StringComparison.OrdinalIgnoreCase)
                && file.FileSizeBytes == 30);
            Assert.Equal(40, database.GetLocalVideoFileInventoryBytes(root));
            Assert.Equal(30, database.GetLocalVideoFileInventoryBytes(otherRoot));
            database.Dispose();
        }
        finally
        {
            DeleteTempDirectory(root);
            DeleteTempDirectory(otherRoot);
        }
    }

    [Fact]
    public void CacheDeletionRequiresEveryRecordSharingTheFileToBeVerified()
    {
        string root = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(root, "videos.db");
            string videoPath = Path.Combine(root, "shared.mp4");
            File.WriteAllBytes(videoPath, new byte[64]);
            using var database = new VideoDatabase(databasePath);
            using var queue = new RecordingTransferQueueStore(databasePath);
            long firstId = database.InsertVideoRecord(
                "ORDER-1", "发货", "", "", videoPath, DateTime.Now.AddMinutes(-2));
            long secondId = database.InsertVideoRecord(
                "ORDER-2", "发货", "", "", videoPath, DateTime.Now.AddMinutes(-1));
            database.UpdateVideoRecordOnStop(firstId, DateTime.Now, 60, 64, "手动");
            database.UpdateVideoRecordOnStop(secondId, DateTime.Now, 60, 64, "手动");
            queue.Enqueue(firstId, videoPath, "session-1", "host", "http://host", DateTime.UtcNow);
            queue.Enqueue(secondId, videoPath, "session-2", "host", "http://host", DateTime.UtcNow);
            RecordingTransferTask[] tasks = queue.GetReady(DateTime.UtcNow.AddMinutes(1), 10).ToArray();

            queue.MarkUploaded(tasks[0].Id, 101, DateTime.UtcNow);
            database.MarkVideoUploaded(firstId, 101);
            Assert.False(database.IsLocalVideoFileFullyVerifiedForCacheDeletion(videoPath));

            queue.MarkUploaded(tasks[1].Id, 102, DateTime.UtcNow);
            database.MarkVideoUploaded(secondId, 102);
            Assert.True(database.IsLocalVideoFileFullyVerifiedForCacheDeletion(videoPath));
            queue.Dispose();
            database.Dispose();
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public void RecordingWorkstationHotMaintenance_UsesDatabaseInventoryAndOnlyReconcilesOnDemand()
    {
        string transfer = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Transfer.cs");

        Assert.Contains("GetLocalVideoFileInventoryBytes(cachePath)", transfer, StringComparison.Ordinal);
        Assert.Contains("ReconcileRecordingCacheInventory(cachePath)", transfer, StringComparison.Ordinal);
        Assert.Contains("forceReconcile", transfer, StringComparison.Ordinal);
        Assert.DoesNotContain("GetVideoBytes(cachePath)", transfer, StringComparison.Ordinal);
        Assert.DoesNotContain("GetUploadedWithLocalCache()", transfer, StringComparison.Ordinal);
        Assert.Contains("GetCacheCleanupCandidateBatch(", transfer, StringComparison.Ordinal);
        Assert.Contains("RecordingCacheCleanupBatchSize = 32", transfer, StringComparison.Ordinal);
        Assert.Contains("RecordingCacheReconcileInterval", transfer, StringComparison.Ordinal);
        Assert.Contains("Cache inventory reused from database", transfer, StringComparison.Ordinal);
        Assert.Contains(
            "_db.IsLocalVideoFileFullyVerifiedForCacheDeletion(",
            transfer,
            StringComparison.Ordinal);
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

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"RecordingWorkstationCacheTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        SqliteTestPool.ClearPoolFor(path);
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
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
