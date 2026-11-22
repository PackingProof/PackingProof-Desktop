using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

[Collection("Web server tests")]
public sealed class RecordingWorkstationLoopbackIntegrationTests
{
    [Fact]
    public async Task UnpairedWorkstationHeartbeatReportsRepairRequired()
    {
        string root = CreateTempDirectory();
        string stateDirectory = Path.Combine(root, "host-state");
        Directory.CreateDirectory(stateDirectory);
        int port = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(root, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupComputerId: Guid.NewGuid().ToString("D"),
                mobileBackupComputerName: "测试主机",
                mobileBackupStateDirectory: stateDirectory,
                mobileBackupRecordingRootResolver: () => Path.Combine(root, "recordings"),
                nodeId: Guid.NewGuid().ToString("D"),
                nodeName: "测试主机",
                deploymentPreset: DeploymentPresets.MobileBackupHost,
                backupDeviceEnrollmentApprover: _ =>
                    BackupDeviceEnrollmentApprovalDecision.Approved);
            server.Start(allowAccessSetup: false);

            string deviceId = Guid.NewGuid().ToString("D");
            BackupDeviceEnrollmentResult enrollment =
                await WorkstationNetwork.EnrollBackupDeviceAsync(
                    $"127.0.0.1:{port}",
                    deviceId,
                    "电脑1",
                    "pc",
                    TestContext.Current.CancellationToken,
                    platform: "windows");
            RecordingWorkstationHeartbeatResult online =
                await WorkstationNetwork.SendRecordingWorkstationHeartbeatAsync(
                    $"127.0.0.1:{port}",
                    deviceId,
                    "电脑1",
                    5280,
                    accessKey: enrollment.DeviceToken,
                    authVersion: enrollment.AuthVersion,
                    token: TestContext.Current.CancellationToken);
            Assert.True(online.Online);
            Assert.False(online.NeedsRepair);

            Assert.Equal(
                BackupDeviceRemovalStatus.Removed,
                server.TryForgetKnownBackupDevice(deviceId).Status);

            RecordingWorkstationHeartbeatResult unpaired =
                await WorkstationNetwork.SendRecordingWorkstationHeartbeatAsync(
                    $"127.0.0.1:{port}",
                    deviceId,
                    "电脑1",
                    5280,
                    accessKey: enrollment.DeviceToken,
                    authVersion: enrollment.AuthVersion,
                    token: TestContext.Current.CancellationToken);
            Assert.False(unpaired.Online);
            Assert.True(unpaired.NeedsRepair);
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(root);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task OlderReportedWorkstationVersionRemainsCompatibleDuringTransition()
    {
        string root = CreateTempDirectory();
        string stateDirectory = Path.Combine(root, "host-state");
        Directory.CreateDirectory(stateDirectory);
        int port = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(root, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupComputerId: Guid.NewGuid().ToString("D"),
                mobileBackupComputerName: "测试主机",
                mobileBackupStateDirectory: stateDirectory,
                mobileBackupRecordingRootResolver: () => Path.Combine(root, "recordings"),
                nodeId: Guid.NewGuid().ToString("D"),
                nodeName: "测试主机",
                deploymentPreset: DeploymentPresets.MobileBackupHost,
                backupDeviceEnrollmentApprover: _ =>
                    BackupDeviceEnrollmentApprovalDecision.Approved);
            server.Start(allowAccessSetup: false);

            string deviceId = Guid.NewGuid().ToString("D");
            BackupDeviceEnrollmentResult enrollment =
                await WorkstationNetwork.EnrollBackupDeviceAsync(
                    $"127.0.0.1:{port}",
                    deviceId,
                    "电脑1",
                    "pc",
                    TestContext.Current.CancellationToken,
                    platform: "windows");

            RecordingWorkstationHeartbeatResult outdated =
                await WorkstationNetwork.SendRecordingWorkstationHeartbeatAsync(
                    $"127.0.0.1:{port}",
                    deviceId,
                    "电脑1",
                    5280,
                    accessKey: enrollment.DeviceToken,
                    authVersion: enrollment.AuthVersion,
                    token: TestContext.Current.CancellationToken,
                    clientVersion: "0.0.54");

            Assert.True(outdated.Online);
            Assert.False(outdated.NeedsUpgrade);
            Assert.False(outdated.NeedsRepair);

            RecordingWorkstationHeartbeatResult current =
                await WorkstationNetwork.SendRecordingWorkstationHeartbeatAsync(
                    $"127.0.0.1:{port}",
                    deviceId,
                    "电脑1",
                    5280,
                    accessKey: enrollment.DeviceToken,
                    authVersion: enrollment.AuthVersion,
                    token: TestContext.Current.CancellationToken);

            Assert.True(current.Online);
            Assert.False(current.NeedsUpgrade);
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(root);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DeniedRecordingHostEnrollmentReturnsActionableFailure()
    {
        string root = CreateTempDirectory();
        int port = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(root, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupComputerId: Guid.NewGuid().ToString("D"),
                mobileBackupStateDirectory: Path.Combine(root, "state"),
                mobileBackupRecordingRootResolver: () => Path.Combine(root, "recordings"),
                nodeId: Guid.NewGuid().ToString("D"),
                deploymentPreset: DeploymentPresets.RecordingHost,
                backupDeviceEnrollmentApprover: _ =>
                    BackupDeviceEnrollmentApprovalDecision.Denied);
            server.Start(allowAccessSetup: false);

            BackupDeviceEnrollmentException error = await Assert.ThrowsAsync<
                BackupDeviceEnrollmentException>(() =>
                WorkstationNetwork.EnrollBackupDeviceAsync(
                    $"127.0.0.1:{port}",
                    Guid.NewGuid().ToString("D"),
                    "电脑1",
                    "pc",
                    TestContext.Current.CancellationToken));

            Assert.Equal(BackupDeviceEnrollmentFailureKind.Denied, error.Kind);
            Assert.Contains("拒绝", error.Message);
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(root);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task UnavailableRecordingHostApprovalReturnsNotPendingError()
    {
        string root = CreateTempDirectory();
        int port = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(root, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupComputerId: Guid.NewGuid().ToString("D"),
                mobileBackupStateDirectory: Path.Combine(root, "state"),
                mobileBackupRecordingRootResolver: () => Path.Combine(root, "recordings"),
                nodeId: Guid.NewGuid().ToString("D"),
                deploymentPreset: DeploymentPresets.RecordingHost,
                backupDeviceEnrollmentApprover: _ =>
                    BackupDeviceEnrollmentApprovalDecision.Unavailable);
            server.Start(allowAccessSetup: false);

            BackupDeviceEnrollmentException error = await Assert.ThrowsAsync<
                BackupDeviceEnrollmentException>(() =>
                WorkstationNetwork.EnrollBackupDeviceAsync(
                    $"127.0.0.1:{port}",
                    Guid.NewGuid().ToString("D"),
                    "电脑1",
                    "pc",
                    TestContext.Current.CancellationToken));

            Assert.Equal(
                BackupDeviceEnrollmentFailureKind.ApprovalUnavailable,
                error.Kind);
            Assert.Contains("无法显示连接确认窗口", error.Message);
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(root);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task UnresponsiveRecordingHostEnrollmentTimesOutInsteadOfWaitingForever()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverCancellation = new CancellationTokenSource();
        Task hangingServer = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(
                serverCancellation.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, serverCancellation.Token);
        }, serverCancellation.Token);
        try
        {
            BackupDeviceEnrollmentException error = await Assert.ThrowsAsync<
                BackupDeviceEnrollmentException>(() =>
                WorkstationNetwork.EnrollBackupDeviceAsync(
                    $"127.0.0.1:{port}",
                    Guid.NewGuid().ToString("D"),
                    "电脑1",
                    "pc",
                    TestContext.Current.CancellationToken,
                    requestTimeout: TimeSpan.FromMilliseconds(250)));

            Assert.Equal(BackupDeviceEnrollmentFailureKind.TimedOut, error.Kind);
            Assert.Contains("超时", error.Message);
        }
        finally
        {
            serverCancellation.Cancel();
            listener.Stop();
            try { await hangingServer; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public void RecordingHostApprovalPendingWindowExpires()
    {
        var config = new AppConfig
        {
            RecordingHostApprovalPending = true,
            RecordingHostApprovalRequestedAtUtc = DateTime.UtcNow
        };
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Assert.True(RecordingHostApprovalPolicy.IsPending(config, now));

        config.RecordingHostApprovalRequestedAtUtc =
            DateTime.UtcNow - RecordingHostApprovalPolicy.PendingLifetime
                - TimeSpan.FromSeconds(1);
        Assert.False(RecordingHostApprovalPolicy.IsPending(config, now));
    }

    [Fact]
    public async Task SameMachineLoopbackTransfer_CachesBeforeBindingAndUploadsAfterBinding()
    {
        string root = CreateTempDirectory();
        string hostRoot = Path.Combine(root, "host-user-data");
        string workstationRoot = Path.Combine(root, "workstation-user-data");
        Directory.CreateDirectory(hostRoot);
        Directory.CreateDirectory(workstationRoot);

        try
        {
            string hostDatabasePath = Path.Combine(hostRoot, "data", "videos.db");
            string hostStoragePath = Path.Combine(hostRoot, "recordings");
            string hostUploadStatePath = Path.Combine(hostRoot, "cache", "mobile-backup");
            string workstationDatabasePath = Path.Combine(workstationRoot, "data", "videos.db");
            string workstationStoragePath = Path.Combine(workstationRoot, "recordings");
            string workstationUploadStatePath = Path.Combine(
                workstationRoot,
                "cache",
                "mobile-backup");
            Directory.CreateDirectory(Path.GetDirectoryName(hostDatabasePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(workstationDatabasePath)!);
            Directory.CreateDirectory(hostStoragePath);
            Directory.CreateDirectory(workstationStoragePath);

            int hostPort = GetFreeTcpPort();
            string hostNodeId = Guid.NewGuid().ToString("D");
            string hostAccessKey = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(hostNodeId)))[..32].ToLowerInvariant();
            AppConfig hostConfig = CreateHostConfig(
                hostNodeId,
                hostAccessKey,
                hostPort,
                hostStoragePath);

            using var hostDatabase = new VideoDatabase(hostDatabasePath);
            using var hostServer = new WebServer(
                hostDatabase,
                hostConfig.WebServerPort,
                requireAccessKey: hostConfig.RequireWebAccessKey,
                accessKey: hostConfig.WebAccessKey,
                listenerHost: "127.0.0.1",
                mobileBackupComputerId: hostConfig.NodeId,
                mobileBackupComputerName: hostConfig.NodeName,
                mobileBackupStateDirectory: hostUploadStatePath,
                mobileBackupRecordingRootResolver: () => hostStoragePath,
                nodeId: hostConfig.NodeId,
                nodeName: hostConfig.NodeName,
                deploymentPreset: hostConfig.DeploymentPreset,
                backupDeviceEnrollmentApprover: _ => BackupDeviceEnrollmentApprovalDecision.Approved);
            hostServer.Start(allowAccessSetup: false);

            int workstationPort = GetFreeTcpPort();
            Assert.NotEqual(hostPort, workstationPort);
            string workstationNodeId = Guid.NewGuid().ToString("D");
            AppConfig workstationConfig = CreateWorkstationConfig(
                workstationNodeId,
                workstationPort,
                workstationStoragePath);

            using var workstationDatabase = new VideoDatabase(workstationDatabasePath);
            using var workstationServer = new WebServer(
                workstationDatabase,
                workstationConfig.WebServerPort,
                listenerHost: "127.0.0.1",
                mobileBackupComputerId: workstationConfig.NodeId,
                mobileBackupComputerName: workstationConfig.NodeName,
                mobileBackupStateDirectory: workstationUploadStatePath,
                mobileBackupRecordingRootResolver: () => workstationStoragePath,
                nodeId: workstationConfig.NodeId,
                nodeName: workstationConfig.NodeName,
                deploymentPreset: workstationConfig.DeploymentPreset,
                orderReceiverOnly: true);
            workstationServer.Start(allowAccessSetup: false);

            string localVideoPath = Path.Combine(workstationStoragePath, "late-bound-recording.mp4");
            byte[] videoBytes = TestMediaAssets.TinyValidMp4;
            Assert.Equal("ftyp", Encoding.ASCII.GetString(videoBytes, 4, 4));
            await File.WriteAllBytesAsync(
                localVideoPath,
                videoBytes,
                TestContext.Current.CancellationToken);

            DateTime startedAt = DateTime.Now.AddSeconds(-2);
            long localRecordId = workstationDatabase.InsertVideoRecord(
                "LOOPBACK-TRACKING-001",
                "退货",
                "mpeg4",
                "test-fixture",
                localVideoPath,
                startedAt);
            workstationDatabase.UpdateVideoRecordOnStop(
                localRecordId,
                startedAt.AddSeconds(1),
                1,
                videoBytes.LongLength,
                "手动");

            using var transferStore = new RecordingTransferQueueStore(workstationDatabasePath);
            using var transferService = new RecordingTransferService(
                transferStore,
                workstationDatabase,
                () => workstationConfig);

            Assert.Equal(0, transferService.EnqueueCompletedRecordings());
            RecordingTransferSummary unboundSummary = transferStore.GetSummary();
            Assert.Equal(0, unboundSummary.PendingCount);
            Assert.Equal(0, unboundSummary.UploadingCount);
            Assert.Equal(0, unboundSummary.FailedCount);
            Assert.Empty(transferStore.GetReady(DateTime.UtcNow));
            Assert.True(File.Exists(localVideoPath));

            string connectionLink =
                $"http://127.0.0.1:{hostConfig.WebServerPort}/?key={hostConfig.WebAccessKey}";
            WorkstationNetwork.ParseHostConnectionInput(
                connectionLink,
                out string hostAddress,
                out string parsedAccessKey);
            PackingProofNodeInfo hostNode = Assert.IsType<PackingProofNodeInfo>(
                await WorkstationNetwork.GetNodeInfoAsync(
                    hostAddress,
                    TestContext.Current.CancellationToken));
            Assert.True(hostNode.IsValidHost);
            Assert.Equal(hostConfig.NodeId, hostNode.NodeId);
            Assert.Equal(hostConfig.WebAccessKey, parsedAccessKey);

            BackupDeviceEnrollmentResult enrollment = await WorkstationNetwork.EnrollBackupDeviceAsync(
                hostNode.Address,
                workstationConfig.NodeId,
                workstationConfig.NodeName,
                "pc",
                TestContext.Current.CancellationToken);
            Assert.Equal(hostConfig.NodeId, enrollment.ComputerId);
            Assert.Equal(BackupRequestAuthentication.CurrentVersion, enrollment.AuthVersion);

            workstationConfig.LastKnownHostNodeId = hostNode.NodeId;
            workstationConfig.LastKnownHostNodeName = hostNode.NodeName;
            workstationConfig.LastKnownHostAddress = hostNode.Address;
            workstationConfig.LastKnownHostAccessKey = enrollment.DeviceToken;
            workstationConfig.LastKnownHostBackupAuthVersion =
                BackupRequestAuthentication.CurrentVersion;

            Assert.Equal(1, transferService.EnqueueCompletedRecordings());
            RecordingTransferTask queued = Assert.Single(
                transferStore.GetReady(DateTime.UtcNow));
            Assert.Equal(localRecordId, queued.LocalVideoRecordId);
            Assert.Equal(hostConfig.NodeId, queued.TargetNodeId);
            Assert.Equal(hostNode.Address, queued.TargetAddress);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            Assert.Equal(1, await transferService.ProcessReadyOnceAsync(timeout.Token));

            RecordingTransferTask uploaded = Assert.Single(
                transferStore.GetUploadedWithLocalCache());
            Assert.Equal(RecordingTransferStates.Uploaded, uploaded.State);
            Assert.True(uploaded.RemoteVideoRecordId > 0);
            Assert.Equal(BackupRequestAuthentication.CurrentVersion, uploaded.VerificationVersion);
            Assert.NotEmpty(uploaded.VerificationReceipt);
            Assert.True(File.Exists(localVideoPath));
            Assert.True(await transferService.VerifyRemoteRecordForCleanupAsync(
                uploaded,
                new FileInfo(localVideoPath).Length,
                timeout.Token));

            VideoRecord localRecord = Assert.IsType<VideoRecord>(
                workstationDatabase.GetVideoById(localRecordId));
            Assert.Equal("Uploaded", localRecord.StorageState);
            Assert.Equal(uploaded.RemoteVideoRecordId, localRecord.RemoteVideoRecordId);

            VideoRecord hostRecord = Assert.IsType<VideoRecord>(
                hostDatabase.GetVideoById(uploaded.RemoteVideoRecordId!.Value));
            Assert.Equal("external", hostRecord.SourceType);
            Assert.Equal("pc", hostRecord.SourceDeviceKind);
            Assert.Equal("退货", hostRecord.Mode);
            Assert.Equal(workstationConfig.NodeId, hostRecord.SourceDeviceId);
            // 上传会话标识跟录像自己的 GUID 走，不再用"节点:本地自增ID"（本地库重建会与旧绑定撞车）。
            Assert.Equal(localRecord.SourceSessionId, hostRecord.SourceSessionId);
            Assert.Matches("^[0-9a-f]{32}$", hostRecord.SourceSessionId);
            Assert.True(File.Exists(hostRecord.FilePath));
            Assert.StartsWith(
                Path.GetFullPath(hostStoragePath) + Path.DirectorySeparatorChar,
                Path.GetFullPath(hostRecord.FilePath),
                StringComparison.OrdinalIgnoreCase);

            string localSha256 = await ComputeSha256Async(
                localVideoPath,
                TestContext.Current.CancellationToken);
            string hostSha256 = await ComputeSha256Async(
                hostRecord.FilePath,
                TestContext.Current.CancellationToken);
            Assert.Equal(localSha256, hostSha256);
            Assert.Equal(localSha256, uploaded.FileSha256);
            Assert.Equal(localSha256, hostRecord.ContentSha256);
        }
        finally
        {
            await DeleteTempDirectoryAsync(root);
        }
    }

    private static AppConfig CreateHostConfig(
        string nodeId,
        string accessKey,
        int port,
        string storagePath)
    {
        var config = new AppConfig
        {
            DeploymentPreset = DeploymentPresets.RecordingHost,
            DeploymentSchemaVersion = DeploymentPresets.CurrentSchemaVersion,
            NodeId = nodeId,
            NodeName = "回环备份主机",
            WebServerPort = port,
            RequireWebAccessKey = false,
            WebAccessKey = accessKey,
            StorageLocations = [new StorageLocation { Path = storagePath }]
        };
        AppConfig.NormalizeAfterLoad(config);
        return config;
    }

    private static AppConfig CreateWorkstationConfig(
        string nodeId,
        int port,
        string storagePath)
    {
        var config = new AppConfig
        {
            DeploymentPreset = DeploymentPresets.RecordingWorkstation,
            DeploymentSchemaVersion = DeploymentPresets.CurrentSchemaVersion,
            NodeId = nodeId,
            NodeName = "回环录制工位",
            RecordingWorkstationActivatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            WebServerPort = port,
            StorageLocations = [new StorageLocation { Path = storagePath }]
        };
        AppConfig.NormalizeAfterLoad(config);
        return config;
    }

    private static async Task<string> ComputeSha256Async(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<HttpResponseMessage> SendSignedAsync(
        string hostAddress,
        string path,
        HttpMethod method,
        string deviceId,
        string deviceKind,
        string credential,
        CancellationToken cancellationToken)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        string contentHash = BackupRequestAuthentication.ComputeContentHash([]);
        var request = new HttpRequestMessage(
            method,
            $"{hostAddress.TrimEnd('/')}{path}");
        if (method != HttpMethod.Get)
            request.Content = new ByteArrayContent([]);
        request.Headers.TryAddWithoutValidation("X-EPM-Device-Id", deviceId);
        request.Headers.TryAddWithoutValidation("X-EPM-Device-Kind", deviceKind);
        request.Headers.TryAddWithoutValidation(
            BackupRequestAuthentication.VersionHeader,
            BackupRequestAuthentication.CurrentVersion.ToString());
        request.Headers.TryAddWithoutValidation(
            BackupRequestAuthentication.TimestampHeader,
            timestamp.ToString());
        request.Headers.TryAddWithoutValidation(BackupRequestAuthentication.NonceHeader, nonce);
        request.Headers.TryAddWithoutValidation(
            BackupRequestAuthentication.ContentHashHeader,
            contentHash);
        request.Headers.TryAddWithoutValidation(
            BackupRequestAuthentication.SignatureHeader,
            BackupRequestAuthentication.CreateRequestSignature(
                credential,
                method.Method,
                path,
                timestamp,
                nonce,
                contentHash,
                deviceId));
        using var client = new HttpClient(
            WorkstationNetwork.CreateLanHttpMessageHandler(),
            disposeHandler: true);
        return await client.SendAsync(request, cancellationToken);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"epm-recording-workstation-loopback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task DeleteTempDirectoryAsync(string path)
    {
        SqliteTestPool.ClearPoolFor(path);
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (
                attempt < 4
                && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(attempt * 50));
            }
        }
    }

    private static int GetFreeTcpPort() =>
        TestPortAllocator.GetFreeTcpPort();

}
