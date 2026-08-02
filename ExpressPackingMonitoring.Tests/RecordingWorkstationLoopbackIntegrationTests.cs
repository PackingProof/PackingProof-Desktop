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

public sealed class RecordingWorkstationLoopbackIntegrationTests
{
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
            Assert.Equal(
                $"{workstationConfig.NodeId}:{localRecordId}",
                hostRecord.SourceSessionId);
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
            NodeName = "回环保存主机",
            EnableWebServer = true,
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
            EnableWebServer = true,
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
        SqliteConnection.ClearAllPools();
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

    private static int GetFreeTcpPort()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            int port;
            using (var tcpListener = new TcpListener(IPAddress.Loopback, 0))
            {
                tcpListener.Start();
                port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
            }

            using var httpListener = new HttpListener();
            httpListener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                httpListener.Start();
                return port;
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5)
            {
                // HTTP.sys may reserve an otherwise free TCP port for another URL ACL.
            }
        }

        throw new InvalidOperationException("Unable to find a loopback port available to HttpListener.");
    }

}
