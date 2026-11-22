using System.Net;
using System.Globalization;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class TransferManagementSnapshotTests
{
    [Fact]
    public void TransferPopup_HidesHealthySummaryAndKeepsPauseActionInFooterModel()
    {
        using var database = new VideoDatabase(":memory:");
        var service = new TransferManagementSnapshotService(
            TransferManagementMode.RecordingTransfer,
            new AppConfig(),
            database,
            webServer: null,
            queueStore: null,
            transferService: null);
        using var viewModel = new TransferManagementWindowViewModel(
            service,
            isUploadPaused: () => false,
            toggleUploadPause: () => Task.FromResult(true));

        viewModel.ApplySnapshotForPreview(
            TransferManagementSnapshot.Empty()
            with
            {
                HostState = HostConnectionState.Online,
                TodayReceivedCount = 37
            });

        Assert.Equal("", viewModel.SummaryText);
        Assert.False(viewModel.HasDetailSummary);
        Assert.Equal("37", viewModel.TodayReceivedCountText);
        Assert.False(viewModel.HasRemainingUpload);
        Assert.True(viewModel.IsUploadPauseVisible);
        Assert.Equal("暂停上传", viewModel.UploadPauseButtonText);

        viewModel.ApplySnapshotForPreview(
            TransferManagementSnapshot.Empty()
            with
            {
                HostState = HostConnectionState.PausedByUser,
                PendingCount = 3,
                PausedUpload = new TransferTaskRow(
                    1,
                    "PAUSED-ORDER",
                    1000,
                    160,
                    "")
            });

        Assert.Equal("", viewModel.SummaryText);
        Assert.False(viewModel.HasDetailSummary);
        Assert.Equal("继续上传", viewModel.UploadPauseButtonText);
        Assert.Equal("已暂停", viewModel.UploadSlot?.StatusText);
        Assert.True(viewModel.HasRemainingUpload);
        Assert.Equal("还有 3 个待上传", viewModel.RemainingUploadText);

        viewModel.ApplySnapshotForPreview(
            TransferManagementSnapshot.Empty()
            with
            {
                HostState = HostConnectionState.StorageBlocked,
                NeedsActionCount = 2,
                FailurePreview =
                [
                    new TransferTaskRow(2, "FAILED-ORDER", 2048, 0, "备份主机空间不足")
                ]
            });

        Assert.Equal("2 个异常", viewModel.SummaryText);
        Assert.True(viewModel.IsAttentionSummary);
        Assert.False(viewModel.HasDetailSummary);
    }

    [Theory]
    [InlineData(nameof(HostConnectionState.ClientUpdateRequired), "当前电脑版本过低，请更新后再备份")]
    [InlineData(nameof(HostConnectionState.HostUpdateRequired), "备份主机版本过低，请先在主机上更新后继续备份")]
    public void PreparedUpgradeStatesUseTransferPopupStatusStrip(
        string stateValue,
        string expected)
    {
        HostConnectionState state = Enum.Parse<HostConnectionState>(stateValue);
        using var database = new VideoDatabase(":memory:");
        var service = new TransferManagementSnapshotService(
            TransferManagementMode.RecordingTransfer,
            new AppConfig(),
            database,
            webServer: null,
            queueStore: null,
            transferService: null);
        using var viewModel = new TransferManagementWindowViewModel(service);

        viewModel.ApplySnapshotForPreview(
            TransferManagementSnapshot.Empty()
            with
            {
                HostState = state
            });

        Assert.True(viewModel.HasDetailSummary);
        Assert.Equal(expected, viewModel.StopReasonText);
    }

    [Theory]
    [InlineData(nameof(HostConnectionState.ClientUpdateRequired))]
    [InlineData(nameof(HostConnectionState.HostUpdateRequired))]
    public void UpgradeStatesStillShowTheNextUploadSlot(
        string stateValue)
    {
        HostConnectionState state = Enum.Parse<HostConnectionState>(stateValue);
        using var database = new VideoDatabase(":memory:");
        var service = new TransferManagementSnapshotService(
            TransferManagementMode.RecordingTransfer,
            new AppConfig(),
            database,
            webServer: null,
            queueStore: null,
            transferService: null);
        using var viewModel = new TransferManagementWindowViewModel(service);

        viewModel.ApplySnapshotForPreview(
            TransferManagementSnapshot.Empty()
            with
            {
                HostState = state,
                PendingCount = 24,
                UpcomingUpload = new TransferTaskRow(
                    8,
                    "AUTO_WEB_0145",
                    328 * 1024 * 1024,
                    0,
                    "")
            });

        UploadSlotItem slot = Assert.IsType<UploadSlotItem>(viewModel.UploadSlot);
        Assert.StartsWith(
            "AUTO_WEB_0145",
            slot.SummaryText,
            StringComparison.Ordinal);
        Assert.Equal("等待更新", slot.StatusText);
        Assert.Equal("还有 24 个待上传", viewModel.RemainingUploadText);
    }

    [Theory]
    [InlineData(
        RecordingTransferFailureKinds.ClientUpgradeRequired,
        nameof(HostConnectionState.ClientUpdateRequired))]
    [InlineData(
        RecordingTransferFailureKinds.HostUpgradeRequired,
        nameof(HostConnectionState.HostUpdateRequired))]
    public async Task PersistedUpgradeFailureRestoresDirectionalHostState(
        string failureKind,
        string expectedState)
    {
        string directory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "videos.db");
            using var database = new VideoDatabase(databasePath);
            long recordId = InsertRecording(
                database,
                directory,
                "UPGRADE-STATE",
                128);
            using var store = new RecordingTransferQueueStore(databasePath);
            string targetNodeId = Guid.NewGuid().ToString("D");
            Assert.True(store.Enqueue(
                recordId,
                Path.Combine(directory, "video-UPGRADE-STATE.mp4"),
                Guid.NewGuid().ToString("D"),
                targetNodeId,
                "http://127.0.0.1:5280",
                DateTime.UtcNow));

            RecordingTransferTask task =
                Assert.Single(store.GetReady(DateTime.UtcNow));
            store.DeferForHost(
                task.Id,
                "版本需要更新",
                failureKind,
                DateTime.UtcNow);

            var service = new TransferManagementSnapshotService(
                TransferManagementMode.RecordingTransfer,
                new AppConfig(),
                database,
                webServer: null,
                queueStore: store,
                transferService: null,
                hostHealth: () => null);
            TransferManagementSnapshot snapshot =
                await service.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Equal(
                Enum.Parse<HostConnectionState>(expectedState),
                snapshot.HostState);
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void HostPopupHeaderShowsTodayReceivedCount()
    {
        using var database = new VideoDatabase(":memory:");
        var service = new TransferManagementSnapshotService(
            TransferManagementMode.HostDevices,
            new AppConfig(),
            database,
            webServer: null,
            queueStore: null,
            transferService: null);
        using var viewModel = new TransferManagementWindowViewModel(service);

        viewModel.ApplySnapshotForPreview(
            TransferManagementSnapshot.Empty()
            with
            {
                HostState = HostConnectionState.Online,
                TodayReceivedCount = 38
            });

        Assert.Equal("今日已接收", viewModel.TodaySummaryLabel);
        Assert.Equal("38", viewModel.TodayReceivedCountText);
    }

    [Fact]
    public void PendingUploadsPreviewTheNextItemDuringCooldown()
    {
        using var database = new VideoDatabase(":memory:");
        var service = new TransferManagementSnapshotService(
            TransferManagementMode.RecordingTransfer,
            new AppConfig(),
            database,
            webServer: null,
            queueStore: null,
            transferService: null);
        using var viewModel = new TransferManagementWindowViewModel(
            service,
            isUploadPaused: () => false,
            toggleUploadPause: () => Task.FromResult(true));

        viewModel.ApplySnapshotForPreview(
            TransferManagementSnapshot.Empty()
            with
            {
                HostState = HostConnectionState.Online,
                PendingCount = 707,
                UpcomingUpload = new TransferTaskRow(
                    42,
                    "ORDER-NEXT",
                    1024,
                    0,
                    ""),
                NextUploadAt = DateTimeOffset.UtcNow.AddSeconds(15)
            });

        UploadSlotItem slot = Assert.IsType<UploadSlotItem>(viewModel.UploadSlot);
        Assert.Contains("秒后上传", slot.StatusText, StringComparison.Ordinal);
        Assert.Equal("ORDER-NEXT · 1.00 KB", slot.SummaryText);
        Assert.Equal(0, slot.Progress);
        Assert.True(viewModel.HasRemainingUpload);
        Assert.Equal("还有 707 个待上传", viewModel.RemainingUploadText);

        viewModel.ApplySnapshotForPreview(
            TransferManagementSnapshot.Empty()
            with
            {
                HostState = HostConnectionState.Unknown,
                PendingCount = 599,
                UpcomingUpload = new TransferTaskRow(
                    45,
                    "ORDER-CHECKING",
                    4096,
                    0,
                    "")
            });

        UploadSlotItem checkingSlot = Assert.IsType<UploadSlotItem>(
            viewModel.UploadSlot);
        Assert.Equal("正在检查主机", checkingSlot.StatusText);
        Assert.Equal("ORDER-CHECKING · 4.00 KB", checkingSlot.SummaryText);
        Assert.Equal(0, checkingSlot.Progress);
        Assert.False(viewModel.HasDetailSummary);
        Assert.True(viewModel.HasRemainingUpload);
        Assert.Equal("还有 599 个待上传", viewModel.RemainingUploadText);

        viewModel.ApplySnapshotForPreview(
            TransferManagementSnapshot.Empty()
            with
            {
                HostState = HostConnectionState.Offline,
                PendingCount = 707,
                UpcomingUpload = new TransferTaskRow(
                    42,
                    "ORDER-NEXT",
                    1024,
                    0,
                    "")
            });
        Assert.Null(viewModel.UploadSlot);
    }

    [Fact]
    public void PausedUploadKeepsThePartialTaskAndProgressVisible()
    {
        using var database = new VideoDatabase(":memory:");
        var service = new TransferManagementSnapshotService(
            TransferManagementMode.RecordingTransfer,
            new AppConfig(),
            database,
            webServer: null,
            queueStore: null,
            transferService: null);
        using var viewModel = new TransferManagementWindowViewModel(
            service,
            isUploadPaused: () => true,
            toggleUploadPause: () => Task.FromResult(true));

        viewModel.ApplySnapshotForPreview(
            TransferManagementSnapshot.Empty()
            with
            {
                HostState = HostConnectionState.PausedByUser,
                PendingCount = 1,
                PausedUpload = new TransferTaskRow(
                    43,
                    "ORDER-PAUSED",
                    1000,
                    160,
                    "")
            });

        UploadSlotItem slot = Assert.IsType<UploadSlotItem>(viewModel.UploadSlot);
        Assert.True(slot.IsPaused);
        Assert.Equal("已暂停", slot.StatusText);
        Assert.Equal("ORDER-PAUSED · 1000 B", slot.SummaryText);
        Assert.Equal(16, slot.Progress);

        viewModel.ApplySnapshotForPreview(
            TransferManagementSnapshot.Empty()
            with
            {
                HostState = HostConnectionState.PausedByUser,
                PendingCount = 1,
                UpcomingUpload = new TransferTaskRow(
                    44,
                    "ORDER-QUEUED",
                    2048,
                    0,
                    "")
            });

        UploadSlotItem queuedSlot = Assert.IsType<UploadSlotItem>(viewModel.UploadSlot);
        Assert.True(queuedSlot.IsPaused);
        Assert.Equal("已暂停", queuedSlot.StatusText);
        Assert.Equal("ORDER-QUEUED · 2.00 KB", queuedSlot.SummaryText);
        Assert.Equal(0, queuedSlot.Progress);
    }

    [Fact]
    public void ManagementQuery_UsesAggregatesAndBoundedActionRows()
    {
        string directory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "videos.db");
            using var database = new VideoDatabase(databasePath);
            long currentId = InsertRecording(database, directory, "CURRENT", 4096);
            long retryId = InsertRecording(database, directory, "RETRY", 2048);
            long abandonedId = InsertRecording(database, directory, "ABANDONED", 1024);
            using var store = new RecordingTransferQueueStore(databasePath);
            DateTime now = DateTime.UtcNow;
            foreach (long recordId in new[] { currentId, retryId, abandonedId })
            {
                Assert.True(store.Enqueue(
                    recordId,
                    Path.Combine(directory, $"video-{recordId}.mp4"),
                    $"session-{recordId}",
                    Guid.NewGuid().ToString("D"),
                    "http://127.0.0.1:5280",
                    now));
            }

            RecordingTransferTask[] tasks = store.GetReady(now).ToArray();
            RecordingTransferTask current = Assert.Single(
                tasks,
                task => task.LocalVideoRecordId == currentId);
            RecordingTransferTask retry = Assert.Single(
                tasks,
                task => task.LocalVideoRecordId == retryId);
            RecordingTransferTask abandoned = Assert.Single(
                tasks,
                task => task.LocalVideoRecordId == abandonedId);
            store.MarkUploading(current.Id, 1024, now);
            store.MarkFailed(
                retry.Id,
                1,
                "storage unavailable",
                now.AddSeconds(5),
                now,
                RecordingTransferFailureKinds.HostStorageUnavailable);
            store.MarkAbandoned(
                abandoned.Id,
                "session conflict",
                now);

            RecordingTransferManagementData data = store.GetManagementData(
                new DateTimeOffset(now.AddDays(-1)),
                new DateTimeOffset(now.AddDays(1)));

            Assert.Equal(0, data.PendingCount);
            Assert.Equal(1, data.RetryWaitCount);
            Assert.Equal(1, data.NeedsActionCount);
            Assert.NotNull(data.CurrentUpload);
            Assert.Equal("CURRENT", data.CurrentUpload.Identifier);
            Assert.Equal(4096, data.CurrentUpload.FileSizeBytes);
            Assert.Equal(1024, data.CurrentUpload.SentBytes);
            Assert.Single(data.FailurePreview);
            Assert.Equal("ABANDONED", data.FailurePreview[0].Identifier);
            Assert.Equal(
                RecordingTransferFailureKinds.HostStorageUnavailable,
                data.DominantFailureKind);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void HostWriteBusyDefersAsPendingInsteadOfFailure()
    {
        string directory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "videos.db");
            using var database = new VideoDatabase(databasePath);
            long recordId = InsertRecording(database, directory, "BUSY", 1024);
            using var store = new RecordingTransferQueueStore(databasePath);
            DateTime now = DateTime.UtcNow;
            Assert.True(store.Enqueue(
                recordId,
                Path.Combine(directory, "BUSY.mp4"),
                "session-busy",
                Guid.NewGuid().ToString("D"),
                "http://127.0.0.1:5280",
                now));
            RecordingTransferTask task = Assert.Single(store.GetReady(now));
            DateTime nextAttemptAt = now.AddSeconds(5);

            store.DeferForRetry(
                task.Id,
                "主机磁盘繁忙",
                RecordingTransferFailureKinds.HostWriteBusy,
                nextAttemptAt,
                now);

            RecordingTransferManagementData data = store.GetManagementData(
                new DateTimeOffset(now.AddDays(-1), TimeSpan.Zero),
                new DateTimeOffset(now.AddDays(1), TimeSpan.Zero));
            Assert.Equal(1, data.PendingCount);
            Assert.Equal(0, data.RetryWaitCount);
            Assert.Equal(0, data.NeedsActionCount);
            Assert.Empty(data.FailurePreview);
            Assert.Empty(store.GetReady(now.AddSeconds(1)));
            Assert.Single(store.GetReady(nextAttemptAt));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void ManagementQuery_ExposesTheNextPendingUploadDuringCooldown()
    {
        string directory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "videos.db");
            using var database = new VideoDatabase(databasePath);
            long firstId = InsertRecording(database, directory, "NEXT-ONE", 4096);
            long secondId = InsertRecording(database, directory, "NEXT-TWO", 8192);
            using var store = new RecordingTransferQueueStore(databasePath);
            DateTime now = DateTime.UtcNow;
            Assert.True(store.Enqueue(
                firstId,
                Path.Combine(directory, "next-one.mp4"),
                "next-one",
                Guid.NewGuid().ToString("D"),
                "http://127.0.0.1:5280",
                now));
            Assert.True(store.Enqueue(
                secondId,
                Path.Combine(directory, "next-two.mp4"),
                "next-two",
                Guid.NewGuid().ToString("D"),
                "http://127.0.0.1:5280",
                now.AddSeconds(1)));

            RecordingTransferManagementData data = store.GetManagementData(
                new DateTimeOffset(now.AddDays(-1)),
                new DateTimeOffset(now.AddDays(1)));

            Assert.Null(data.CurrentUpload);
            Assert.NotNull(data.NextPendingUpload);
            Assert.Equal("NEXT-ONE", data.NextPendingUpload.Identifier);
            Assert.Equal(4096, data.NextPendingUpload.FileSizeBytes);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void RetryTaskNow_OnlyRequeuesTheRequestedTask()
    {
        string directory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "videos.db");
            using var database = new VideoDatabase(databasePath);
            long firstRecordId = InsertRecording(database, directory, "FIRST", 128);
            long secondRecordId = InsertRecording(database, directory, "SECOND", 128);
            using var store = new RecordingTransferQueueStore(databasePath);
            DateTime now = DateTime.UtcNow;
            Assert.True(store.Enqueue(
                firstRecordId,
                Path.Combine(directory, "first.mp4"),
                "first",
                Guid.NewGuid().ToString("D"),
                "http://127.0.0.1:5280",
                now));
            Assert.True(store.Enqueue(
                secondRecordId,
                Path.Combine(directory, "second.mp4"),
                "second",
                Guid.NewGuid().ToString("D"),
                "http://127.0.0.1:5280",
                now));
            RecordingTransferTask[] tasks = store.GetReady(now).ToArray();
            RecordingTransferTask first = Assert.Single(
                tasks,
                task => task.LocalVideoRecordId == firstRecordId);
            RecordingTransferTask second = Assert.Single(
                tasks,
                task => task.LocalVideoRecordId == secondRecordId);
            store.MarkAbandoned(first.Id, "first failed", now);
            store.MarkAbandoned(second.Id, "second failed", now);

            store.RetryTaskNow(first.Id, now.AddSeconds(1));

            RecordingTransferTask[] ready = store.GetReady(now.AddSeconds(1)).ToArray();
            RecordingTransferTask recovered = Assert.Single(ready);
            Assert.Equal(firstRecordId, recovered.LocalVideoRecordId);
            Assert.Equal(1, store.GetSummary().AbandonedCount);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void HostApprovalBlockedTasksAreRestoredToPending()
    {
        string directory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "videos.db");
            using var database = new VideoDatabase(databasePath);
            long recordId = InsertRecording(database, directory, "APPROVAL", 128);
            using var store = new RecordingTransferQueueStore(databasePath);
            DateTime now = DateTime.UtcNow;
            Assert.True(store.Enqueue(
                recordId,
                Path.Combine(directory, "approval.mp4"),
                "approval-session",
                Guid.NewGuid().ToString("D"),
                "http://127.0.0.1:5280",
                now));
            RecordingTransferTask task = Assert.Single(store.GetReady(now));
            store.MarkAbandoned(
                task.Id,
                "备份主机尚未完成安全配对",
                now);

            int restored = store.RequeueHostApprovalBlocked(now.AddSeconds(1));

            Assert.Equal(1, restored);
            RecordingTransferSummary summary = store.GetSummary();
            Assert.Equal(1, summary.PendingCount);
            Assert.Equal(0, summary.AbandonedCount);
            Assert.Equal(0, summary.FailedCount);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void HostDeviceSummaries_IncludeTodayCountAndLatestActivity()
    {
        string directory = CreateTempDirectory();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            string firstPath = Path.Combine(directory, "phone-1.mp4");
            string secondPath = Path.Combine(directory, "phone-2.mp4");
            database.InsertMobileBackupRecord(
                "PHONE-1",
                firstPath,
                1024,
                DateTime.Now.AddMinutes(-8),
                30,
                "phone-1",
                "手机1",
                "session-1",
                "hash-1");
            database.InsertMobileBackupRecord(
                "PHONE-2",
                secondPath,
                2048,
                DateTime.Now.AddMinutes(-3),
                30,
                "phone-1",
                "手机1",
                "session-2",
                "hash-2");

            MobileBackupDeviceSummary summary = Assert.Single(
                database.GetMobileBackupDeviceSummaries(DateTime.Today));
            Assert.Equal("phone-1", summary.DeviceId);
            Assert.Equal("手机1", summary.DeviceName);
            Assert.Equal(2, summary.TodayCount);
            Assert.NotNull(summary.LastActivityAt);
            Assert.Equal(DateTime.Today, summary.LastActivityAt!.Value.Date);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void ForgetKnownBackupDevice_UnpairsAndRevokesCredential()
    {
        string directory = CreateTempDirectory();
        try
        {
            string stateDirectory = Path.Combine(directory, "state");
            Directory.CreateDirectory(stateDirectory);
            var receiverRegistry = new MobileOrderReceiverRegistry(
                Path.Combine(stateDirectory, "order-receivers.json"),
                () => DateTime.UtcNow.AddHours(-1));
            receiverRegistry.Register(
                IPAddress.Parse("192.168.31.201"),
                "recording-workstation-0001",
                "电脑2",
                deviceKind: "pc",
                platform: "windows",
                trustProvidedName: true);
            var credentials = new BackupPairingTokenService(
                stateDirectory,
                "");
            credentials.Enroll("recording-workstation-0001", "pc");

            using var database = new VideoDatabase(
                Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                mobileBackupStateDirectory: stateDirectory,
                nodeId: "host-node-0001",
                deploymentPreset: DeploymentPresets.MobileBackupHost);

            BackupDeviceRemovalResult result =
                server.TryForgetKnownBackupDevice(
                    "recording-workstation-0001");

            Assert.Equal(BackupDeviceRemovalStatus.Removed, result.Status);
            Assert.Empty(server.GetMobileBackupDeviceDirectory());
            Assert.Empty(server.GetRecordingDevices("http://192.168.31.10:5280"));
            Assert.Empty(database.GetVideoSources());
            Assert.Equal(
                "电脑2",
                server.GetCurrentSourceDeviceNames()[
                    "recording-workstation-0001"]);
            Assert.Contains(
                "recording-workstation-0001",
                new MobileOrderReceiverRegistry(
                    Path.Combine(stateDirectory, "order-receivers.json"))
                    .GetKnownRecordingDevices(includeRemoved: true)
                    .Select(device => device.NodeId));
            Assert.False(new BackupPairingTokenService(
                stateDirectory,
                "").TryGetDeviceCredential(
                    "recording-workstation-0001",
                    out _));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void ForgetKnownBackupDevice_KeepsHistoricalVideosAndTags()
    {
        string directory = CreateTempDirectory();
        try
        {
            string stateDirectory = Path.Combine(directory, "state");
            Directory.CreateDirectory(stateDirectory);
            var receiverRegistry = new MobileOrderReceiverRegistry(
                Path.Combine(stateDirectory, "order-receivers.json"),
                () => DateTime.UtcNow.AddHours(-1));
            receiverRegistry.Register(
                IPAddress.Parse("192.168.31.201"),
                "recording-workstation-0001",
                "电脑2",
                deviceKind: "pc",
                platform: "windows",
                trustProvidedName: true,
                hasRecordings: true);

            using var database = new VideoDatabase(
                Path.Combine(directory, "videos.db"));
            string recordingPath = Path.Combine(
                directory,
                "recording.mp4");
            long recordId = database.InsertMobileBackupRecord(
                "ORDER-1",
                recordingPath,
                1024,
                DateTime.Now,
                30,
                "recording-workstation-0001",
                "电脑2",
                "session-1",
                "hash-1");
            using var server = new WebServer(
                database,
                mobileBackupStateDirectory: stateDirectory,
                nodeId: "host-node-0001",
                deploymentPreset: DeploymentPresets.MobileBackupHost);

            BackupDeviceRemovalResult result =
                server.TryForgetKnownBackupDevice(
                    "recording-workstation-0001");

            Assert.Equal(BackupDeviceRemovalStatus.Removed, result.Status);
            Assert.DoesNotContain(
                server.GetMobileBackupDeviceDirectory(),
                device => device.DeviceId == "recording-workstation-0001");
            Assert.Single(database.GetVideoSources());
            Assert.Equal(
                "电脑2",
                server.GetCurrentSourceDeviceNames()[
                    "recording-workstation-0001"]);
            VideoRecord record = Assert.IsType<VideoRecord>(
                database.GetVideoById(recordId));
            Assert.Equal("电脑2", record.SourceDeviceName);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task HostDeviceSnapshot_DoesNotReAddUnpairedDeviceFromVideoRows()
    {
        string directory = CreateTempDirectory();
        try
        {
            string stateDirectory = Path.Combine(directory, "state");
            Directory.CreateDirectory(stateDirectory);
            var registry = new MobileOrderReceiverRegistry(
                Path.Combine(stateDirectory, "order-receivers.json"),
                () => DateTime.UtcNow.AddHours(-1));
            registry.Register(
                IPAddress.Parse("192.168.31.201"),
                "phone-removed",
                "安卓1",
                deviceKind: "mobile",
                platform: "android",
                hasRecordings: true);
            using var database = new VideoDatabase(
                Path.Combine(directory, "videos.db"));
            database.InsertMobileBackupRecord(
                "ORDER-KEEP",
                Path.Combine(directory, "kept.mp4"),
                1,
                DateTime.Now,
                3,
                "phone-removed",
                "安卓1",
                "session-keep",
                "sha-keep");
            using var server = new WebServer(
                database,
                mobileBackupStateDirectory: stateDirectory,
                nodeId: "host-node-0001",
                deploymentPreset: DeploymentPresets.MobileBackupHost);
            Assert.Equal(
                BackupDeviceRemovalStatus.Removed,
                server.TryForgetKnownBackupDevice("phone-removed").Status);

            var service = new TransferManagementSnapshotService(
                TransferManagementMode.HostDevices,
                new AppConfig
                {
                    NodeId = "host-node-0001",
                    DeploymentPreset = DeploymentPresets.MobileBackupHost
                },
                database,
                server,
                queueStore: null,
                transferService: null);
            TransferManagementSnapshot snapshot = await service.LoadAsync(
                TestContext.Current.CancellationToken);

            Assert.Empty(snapshot.HostDevices);
            Assert.Single(database.GetMobileBackupDeviceSummaries(DateTime.Today));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task HostDeviceSnapshot_HidesHistoryOnlyDeviceAfterRestart()
    {
        string directory = CreateTempDirectory();
        try
        {
            string stateDirectory = Path.Combine(directory, "state");
            Directory.CreateDirectory(stateDirectory);
            string databasePath = Path.Combine(directory, "videos.db");
            using var database = new VideoDatabase(databasePath);
            database.InsertMobileBackupRecord(
                "ORDER-HISTORY",
                Path.Combine(directory, "history.mp4"),
                1,
                DateTime.Now.AddDays(-30),
                3,
                "phone-history-only",
                "旧安卓",
                "session-history",
                "sha-history");

            using (var server = new WebServer(
                       database,
                       mobileBackupStateDirectory: stateDirectory,
                       nodeId: "host-node-0001",
                       deploymentPreset: DeploymentPresets.MobileBackupHost))
            {
                Assert.Equal(
                    BackupDeviceRemovalStatus.Removed,
                    server.TryForgetKnownBackupDevice(
                        "phone-history-only").Status);
                Assert.Contains(
                    "phone-history-only",
                    server.GetRemovedSourceDeviceIds());
            }

            using var restartedServer = new WebServer(
                database,
                mobileBackupStateDirectory: stateDirectory,
                nodeId: "host-node-0001",
                deploymentPreset: DeploymentPresets.MobileBackupHost);
            var service = new TransferManagementSnapshotService(
                TransferManagementMode.HostDevices,
                new AppConfig
                {
                    NodeId = "host-node-0001",
                    DeploymentPreset = DeploymentPresets.MobileBackupHost
                },
                database,
                restartedServer,
                queueStore: null,
                transferService: null);
            TransferManagementSnapshot snapshot = await service.LoadAsync(
                TestContext.Current.CancellationToken);

            Assert.Contains(
                "phone-history-only",
                restartedServer.GetRemovedSourceDeviceIds());
            Assert.Empty(snapshot.HostDevices);
            Assert.Single(database.GetMobileBackupDeviceSummaries(DateTime.Today));
        }
        finally
        {
            DeleteTempDirectory(directory);
            SqliteTestPool.ClearPoolFor(directory);
        }
    }

    [Fact]
    public async Task HostDeviceSnapshot_ImportsLegacyHistoryOnlyRemovalsAndKeepsCurrentFour()
    {
        string directory = CreateTempDirectory();
        try
        {
            string stateDirectory = Path.Combine(directory, "state");
            Directory.CreateDirectory(stateDirectory);
            var registry = new MobileOrderReceiverRegistry(
                Path.Combine(stateDirectory, "order-receivers.json"),
                () => DateTime.UtcNow.AddHours(-1));
            string[] currentDeviceIds =
            [
                "current-phone-1",
                "current-phone-2",
                "current-phone-3",
                "current-phone-4"
            ];
            string[] historyOnlyDeviceIds =
            [
                "history-phone-1",
                "history-phone-2",
                "history-phone-3",
                "history-pc-1"
            ];
            for (int index = 0; index < currentDeviceIds.Length; index++)
            {
                registry.Register(
                    IPAddress.Parse($"192.168.31.{201 + index}"),
                    currentDeviceIds[index],
                    $"当前设备{index + 1}",
                    deviceKind: "mobile",
                    platform: "android",
                    hasRecordings: true);
            }

            string databasePath = Path.Combine(directory, "videos.db");
            using var database = new VideoDatabase(databasePath);
            foreach (string deviceId in currentDeviceIds.Concat(historyOnlyDeviceIds))
            {
                database.InsertMobileBackupRecord(
                    $"ORDER-{deviceId}",
                    Path.Combine(directory, $"{deviceId}.mp4"),
                    1,
                    DateTime.Now.AddDays(-30),
                    3,
                    deviceId,
                    "历史设备",
                    $"session-{deviceId}",
                    $"sha-{deviceId}");
            }

            var config = new AppConfig
            {
                NodeId = "host-node-0001",
                DeploymentPreset = DeploymentPresets.MobileBackupHost
            };
            using (var server = new WebServer(
                       database,
                       mobileBackupStateDirectory: stateDirectory,
                       nodeId: config.NodeId,
                       deploymentPreset: config.DeploymentPreset))
            {
                var service = new TransferManagementSnapshotService(
                    TransferManagementMode.HostDevices,
                    config,
                    database,
                    server,
                    queueStore: null,
                    transferService: null);
                Assert.Equal(
                    8,
                    (await service.LoadAsync(
                        TestContext.Current.CancellationToken))
                    .HostDevices.Count);

                Assert.Equal(
                    4,
                    server.ImportLegacyDeviceRemovals(
                        database.GetVideoSources()));

                Assert.Equal(
                    currentDeviceIds.OrderBy(item => item),
                    (await service.LoadAsync(
                        TestContext.Current.CancellationToken))
                    .HostDevices
                    .Select(item => item.DeviceId)
                    .OrderBy(item => item));
            }

            using var restartedServer = new WebServer(
                database,
                mobileBackupStateDirectory: stateDirectory,
                nodeId: config.NodeId,
                deploymentPreset: config.DeploymentPreset);
            var restartedService = new TransferManagementSnapshotService(
                TransferManagementMode.HostDevices,
                config,
                database,
                restartedServer,
                queueStore: null,
                transferService: null);
            TransferManagementSnapshot restarted = await restartedService.LoadAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(
                currentDeviceIds.OrderBy(item => item),
                restarted.HostDevices
                    .Select(item => item.DeviceId)
                    .OrderBy(item => item));
        }
        finally
        {
            DeleteTempDirectory(directory);
            SqliteTestPool.ClearPoolFor(directory);
        }
    }

    [Fact]
    public void ManagementQuery_HandlesOneHundredThousandPendingRowsWithBoundedPreview()
    {
        string directory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "videos.db");
            using (var database = new VideoDatabase(databasePath)) { }
            using (var store = new RecordingTransferQueueStore(databasePath)) { }
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    WITH RECURSIVE seq(value) AS (
                        VALUES(1)
                        UNION ALL
                        SELECT value + 1 FROM seq WHERE value < 100000
                    )
                    INSERT INTO RecordingTransferQueue (
                        LocalVideoRecordId, LocalFilePath, SourceSessionId, TargetNodeId,
                        TargetAddress, State, CreatedAt, UpdatedAt)
                    SELECT value, 'video.mp4', 'session-' || value, 'host',
                           'http://127.0.0.1:5280', 'Pending',
                           '2026-01-01 00:00:00', '2026-01-01 00:00:00'
                    FROM seq;";
                command.ExecuteNonQuery();
            }

            using var reopened = new RecordingTransferQueueStore(databasePath);
            RecordingTransferManagementData data = reopened.GetManagementData(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1),
                failureLimit: 20);

            Assert.Equal(100000, data.PendingCount);
            Assert.Empty(data.FailurePreview);
            Assert.Null(data.CurrentUpload);
            Assert.Equal(0, data.NeedsActionCount);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Theory]
    [InlineData("storage_unavailable", RecordingTransferFailureKinds.HostStorageUnavailable)]
    [InlineData("server_busy", RecordingTransferFailureKinds.HostWriteBusy)]
    [InlineData("host_storage_busy", RecordingTransferFailureKinds.HostWriteBusy)]
    [InlineData("upload_not_found", RecordingTransferFailureKinds.RetryableTask)]
    [InlineData("enrollment_required", RecordingTransferFailureKinds.HostApprovalRequired)]
    [InlineData("device_token_invalid", RecordingTransferFailureKinds.HostApprovalRequired)]
    [InlineData("session_conflict", RecordingTransferFailureKinds.ActionRequired)]
    public void FailurePolicy_ClassifiesHostErrorsForTheManagementUi(
        string errorCode,
        string expectedKind)
    {
        var exception = new RecordingTransferHostException(
            errorCode,
            "host rejected upload",
            HttpStatusCode.Conflict);

        Assert.Equal(
            expectedKind,
            RecordingTransferFailurePolicy.ClassifyFailureKind(exception));
    }

    [Fact]
    public void TransferWindow_UsesTheRequestedCompactWidth()
    {
        string xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "TransferManagementWindow.xaml"));
        Assert.Contains("Width=\"480\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"420\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"400\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"320\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=\"720\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ResizeMode=\"CanResizeWithGrip\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SizeToContent=\"Height\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"54\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "WindowStyle=\"SingleBorderWindow\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("WindowStyle=\"ToolWindow\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowsTransparency=\"True\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BorderlessDialogSurfaceStyle", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "<controls:SmoothProgressBar",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("<ProgressBar", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"TransferRowStyle\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "<Trigger Property=\"IsMouseOver\" Value=\"True\">",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Value=\"{DynamicResource ControlBackgroundHover}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Content=\"{loc:Localized 关闭}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"✕\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"{loc:Localized 解绑}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Text=\"{loc:Localized 待上传}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PendingCountText", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Click=\"RemoveDevice_Click\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            xaml.Split(
                "DataContext.TodayColumnWidth",
                StringSplitOptions.None).Length - 1);
        Assert.Equal(
            2,
            xaml.Split(
                "DataContext.ActivityColumnWidth",
                StringSplitOptions.None).Length - 1);
        Assert.Equal(
            2,
            xaml.Split(
                "DataContext.AttentionColumnWidth",
                StringSplitOptions.None).Length - 1);
        Assert.Equal(
            4,
            xaml.Split(
                "TextAlignment=\"Left\"",
                StringSplitOptions.None).Length - 1);
        Assert.Equal(
            2,
            xaml.Split(
                "Width=\"{Binding DataContext.DeviceNameColumnWidth, RelativeSource={RelativeSource AncestorType=Window}}\"",
                StringSplitOptions.None).Length - 1);
        Assert.Equal(
            2,
            xaml.Split(
                "Grid.Column=\"6\"",
                StringSplitOptions.None).Length - 1);

        string viewModel = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "TransferManagementWindowViewModel.cs"));
        Assert.Contains(
            "AppLanguage.Get(\"今日数量单位\")",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "AppLanguage.Get(IsHostMode ? \"今日已接收\" : \"今日已上传\")",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding HasDetailSummary, Converter={StaticResource BoolToVisibility}}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding TodayReceivedCountText}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding TodaySummaryLabel}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding Title}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding CountText}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("今日总", xaml, StringComparison.Ordinal);
        int uploadedLabelIndex = xaml.IndexOf(
            "Text=\"{Binding TodaySummaryLabel}\"",
            StringComparison.Ordinal);
        string uploadedLabelNode = xaml[
            uploadedLabelIndex..xaml.IndexOf("/>", uploadedLabelIndex, StringComparison.Ordinal)];
        Assert.Contains("FontSize=\"16\"", uploadedLabelNode, StringComparison.Ordinal);
        int uploadedCountIndex = xaml.IndexOf(
            "Text=\"{Binding TodayReceivedCountText}\"",
            uploadedLabelIndex,
            StringComparison.Ordinal);
        string uploadedCountNode = xaml[
            uploadedCountIndex..xaml.IndexOf("/>", uploadedCountIndex, StringComparison.Ordinal)];
        Assert.Contains("FontSize=\"16\"", uploadedCountNode, StringComparison.Ordinal);
        Assert.Contains("FontWeight=\"Bold\"", uploadedCountNode, StringComparison.Ordinal);
        int uploadedUnitIndex = xaml.IndexOf(
            "Text=\"{loc:Localized 今日数量单位}\"",
            uploadedCountIndex,
            StringComparison.Ordinal);
        string uploadedUnitNode = xaml[
            uploadedUnitIndex..xaml.IndexOf("/>", uploadedUnitIndex, StringComparison.Ordinal)];
        Assert.Contains("FontSize=\"16\"", uploadedUnitNode, StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding RemainingUploadText}\"",
            xaml,
            StringComparison.Ordinal);
        int failureRowsIndex = xaml.IndexOf(
            "<ItemsControl ItemsSource=\"{Binding FailureItems}\">",
            StringComparison.Ordinal);
        int currentUploadIndex = xaml.IndexOf(
            "Height=\"54\"",
            StringComparison.Ordinal);
        int remainingUploadIndex = xaml.IndexOf(
            "Text=\"{Binding RemainingUploadText}\"",
            StringComparison.Ordinal);
        Assert.True(failureRowsIndex >= 0);
        Assert.True(currentUploadIndex > failureRowsIndex);
        Assert.True(remainingUploadIndex > currentUploadIndex);
        Assert.Contains(
            "Text=\"{Binding SummaryText}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<DataTrigger Binding=\"{Binding IsAttentionSummary}\" Value=\"True\">",
            xaml,
            StringComparison.Ordinal);
        string mainXaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "MainWindow.xaml"));
        Assert.Equal(
            2,
            mainXaml.Split(
                "PreviewMouseLeftButtonUp=\"TransferManagementCard_PreviewMouseLeftButtonUp\"",
                StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain(
            "MouseLeftButtonUp=\"TransferManagementCard_MouseLeftButtonUp\"",
            mainXaml,
            StringComparison.Ordinal);

        string mobileConnectionXaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "MobileConnectionWindow.xaml"));
        Assert.Contains(
            "WindowStyle=\"SingleBorderWindow\"",
            mobileConnectionXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "WindowStyle=\"ToolWindow\"",
            mobileConnectionXaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceNameColumnReservesTheLongestCurrentDeviceName()
    {
        double compactWidth =
            TransferManagementWindowViewModel.CalculateDeviceNameColumnWidth(
                ["手机1", "电脑1"]);
        double longNameWidth =
            TransferManagementWindowViewModel.CalculateDeviceNameColumnWidth(
                ["手机1", "手机1·75DD03"]);

        Assert.Equal(80, compactWidth);
        Assert.True(longNameWidth > compactWidth);
        Assert.True(longNameWidth <= 132);
    }

    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public void PreviousActivityDatesUseTheCurrentCultureShortFormat(string cultureName)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            string text = TransferManagementWindowViewModel.FormatActivity(
                DateTimeOffset.Now.AddDays(-2),
                isOnline: true);
            Assert.Contains("/", text, StringComparison.Ordinal);
            Assert.DoesNotContain("-", text, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static long InsertRecording(
        VideoDatabase database,
        string directory,
        string orderId,
        long size)
    {
        string path = Path.Combine(directory, $"video-{orderId}.mp4");
        long id = database.InsertVideoRecord(
            orderId,
            "发货",
            "",
            "",
            path,
            DateTime.Now.AddMinutes(-1));
        database.UpdateVideoRecordOnStop(
            id,
            DateTime.Now,
            30,
            size,
            "手动");
        return id;
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"TransferManagementSnapshotTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("找不到仓库根目录");
    }
}
