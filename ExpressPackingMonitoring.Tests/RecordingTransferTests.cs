using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class RecordingTransferTests
{
    [Fact]
    public async Task Dispose_DefersOwnedResourcesUntilActiveTransferExits()
    {
        string directory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "videos.db");
            string videoPath = Path.Combine(directory, "video.mp4");
            await File.WriteAllBytesAsync(videoPath, new byte[4096], TestContext.Current.CancellationToken);
            using var database = new VideoDatabase(databasePath);
            long recordId = database.InsertVideoRecord("DISPOSE-RACE", "发货", "", "", videoPath, DateTime.Now.AddMinutes(-1));
            database.UpdateVideoRecordOnStop(recordId, DateTime.Now, 30, 4096, "手动");

            string targetNodeId = Guid.NewGuid().ToString("D");
            AppConfig config = CreateConfig(directory, targetNodeId);
            var resolverStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseResolver = new TaskCompletionSource<PackingProofNodeInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var store = new RecordingTransferQueueStore(databasePath);
            var service = new RecordingTransferService(
                store,
                database,
                () => config,
                nodeInfoResolver: (_, _) =>
                {
                    resolverStarted.TrySetResult();
                    return releaseResolver.Task;
                })
            {
                ShutdownWaitTimeout = TimeSpan.FromMilliseconds(50)
            };
            service.EnqueueCompletedRecordings();

            Task<int> processing = service.ProcessReadyOnceAsync(TestContext.Current.CancellationToken);
            await resolverStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            service.Dispose();

            Assert.False(service.ResourcesDisposedForTesting);
            Assert.Equal(1, store.GetSummary().PendingCount);

            releaseResolver.TrySetResult(null);
            Assert.Equal(
                1,
                await processing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.True(SpinWait.SpinUntil(() => service.ResourcesDisposedForTesting, TimeSpan.FromSeconds(10)));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void QueueStore_RecoversUploadingTaskAfterRestart()
    {
        string directory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "videos.db");
            using (var database = new VideoDatabase(databasePath)) { }
            using (var store = new RecordingTransferQueueStore(databasePath))
            {
                Assert.True(store.Enqueue(
                    1,
                    Path.Combine(directory, "video.mp4"),
                    "session-1",
                    Guid.NewGuid().ToString("D"),
                    "http://127.0.0.1:5280",
                    DateTime.UtcNow));
                RecordingTransferTask task = Assert.Single(store.GetReady(DateTime.UtcNow));
                store.MarkUploading(task.Id, 1024, DateTime.UtcNow);
            }

            using var reopened = new RecordingTransferQueueStore(databasePath);
            reopened.RecoverInterrupted(DateTime.UtcNow);
            RecordingTransferTask recovered = Assert.Single(reopened.GetReady(DateTime.UtcNow));
            Assert.Equal(RecordingTransferStates.Pending, recovered.State);
            Assert.Equal(1024, recovered.ServerOffset);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void QueueOnlyIncludesRecordingsCreatedAfterWorkstationActivation()
    {
        string directory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "videos.db");
            string oldPath = Path.Combine(directory, "old.mp4");
            string newPath = Path.Combine(directory, "new.mp4");
            File.WriteAllBytes(oldPath, new byte[128]);
            File.WriteAllBytes(newPath, new byte[128]);
            using var database = new VideoDatabase(databasePath);
            long oldRecordId = database.InsertVideoRecord(
                "OLD-HOST-RECORDING",
                "发货",
                "",
                "",
                oldPath,
                DateTime.Now.AddMinutes(-20));
            database.UpdateVideoRecordOnStop(oldRecordId, DateTime.Now.AddMinutes(-19), 60, 128, "手动");
            long newRecordId = database.InsertVideoRecord(
                "NEW-WORKSTATION-RECORDING",
                "发货",
                "",
                "",
                newPath,
                DateTime.Now.AddMinutes(-5));
            database.UpdateVideoRecordOnStop(newRecordId, DateTime.Now.AddMinutes(-4), 60, 128, "手动");

            string targetNodeId = Guid.NewGuid().ToString("D");
            AppConfig config = CreateConfig(directory, targetNodeId);
            config.RecordingWorkstationActivatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            var store = new RecordingTransferQueueStore(databasePath);
            using (var service = new RecordingTransferService(store, database, () => config))
            {
                Assert.Equal(1, service.EnqueueCompletedRecordings());
            }

            using var reopened = new RecordingTransferQueueStore(databasePath);
            RecordingTransferTask queued = Assert.Single(reopened.GetReady(DateTime.UtcNow));
            Assert.Equal(newRecordId, queued.LocalVideoRecordId);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void EnqueueStillFindsNewRecordingsWhenQueueScanLimitIsReached()
    {
        string directory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "videos.db");
            string targetNodeId = Guid.NewGuid().ToString("D");
            AppConfig config = CreateConfig(directory, targetNodeId);
            using var database = new VideoDatabase(databasePath);
            using var store = new RecordingTransferQueueStore(databasePath);

            const int total = 505;
            DateTime start = DateTime.Now.AddMinutes(-total);
            for (int i = 1; i <= total; i++)
            {
                string videoPath = Path.Combine(directory, $"video-{i:D4}.mp4");
                File.WriteAllBytes(videoPath, new byte[128]);
                long recordId = database.InsertVideoRecord(
                    $"TRACK-{i:D4}",
                    "发货",
                    "",
                    "",
                    videoPath,
                    start.AddMinutes(i));
                database.UpdateVideoRecordOnStop(
                    recordId,
                    start.AddMinutes(i + 1),
                    60,
                    128,
                    "手动");
                if (i <= 500)
                {
                    store.Enqueue(
                        recordId,
                        videoPath,
                        $"{config.NodeId}:{recordId}",
                        targetNodeId,
                        "http://127.0.0.1:5280",
                        DateTime.UtcNow);
                }
            }

            using (var service = new RecordingTransferService(store, database, () => config))
            {
                Assert.Equal(total - 500, service.EnqueueCompletedRecordings());
            }

            using var reopened = new RecordingTransferQueueStore(databasePath);
            Assert.Equal(total, reopened.GetSummary().PendingCount);
            RecordingTransferTask[] pending = reopened.GetReady(DateTime.UtcNow, limit: total + 10).ToArray();
            Assert.Equal(
                Enumerable.Range(501, total - 500),
                pending
                    .Where(task => task.LocalVideoRecordId > 500)
                    .Select(task => (int)task.LocalVideoRecordId)
                    .OrderBy(id => id));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void CompletedRecordingIsQueuedAfterHostIsBoundLater()
    {
        string directory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "videos.db");
            string videoPath = Path.Combine(directory, "recorded-before-binding.mp4");
            File.WriteAllBytes(videoPath, new byte[128]);
            using var database = new VideoDatabase(databasePath);
            long recordId = database.InsertVideoRecord(
                "LATE-BOUND-RECORDING",
                "发货",
                "",
                "",
                videoPath,
                DateTime.Now.AddMinutes(-1));
            database.UpdateVideoRecordOnStop(
                recordId,
                DateTime.Now,
                60,
                new FileInfo(videoPath).Length,
                "手动");

            AppConfig config = CreateConfig(directory, Guid.NewGuid().ToString("D"));
            config.LastKnownHostNodeId = "";
            config.LastKnownHostAddress = "";
            config.LastKnownHostAccessKey = "";
            using var store = new RecordingTransferQueueStore(databasePath);
            using var service = new RecordingTransferService(store, database, () => config);

            Assert.Equal(0, service.EnqueueCompletedRecordings());
            Assert.Empty(store.GetReady(DateTime.UtcNow));
            Assert.True(File.Exists(videoPath));

            string targetNodeId = Guid.NewGuid().ToString("D");
            config.LastKnownHostNodeId = targetNodeId;
            config.LastKnownHostAddress = "http://127.0.0.1:5280";
            config.LastKnownHostAccessKey = "0123456789abcdef0123456789abcdef";
            config.LastKnownHostBackupAuthVersion = BackupRequestAuthentication.CurrentVersion;

            Assert.Equal(1, service.EnqueueCompletedRecordings());
            RecordingTransferTask queued = Assert.Single(store.GetReady(DateTime.UtcNow));
            Assert.Equal(recordId, queued.LocalVideoRecordId);
            Assert.Equal(targetNodeId, queued.TargetNodeId);
            Assert.True(File.Exists(videoPath));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task Transfer_OnlyMarksUploadedAfterVerifiedResponse()
    {
        string directory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "videos.db");
            string videoPath = Path.Combine(directory, "video.mp4");
            await File.WriteAllBytesAsync(
                videoPath,
                Enumerable.Range(0, 10000).Select(i => (byte)(i % 251)).ToArray(),
                TestContext.Current.CancellationToken);
            long recordId;
            using var database = new VideoDatabase(databasePath);
            recordId = database.InsertVideoRecord("TRACK-1", "退货", "", "", videoPath, DateTime.Now.AddMinutes(-1));
            database.UpdateVideoRecordOnStop(recordId, DateTime.Now, 60, new FileInfo(videoPath).Length, "手动");

            string targetNodeId = Guid.NewGuid().ToString("D");
            var config = CreateConfig(directory, targetNodeId);
            var handler = new BackupProtocolHandler(verified: true, targetNodeId, config.NodeId);
            using var client = new HttpClient(handler);
            var store = new RecordingTransferQueueStore(databasePath);
            using (var service = new RecordingTransferService(
                       store,
                       database,
                       () => config,
                       client,
                       (_, _) => Task.FromResult<PackingProofNodeInfo?>(CreateHost(targetNodeId))))
            {
                Assert.Equal(1, service.EnqueueCompletedRecordings());
                Assert.Equal(
                    1,
                    await service.ProcessReadyOnceAsync(TestContext.Current.CancellationToken));
            }

            using var reopened = new RecordingTransferQueueStore(databasePath);
            RecordingTransferTask uploaded = Assert.Single(reopened.GetUploadedWithLocalCache());
            Assert.Equal(42, uploaded.RemoteVideoRecordId);
            Assert.True(File.Exists(videoPath));
            Assert.True(handler.SawSignedAuthentication);
            Assert.True(handler.SawPcSource);
            Assert.Equal("退货", handler.ReceivedMode);
            Assert.Equal(new FileInfo(videoPath).Length, handler.ReceivedBytes);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task Transfer_KeepsLocalFileWhenHostDoesNotVerify()
    {
        string directory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "videos.db");
            string videoPath = Path.Combine(directory, "video.mp4");
            await File.WriteAllBytesAsync(
                videoPath,
                new byte[8192],
                TestContext.Current.CancellationToken);
            using var database = new VideoDatabase(databasePath);
            long recordId = database.InsertVideoRecord("TRACK-2", "发货", "", "", videoPath, DateTime.Now.AddMinutes(-1));
            database.UpdateVideoRecordOnStop(recordId, DateTime.Now, 60, 8192, "手动");

            string targetNodeId = Guid.NewGuid().ToString("D");
            var config = CreateConfig(directory, targetNodeId);
            using var client = new HttpClient(new BackupProtocolHandler(verified: false));
            var store = new RecordingTransferQueueStore(databasePath);
            using (var service = new RecordingTransferService(
                       store,
                       database,
                       () => config,
                       client,
                       (_, _) => Task.FromResult<PackingProofNodeInfo?>(CreateHost(targetNodeId))))
            {
                service.EnqueueCompletedRecordings();
                await service.ProcessReadyOnceAsync(TestContext.Current.CancellationToken);
            }

            using var reopened = new RecordingTransferQueueStore(databasePath);
            RecordingTransferSummary summary = reopened.GetSummary();
            Assert.Equal(1, summary.FailedCount);
            Assert.Contains("未明确确认", summary.LastError);
            Assert.True(File.Exists(videoPath));
            Assert.Empty(reopened.GetUploadedWithLocalCache());
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task Transfer_LeavesTaskPendingAndFileUntouchedWhenHostIsOffline()
    {
        string directory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "videos.db");
            string videoPath = Path.Combine(directory, "video.mp4");
            await File.WriteAllBytesAsync(
                videoPath,
                new byte[4096],
                TestContext.Current.CancellationToken);
            using var database = new VideoDatabase(databasePath);
            long recordId = database.InsertVideoRecord("TRACK-3", "发货", "", "", videoPath, DateTime.Now.AddMinutes(-1));
            database.UpdateVideoRecordOnStop(recordId, DateTime.Now, 30, 4096, "手动");

            string targetNodeId = Guid.NewGuid().ToString("D");
            var config = CreateConfig(directory, targetNodeId);
            using var client = new HttpClient(new BackupProtocolHandler(verified: true));
            var store = new RecordingTransferQueueStore(databasePath);
            using (var service = new RecordingTransferService(
                       store,
                       database,
                       () => config,
                       client,
                       (_, _) => Task.FromResult<PackingProofNodeInfo?>(null)))
            {
                service.EnqueueCompletedRecordings();
                await service.ProcessReadyOnceAsync(TestContext.Current.CancellationToken);
            }

            using var reopened = new RecordingTransferQueueStore(databasePath);
            Assert.Equal(1, reopened.GetSummary().FailedCount);
            Assert.True(File.Exists(videoPath));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task ChangingConfiguredHostDoesNotRetargetExistingTask()
    {
        string directory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "videos.db");
            string videoPath = Path.Combine(directory, "video.mp4");
            await File.WriteAllBytesAsync(
                videoPath,
                new byte[4096],
                TestContext.Current.CancellationToken);
            using var database = new VideoDatabase(databasePath);
            long recordId = database.InsertVideoRecord(
                "BOUND-HOST-TRACK",
                "发货",
                "",
                "",
                videoPath,
                DateTime.Now.AddMinutes(-1));
            database.UpdateVideoRecordOnStop(recordId, DateTime.Now, 30, 4096, "手动");

            string originalNodeId = Guid.NewGuid().ToString("D");
            AppConfig config = CreateConfig(directory, originalNodeId);
            var store = new RecordingTransferQueueStore(databasePath);
            using (var service = new RecordingTransferService(store, database, () => config))
            {
                Assert.Equal(1, service.EnqueueCompletedRecordings());
                config.LastKnownHostNodeId = Guid.NewGuid().ToString("D");
                await service.ProcessReadyOnceAsync(TestContext.Current.CancellationToken);
            }

            using var reopened = new RecordingTransferQueueStore(databasePath);
            Assert.Equal(1, reopened.GetSummary().FailedCount);
            RecordingTransferTask task = Assert.Single(
                reopened.GetReady(DateTime.UtcNow.AddMinutes(10)));
            Assert.Equal(originalNodeId, task.TargetNodeId);
            Assert.True(File.Exists(videoPath));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void CacheCleanupStatePreservesDuplicateOrderHistory()
    {
        string directory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "videos.db");
            string videoPath = Path.Combine(directory, "video.mp4");
            File.WriteAllBytes(videoPath, new byte[128]);
            using var database = new VideoDatabase(databasePath);
            long recordId = database.InsertVideoRecord(
                "DUPLICATE-TRACK",
                "发货",
                "",
                "",
                videoPath,
                DateTime.Now.AddMinutes(-2));
            database.UpdateVideoRecordOnStop(recordId, DateTime.Now, 60, 128, "手动");
            database.MarkVideoUploaded(recordId, 88);
            File.Delete(videoPath);
            database.MarkVideoCacheDeleted(recordId, 88);

            Assert.True(database.OrderIdExistsRecent("DUPLICATE-TRACK"));
            VideoRecord record = database.GetVideoById(recordId);
            Assert.NotNull(record);
            Assert.False(record.IsDeleted);
            Assert.Equal("Remote", record.StorageState);
            Assert.Equal(88, record.RemoteVideoRecordId);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void CleanupCandidatesContainOnlyVerifiedUploadedTasks()
    {
        string directory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "videos.db");
            using (var database = new VideoDatabase(databasePath)) { }
            using var store = new RecordingTransferQueueStore(databasePath);
            string nodeId = Guid.NewGuid().ToString("D");
            store.Enqueue(1, Path.Combine(directory, "pending.mp4"), "pending", nodeId, "host", DateTime.UtcNow);
            store.Enqueue(2, Path.Combine(directory, "failed.mp4"), "failed", nodeId, "host", DateTime.UtcNow);
            store.Enqueue(3, Path.Combine(directory, "uploaded.mp4"), "uploaded", nodeId, "host", DateTime.UtcNow);
            RecordingTransferTask[] tasks = store.GetReady(DateTime.UtcNow).ToArray();
            store.MarkFailed(tasks.Single(task => task.LocalVideoRecordId == 2).Id, 1, "offline", DateTime.UtcNow.AddMinutes(1), DateTime.UtcNow);
            store.MarkUploaded(tasks.Single(task => task.LocalVideoRecordId == 3).Id, 99, DateTime.UtcNow);

            RecordingTransferTask candidate = Assert.Single(store.GetUploadedWithLocalCache());
            Assert.Equal(3, candidate.LocalVideoRecordId);
            Assert.Equal(99, candidate.RemoteVideoRecordId);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void CachePoliciesSelectOnlyTheExpectedUploadedRecordings()
    {
        DateTime now = new(2026, 7, 29, 12, 0, 0, DateTimeKind.Local);
        RecordingTransferTask[] uploaded =
        [
            new() { Id = 1, CreatedAt = now.AddDays(-5), LocalFilePath = "old.mp4" },
            new() { Id = 2, CreatedAt = now.AddDays(-1), LocalFilePath = "new.mp4" }
        ];
        string Resolve(RecordingTransferTask task) => task.LocalFilePath;

        Assert.Equal(
            [1L, 2L],
            MainViewModel.SelectRecordingCacheCleanupCandidates(
                    uploaded, "DeleteImmediately", 3, 50, now, Resolve)
                .Select(task => task.Id));
        Assert.Equal(
            [1L],
            MainViewModel.SelectRecordingCacheCleanupCandidates(
                    uploaded, "KeepDays", 3, 50, now, Resolve)
                .Select(task => task.Id));
        Assert.Equal(
            [1L],
            MainViewModel.SelectRecordingCacheCleanupCandidates(
                    uploaded,
                    "KeepWithinSize",
                    3,
                    1,
                    now,
                    Resolve,
                    _ => 700L * 1024 * 1024)
                .Select(task => task.Id));
    }

    private static AppConfig CreateConfig(string directory, string targetNodeId)
    {
        var config = new AppConfig
        {
            DeploymentPreset = DeploymentPresets.RecordingWorkstation,
            DeploymentSchemaVersion = DeploymentPresets.CurrentSchemaVersion,
            NodeId = Guid.NewGuid().ToString("D"),
            NodeName = "电脑录制工位",
            LastKnownHostNodeId = targetNodeId,
            LastKnownHostAddress = "http://127.0.0.1:5280",
            LastKnownHostAccessKey = "0123456789abcdef0123456789abcdef",
            LastKnownHostBackupAuthVersion = BackupRequestAuthentication.CurrentVersion,
            BackupConnectionSchemaVersion = AppConfig.CurrentBackupConnectionSchemaVersion,
            RecordingWorkstationActivatedAtUtc = DateTime.UtcNow.AddDays(-1),
            EnableWebServer = false,
            StorageLocations = [new StorageLocation { Path = directory }]
        };
        AppConfig.NormalizeAfterLoad(config);
        return config;
    }

    private static PackingProofNodeInfo CreateHost(string nodeId) => new()
    {
        Protocol = PackingProofNodeInfo.ExpectedProtocol,
        ProtocolVersion = PackingProofNodeInfo.SupportedProtocolVersion,
        NodeId = nodeId,
        NodeName = "保存主机",
        Preset = DeploymentPresets.RecordingHost,
        Capabilities = [PackingProofCapabilities.Host, PackingProofCapabilities.MobileBackup],
        HttpPort = 5280,
        Address = "http://127.0.0.1:5280",
        BackupCompatibility = BackupCompatibilityPolicy.CreateHostInfo()
    };

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"RecordingTransferTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        SqliteTestPool.ClearPoolFor(path);
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private sealed class BackupProtocolHandler(
        bool verified,
        string hostNodeId = "",
        string sourceDeviceId = "") : HttpMessageHandler
    {
        private string _sha256 = "";
        public long ReceivedBytes { get; private set; }
        public bool SawSignedAuthentication { get; private set; }
        public bool SawPcSource { get; private set; }
        public string ReceivedMode { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SawSignedAuthentication |= request.Headers.TryGetValues(
                BackupRequestAuthentication.VersionHeader,
                out var versions) && versions.Single() == BackupRequestAuthentication.CurrentVersion.ToString();
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/uploads"))
            {
                using JsonDocument document = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                _sha256 = document.RootElement.GetProperty("fileSha256").GetString()!;
                return Json(new
                {
                    uploadId = _sha256,
                    offset = 0,
                    chunkSize = MobileBackupService.ChunkSizeBytes,
                    fileReady = false
                });
            }

            if (request.Method == HttpMethod.Put && path.EndsWith("/chunks"))
            {
                byte[] content = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                ReceivedBytes += content.Length;
                return Json(new { uploadId = _sha256, offset = ReceivedBytes });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/complete"))
            {
                using JsonDocument document = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                SawPcSource = document.RootElement.GetProperty("sourceDeviceKind").GetString() == "pc";
                ReceivedMode = document.RootElement.GetProperty("mode").GetString() ?? "";
                string sessionId = document.RootElement.GetProperty("sessionId").GetString() ?? "";
                long verifiedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long recordId = verified ? 42 : 0;
                string receipt = verified
                    ? BackupRequestAuthentication.CreateReceiptSignature(
                        "0123456789abcdef0123456789abcdef",
                        hostNodeId,
                        sourceDeviceId,
                        sessionId,
                        _sha256,
                        ReceivedBytes,
                        recordId,
                        verifiedAt)
                    : "";
                return Json(new
                {
                    status = verified ? "verified" : "processing",
                    fileSha256 = _sha256,
                    recordId,
                    authVersion = verified ? BackupRequestAuthentication.CurrentVersion : 0,
                    hostNodeId,
                    sourceDeviceId,
                    sourceSessionId = sessionId,
                    fileSizeBytes = ReceivedBytes,
                    verifiedAtUnixSeconds = verifiedAt,
                    receiptSignature = receipt
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json")
        };
    }
}
