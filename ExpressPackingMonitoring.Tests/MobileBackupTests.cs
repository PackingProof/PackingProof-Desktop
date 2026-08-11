using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

[Collection("Web server tests")]
public sealed class MobileBackupTests
{
    private const string AccessKey = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void UploadSynchronizationUsesBoundedLockStripes()
    {
        int[] indexes = Enumerable.Range(0, 10000)
            .Select(index => MobileBackupService.GetUploadLockStripeIndex($"upload-{index}"))
            .ToArray();

        Assert.All(indexes, index => Assert.InRange(index, 0, MobileBackupService.UploadLockStripeCount - 1));
        Assert.True(indexes.Distinct().Count() <= MobileBackupService.UploadLockStripeCount);
    }

    [Fact]
    public void DeviceEnrollmentRotatesTokenAndPersistsEncryptedCredential()
    {
        string directory = CreateTempDirectory();
        try
        {
            var service = new BackupPairingTokenService(directory, AccessKey);
            BackupDeviceEnrollment first = service.Enroll("pc-node-1", "pc");
            BackupDeviceEnrollment rotated = service.Enroll("pc-node-1", "pc");
            Assert.NotEqual(first.DeviceCredential, rotated.DeviceCredential);
            Assert.True(service.TryGetDeviceCredential("pc-node-1", out string credential));
            Assert.Equal(rotated.DeviceCredential, credential);

            string storedJson = File.ReadAllText(
                Path.Combine(directory, "backup-device-credentials.json"),
                Encoding.UTF8);
            Assert.DoesNotContain(rotated.DeviceCredential, storedJson, StringComparison.Ordinal);

            var restarted = new BackupPairingTokenService(directory, AccessKey);
            Assert.True(restarted.TryGetDeviceCredential("pc-node-1", out string restored));
            Assert.Equal(rotated.DeviceCredential, restored);
            Assert.True(restarted.TryGetDeviceCredential("pc-node-1", out _, out string deviceKind));
            Assert.Equal("pc", deviceKind);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void DeviceCredentialEncryptionRootDoesNotDependOnWebAccessKey()
    {
        string directory = CreateTempDirectory();
        try
        {
            var service = new BackupPairingTokenService(directory, AccessKey);
            BackupDeviceEnrollment enrolled = service.Enroll("pc-node-1", "pc");
            var restarted = new BackupPairingTokenService(directory, "different-web-key");
            Assert.True(restarted.TryGetDeviceCredential("pc-node-1", out string restored));
            Assert.Equal(enrolled.DeviceCredential, restored);
            Assert.True(File.Exists(Path.Combine(directory, "backup-device-root.key")));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void CleanupExpiredUploads_DoesNotDeleteNonUploadStateJson()
    {
        string directory = CreateTempDirectory();
        try
        {
            string stateDirectory = Path.Combine(directory, "state");
            Directory.CreateDirectory(stateDirectory);

            string credentialPath = Path.Combine(stateDirectory, "backup-device-credentials.json");
            File.WriteAllText(credentialPath, "[]");
            File.SetLastWriteTimeUtc(credentialPath, DateTime.UtcNow.AddDays(-10));

            string receiversPath = Path.Combine(stateDirectory, "order-receivers.json");
            File.WriteAllText(receiversPath, "[]");
            File.SetLastWriteTimeUtc(receiversPath, DateTime.UtcNow.AddDays(-10));

            string uploadStatePath = Path.Combine(stateDirectory, new string('a', 64) + ".json");
            File.WriteAllText(
                uploadStatePath,
                JsonSerializer.Serialize(new MobileBackupUploadState
                {
                    UploadId = new string('a', 64),
                    UpdatedAtUtc = DateTime.UtcNow.AddDays(-10)
                }));
            File.SetLastWriteTimeUtc(uploadStatePath, DateTime.UtcNow.AddDays(-10));

            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            new MobileBackupService(
                database,
                stateDirectory,
                () => Path.Combine(directory, "recordings"),
                _ => null);
            // 构造函数内执行过期上传清理。

            Assert.True(File.Exists(credentialPath), "设备凭据文件不应被上传状态清理误删");
            Assert.True(File.Exists(receiversPath), "订单接收器状态文件不应被上传状态清理误删");
            Assert.False(File.Exists(uploadStatePath), "过期的真实上传状态文件应被清理");
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void DeviceCredentialsSurviveExpiredUploadCleanupAcrossRestart()
    {
        string directory = CreateTempDirectory();
        try
        {
            string stateDirectory = Path.Combine(directory, "state");
            var tokenService = new BackupPairingTokenService(stateDirectory, AccessKey);
            BackupDeviceEnrollment enrolled = tokenService.Enroll("pc-node-restart", "pc");

            string credentialPath = Path.Combine(stateDirectory, "backup-device-credentials.json");
            File.SetLastWriteTimeUtc(credentialPath, DateTime.UtcNow.AddDays(-10));
            File.SetLastWriteTimeUtc(
                Path.Combine(stateDirectory, "backup-device-root.key"),
                DateTime.UtcNow.AddDays(-10));

            string uploadStatePath = Path.Combine(stateDirectory, new string('b', 64) + ".json");
            File.WriteAllText(
                uploadStatePath,
                JsonSerializer.Serialize(new MobileBackupUploadState
                {
                    UploadId = new string('b', 64),
                    UpdatedAtUtc = DateTime.UtcNow.AddDays(-10)
                }));
            File.SetLastWriteTimeUtc(uploadStatePath, DateTime.UtcNow.AddDays(-10));

            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            new MobileBackupService(
                database,
                stateDirectory,
                () => Path.Combine(directory, "recordings"),
                _ => null);
            // 构造函数内执行过期上传清理。

            Assert.True(File.Exists(credentialPath), "设备凭据文件不应被上传状态清理误删");
            var restarted = new BackupPairingTokenService(stateDirectory, AccessKey);
            Assert.True(restarted.TryGetDeviceCredential("pc-node-restart", out string credential));
            Assert.Equal(enrolled.DeviceCredential, credential);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void LegacyCacheMobileBackupStateMigratesToDurableStateDirectory()
    {
        string directory = CreateTempDirectory();
        try
        {
            string legacyDirectory = Path.Combine(directory, "cache", "mobile-backup");
            string destinationDirectory = Path.Combine(directory, "mobile-backup-state");
            Directory.CreateDirectory(legacyDirectory);

            var tokenService = new BackupPairingTokenService(legacyDirectory, AccessKey);
            BackupDeviceEnrollment enrolled = tokenService.Enroll("pc-node-migrate", "pc");
            File.WriteAllText(Path.Combine(legacyDirectory, "order-receivers.json"), "[]");
            string revisionDirectory = Path.Combine(legacyDirectory, "userscript-config");
            Directory.CreateDirectory(revisionDirectory);
            File.WriteAllText(
                Path.Combine(revisionDirectory, "revision.json"),
                "{\"Fingerprint\":\"fp\",\"Revision\":3}");
            string uploadStatePath = Path.Combine(legacyDirectory, new string('c', 64) + ".json");
            File.WriteAllText(
                uploadStatePath,
                JsonSerializer.Serialize(new MobileBackupUploadState
                {
                    UploadId = new string('c', 64),
                    UpdatedAtUtc = DateTime.UtcNow
                }));

            AppPaths.MigrateMobileBackupState(legacyDirectory, destinationDirectory);

            Assert.False(
                File.Exists(Path.Combine(legacyDirectory, "backup-device-credentials.json")),
                "迁移后旧 cache 目录不应再保留凭据文件");
            Assert.True(
                File.Exists(Path.Combine(destinationDirectory, "backup-device-credentials.json")));
            Assert.True(File.Exists(Path.Combine(destinationDirectory, "backup-device-root.key")));
            Assert.True(File.Exists(Path.Combine(destinationDirectory, "order-receivers.json")));
            Assert.True(
                File.Exists(Path.Combine(destinationDirectory, "userscript-config", "revision.json")));
            Assert.True(File.Exists(Path.Combine(destinationDirectory, new string('c', 64) + ".json")));

            var restarted = new BackupPairingTokenService(destinationDirectory, AccessKey);
            Assert.True(restarted.TryGetDeviceCredential("pc-node-migrate", out string credential));
            Assert.Equal(enrolled.DeviceCredential, credential);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task IncompatibleEnrollmentIsRejectedBeforeHostApproval()
    {
        string directory = CreateTempDirectory();
        int port = GetFreeTcpPort();
        int approvalCount = 0;
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupComputerId: Guid.NewGuid().ToString("D"),
                mobileBackupStateDirectory: Path.Combine(directory, "state"),
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"),
                deploymentPreset: DeploymentPresets.RecordingHost,
                backupDeviceEnrollmentApprover: _ =>
                {
                    approvalCount++;
                    return BackupDeviceEnrollmentApprovalDecision.Approved;
                });
            server.Start();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                "/api/mobile-backup/enroll",
                new { deviceId = "obsolete-mobile-device", deviceName = "旧手机", deviceKind = "mobile" },
                TestContext.Current.CancellationToken);

            Assert.Equal((HttpStatusCode)426, response.StatusCode);
            Assert.Equal(0, approvalCount);
            using JsonDocument body = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("backup_client_upgrade_required", body.RootElement.GetProperty("errorCode").GetString());
            Assert.Equal("mobile", body.RootElement.GetProperty("updateTarget").GetString());
            Assert.Equal("0.5.10", body.RootElement.GetProperty("minimumVersion").GetString());
            Assert.Equal(11010, body.RootElement.GetProperty("minimumBuildNumber").GetInt32());
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task WebServerDispose_DefersSharedResourcesUntilActiveRequestExits()
    {
        string directory = CreateTempDirectory();
        int port = GetFreeTcpPort();
        using var approvalEntered = new ManualResetEventSlim();
        using var releaseApproval = new ManualResetEventSlim();
        WebServer? server = null;
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupComputerId: Guid.NewGuid().ToString("D"),
                mobileBackupStateDirectory: Path.Combine(directory, "state"),
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"),
                deploymentPreset: DeploymentPresets.RecordingHost,
                backupDeviceEnrollmentApprover: _ =>
                {
                    approvalEntered.Set();
                    releaseApproval.Wait(TestContext.Current.CancellationToken);
                    return BackupDeviceEnrollmentApprovalDecision.Approved;
                })
            {
                ShutdownWaitTimeout = TimeSpan.FromMilliseconds(50)
            };
            server.Start();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            Task<HttpResponseMessage> request = client.PostAsJsonAsync(
                "/api/mobile-backup/enroll",
                CreateCompatibleEnrollment("shutdown-race-device", "测试手机"),
                TestContext.Current.CancellationToken);
            Assert.True(approvalEntered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

            server.Dispose();
            Assert.False(server.ServerResourcesDisposedForTesting);

            releaseApproval.Set();
            try
            {
                using HttpResponseMessage _ = await request.WaitAsync(
                    TimeSpan.FromSeconds(2),
                    TestContext.Current.CancellationToken);
            }
            catch (HttpRequestException)
            {
                // Closing the listener may terminate the client response while the server request exits safely.
            }
            Assert.True(SpinWait.SpinUntil(
                () => server.ServerResourcesDisposedForTesting,
                TimeSpan.FromSeconds(2)));
        }
        finally
        {
            releaseApproval.Set();
            server?.Dispose();
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task DeviceEnrollmentWithoutApprovalUiReturnsUnavailable()
    {
        string directory = CreateTempDirectory();
        int port = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupComputerId: Guid.NewGuid().ToString("D"),
                mobileBackupStateDirectory: Path.Combine(directory, "state"),
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"),
                deploymentPreset: DeploymentPresets.RecordingHost);
            server.Start();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                "/api/mobile-backup/enroll",
                CreateCompatibleEnrollment("approval-required-device", "测试手机"),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using JsonDocument body = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("enrollment_approval_unavailable", body.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task DeviceEnrollmentReturnsDeniedOnlyForExplicitRejection()
    {
        string directory = CreateTempDirectory();
        int port = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupComputerId: Guid.NewGuid().ToString("D"),
                mobileBackupStateDirectory: Path.Combine(directory, "state"),
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"),
                deploymentPreset: DeploymentPresets.RecordingHost,
                backupDeviceEnrollmentApprover: _ => BackupDeviceEnrollmentApprovalDecision.Denied);
            server.Start();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            using HttpResponseMessage response = await client.PostAsJsonAsync(
                "/api/mobile-backup/enroll",
                CreateCompatibleEnrollment("explicitly-denied-device", "测试手机"),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using JsonDocument body = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("enrollment_denied", body.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task ApprovalExceptionReturnsUnavailableInsteadOfDenied()
    {
        string directory = CreateTempDirectory();
        int port = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupComputerId: Guid.NewGuid().ToString("D"),
                mobileBackupStateDirectory: Path.Combine(directory, "state"),
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"),
                deploymentPreset: DeploymentPresets.RecordingHost,
                backupDeviceEnrollmentApprover: _ => throw new InvalidOperationException("prompt failed"));
            server.Start();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            using HttpResponseMessage response = await client.PostAsJsonAsync(
                "/api/mobile-backup/enroll",
                CreateCompatibleEnrollment("approval-error-device", "测试手机"),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using JsonDocument body = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("enrollment_approval_unavailable", body.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task ConcurrentEnrollmentRequestsShareOneApprovalAndCredential()
    {
        string directory = CreateTempDirectory();
        int port = GetFreeTcpPort();
        int approvalCount = 0;
        using var approvalEntered = new ManualResetEventSlim();
        using var releaseApproval = new ManualResetEventSlim();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupComputerId: Guid.NewGuid().ToString("D"),
                mobileBackupStateDirectory: Path.Combine(directory, "state"),
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"),
                deploymentPreset: DeploymentPresets.RecordingHost,
                backupDeviceEnrollmentApprover: _ =>
                {
                    Interlocked.Increment(ref approvalCount);
                    approvalEntered.Set();
                    releaseApproval.Wait(TestContext.Current.CancellationToken);
                    return BackupDeviceEnrollmentApprovalDecision.Approved;
                });
            server.Start();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var request = CreateCompatibleEnrollment("duplicate-request-device", "测试手机");

            Task<HttpResponseMessage> first = client.PostAsJsonAsync(
                "/api/mobile-backup/enroll",
                request,
                TestContext.Current.CancellationToken);
            Assert.True(approvalEntered.Wait(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));
            await Task.Delay(800, TestContext.Current.CancellationToken);
            Task<HttpResponseMessage> second = client.PostAsJsonAsync(
                "/api/mobile-backup/enroll",
                request,
                TestContext.Current.CancellationToken);
            releaseApproval.Set();

            using HttpResponseMessage firstResponse = await first;
            using HttpResponseMessage secondResponse = await second;
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
            Assert.Equal(1, approvalCount);
            using JsonDocument firstBody = JsonDocument.Parse(
                await firstResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            using JsonDocument secondBody = JsonDocument.Parse(
                await secondResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(
                firstBody.RootElement.GetProperty("deviceToken").GetString(),
                secondBody.RootElement.GetProperty("deviceToken").GetString());
        }
        finally
        {
            releaseApproval.Set();
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task ImmediateEnrollmentRetryReusesApprovedCredentialWithoutAnotherPrompt()
    {
        string directory = CreateTempDirectory();
        int port = GetFreeTcpPort();
        int approvalCount = 0;
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupComputerId: Guid.NewGuid().ToString("D"),
                mobileBackupStateDirectory: Path.Combine(directory, "state"),
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"),
                deploymentPreset: DeploymentPresets.RecordingHost,
                backupDeviceEnrollmentApprover: _ =>
                {
                    Interlocked.Increment(ref approvalCount);
                    return BackupDeviceEnrollmentApprovalDecision.Approved;
                });
            server.Start();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var request = CreateCompatibleEnrollment("retry-request-device", "测试手机");

            using HttpResponseMessage first = await client.PostAsJsonAsync(
                "/api/mobile-backup/enroll",
                request,
                TestContext.Current.CancellationToken);
            using HttpResponseMessage retry = await client.PostAsJsonAsync(
                "/api/mobile-backup/enroll",
                request,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            Assert.Equal(1, approvalCount);
            using JsonDocument firstBody = JsonDocument.Parse(
                await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            using JsonDocument retryBody = JsonDocument.Parse(
                await retry.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(
                firstBody.RootElement.GetProperty("deviceToken").GetString(),
                retryBody.RootElement.GetProperty("deviceToken").GetString());
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task DifferentDeviceIsDeferredWhileOneApprovalIsActive()
    {
        string directory = CreateTempDirectory();
        int port = GetFreeTcpPort();
        int approvalCount = 0;
        using var approvalEntered = new ManualResetEventSlim();
        using var releaseApproval = new ManualResetEventSlim();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupComputerId: Guid.NewGuid().ToString("D"),
                mobileBackupStateDirectory: Path.Combine(directory, "state"),
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"),
                deploymentPreset: DeploymentPresets.RecordingHost,
                backupDeviceEnrollmentApprover: _ =>
                {
                    Interlocked.Increment(ref approvalCount);
                    approvalEntered.Set();
                    releaseApproval.Wait(TestContext.Current.CancellationToken);
                    return BackupDeviceEnrollmentApprovalDecision.Approved;
                });
            server.Start();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            Task<HttpResponseMessage> first = client.PostAsJsonAsync(
                "/api/mobile-backup/enroll",
                CreateCompatibleEnrollment("first-approval-device", "第一台手机"),
                TestContext.Current.CancellationToken);
            Assert.True(approvalEntered.Wait(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));

            using HttpResponseMessage second = await client.PostAsJsonAsync(
                "/api/mobile-backup/enroll",
                CreateCompatibleEnrollment("second-approval-device", "第二台手机"),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
            Assert.Equal("3", second.Headers.RetryAfter?.Delta?.TotalSeconds.ToString("0"));
            using JsonDocument body = JsonDocument.Parse(
                await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("enrollment_approval_busy", body.RootElement.GetProperty("errorCode").GetString());
            Assert.Equal(1, approvalCount);

            releaseApproval.Set();
            using HttpResponseMessage firstResponse = await first;
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        }
        finally
        {
            releaseApproval.Set();
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void ComputerIdIsGeneratedOnceAndThenRemainsStable()
    {
        var config = new AppConfig { WebAccessKey = AccessKey };

        Assert.True(AppConfig.NormalizeAfterLoad(config));
        string generated = config.MobileBackupComputerId;
        Assert.True(Guid.TryParse(generated, out _));
        Assert.False(AppConfig.NormalizeAfterLoad(config));
        Assert.Equal(generated, config.MobileBackupComputerId);
    }

    [Fact]
    public void ExistingVideoRowsMigrateToPcSource()
    {
        string directory = CreateTempDirectory();
        string databasePath = Path.Combine(directory, "videos.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE VideoRecords (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT, OrderId TEXT NOT NULL, Mode TEXT NOT NULL DEFAULT '',
                        VideoCodec TEXT DEFAULT '', VideoEncoder TEXT DEFAULT '', FilePath TEXT NOT NULL,
                        FileSizeBytes INTEGER DEFAULT 0, StartTime TEXT NOT NULL, EndTime TEXT,
                        DurationSeconds REAL DEFAULT 0, StopReason TEXT DEFAULT '', IsDeleted INTEGER DEFAULT 0,
                        DeletedAt TEXT, DeleteReason TEXT DEFAULT '', TrackingNumber TEXT DEFAULT '',
                        SourceOrderId TEXT DEFAULT '', BuyerMessage TEXT DEFAULT '', SellerMemo TEXT DEFAULT '',
                        ProductInfo TEXT DEFAULT '', OrderInfoPushTime TEXT, OrderInfoJson TEXT DEFAULT ''
                    );
                    INSERT INTO VideoRecords (OrderId, FilePath, StartTime) VALUES ('OLD-1', 'old.mp4', '2026-07-01 10:00:00');
                    """;
                command.ExecuteNonQuery();
            }

            using var database = new VideoDatabase(databasePath);
            VideoRecord migrated = Assert.Single(database.QueryVideos(null, null));
            Assert.Equal("pc", migrated.SourceType);
            Assert.Equal("", migrated.SourceDeviceId);
            Assert.Equal("", migrated.SourceSessionId);
            Assert.Equal("", migrated.ContentSha256);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void UploadResumesValidatesChunksAndCompletesIdempotentlyWithOrderSnapshot()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] file = Encoding.UTF8.GetBytes("mobile backup video payload");
            string fileSha = Sha256(file);
            var order = new OrderInfo
            {
                TrackingNumber = "TRACK-001",
                OrderId = "ORDER-001",
                BuyerMessage = "买家留言",
                SellerMemo = "卖家备注",
                ProductInfo = "商品 A",
                IsPrintedRefund = true,
                PushTime = DateTime.Now
            };
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            var service = CreateService(database, directory);
            MobileBackupCreateRequest createRequest = CreateRequest(fileSha, file.Length);

            MobileBackupCreateResult created = service.CreateOrResume(createRequest);
            Assert.Equal(0, created.Offset);
            Assert.Equal(4 * 1024 * 1024, created.ChunkSize);

            byte[] first = file[..8];
            Assert.Equal(8, service.AppendChunk(fileSha, 0, 7, file.Length, first, Sha256(first)));
            Assert.Equal(8, service.CreateOrResume(createRequest).Offset);
            Assert.Throws<MobileBackupOffsetException>(() =>
                service.AppendChunk(fileSha, 0, file.Length - 9, file.Length, file[8..], Sha256(file[8..])));
            Assert.Throws<MobileBackupValidationException>(() =>
                service.AppendChunk(fileSha, 8, file.Length - 1, file.Length, file[8..], new string('0', 64)));

            byte[] remaining = file[8..];
            Assert.Equal(file.Length, service.AppendChunk(fileSha, 8, file.Length - 1, file.Length, remaining, Sha256(remaining)));
            MobileBackupCompleteRequest completeRequest = CompleteRequest(fileSha, "session-1", "TRACK-001", "phone-1", "打包手机");
            completeRequest.Sessions = new List<MobileBackupSessionRequest>
            {
                new()
                {
                    SessionId = "session-1",
                    TrackingNumber = "TRACK-001",
                    StartedAt = completeRequest.StartedAt,
                    DurationMilliseconds = completeRequest.DurationMilliseconds,
                    Mode = "return",
                    OrderInfo = order
                }
            };
            MobileBackupCompleteResult completed = service.Complete(fileSha, completeRequest);
            MobileBackupCompleteResult repeated = service.Complete(fileSha, completeRequest);
            DateTime localStart = completeRequest.StartedAt.ToLocalTime().DateTime;

            Assert.Equal("verified", completed.Status);
            Assert.False(completed.AlreadyCompleted);
            Assert.True(repeated.AlreadyCompleted);
            Assert.Equal(completed.RecordId, repeated.RecordId);
            VideoRecord record = database.GetVideoById(completed.RecordId);
            Assert.Equal("external", record.SourceType);
            Assert.Equal("phone-1", record.SourceDeviceId);
            Assert.Equal("打包手机", record.SourceDeviceName);
            Assert.Equal("session-1", record.SourceSessionId);
            Assert.Equal(fileSha, record.ContentSha256);
            Assert.Equal("退货", record.Mode);
            Assert.Equal("买家留言", record.BuyerMessage);
            Assert.Equal("卖家备注", record.SellerMemo);
            Assert.Equal("商品 A", record.ProductInfo);
            Assert.True(File.Exists(record.FilePath));
            Assert.Equal(
                Path.Combine(
                    directory,
                    "recordings",
                    "手机备份",
                    "打包手机-PHONE1",
                    localStart.ToString("yyyy-MM-dd"),
                    $"TRACK-001_{localStart:yyyyMMdd_HHmmss}_退货.mp4"),
                record.FilePath);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void PcWorkstationUploadUsesSameVerifiedIdempotentProtocolAndRecordsSourceKind()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] file = Encoding.UTF8.GetBytes("pc workstation video payload");
            string sha = Sha256(file);
            string databasePath = Path.Combine(directory, "videos.db");
            using var database = new VideoDatabase(databasePath);
            var service = CreateService(database, directory);
            service.CreateOrResume(CreateRequest(sha, file.Length));
            service.AppendChunk(sha, 0, file.Length - 1, file.Length, file, sha);
            MobileBackupCompleteRequest request =
                CompleteRequest(sha, "pc-session-1", "PC-TRACK-1", "pc-node-1", "一号录制工位");
            request.SourceDeviceKind = "pc";

            MobileBackupCompleteResult completed = service.Complete(sha, request);
            MobileBackupCompleteResult repeated = service.Complete(sha, request);

            Assert.Equal("verified", completed.Status);
            Assert.True(repeated.AlreadyCompleted);
            Assert.Equal(completed.RecordId, repeated.RecordId);
            VideoRecord record = database.GetVideoById(completed.RecordId);
            Assert.Equal("pc-node-1", record.SourceDeviceId);
            Assert.Equal("一号录制工位", record.SourceDeviceName);
            Assert.Equal("pc", record.SourceDeviceKind);
            Assert.Equal("电脑工位上传", record.StopReason);
            Assert.Equal("发货", record.Mode);
            Assert.Contains(
                $"{Path.DirectorySeparatorChar}电脑上传{Path.DirectorySeparatorChar}",
                record.FilePath);

        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Theory]
    [InlineData("", "发货")]
    [InlineData("shipping", "发货")]
    [InlineData("unknown", "发货")]
    [InlineData("return", "退货")]
    [InlineData("退货", "退货")]
    public void ExternalRecordingModeSafelyNormalizes(string input, string expected)
    {
        Assert.Equal(expected, VideoDatabase.NormalizeRecordingMode(input));
    }

    [Fact]
    public void MissingExternalRecordingModeDefaultsToShipping()
    {
        Assert.Equal("发货", VideoDatabase.NormalizeRecordingMode(null!));
    }

    [Fact]
    public async Task ActiveUploadWaitsUntilBackupCompletes()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] file = Encoding.UTF8.GetBytes("active mobile backup payload");
            string fileSha = Sha256(file);
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            var service = CreateService(database, directory);
            var activity = new List<bool>();
            service.ActiveUploadsChanged += activity.Add;

            service.CreateOrResume(CreateRequest(fileSha, file.Length));
            Task idle = service.WaitForIdleAsync(TestContext.Current.CancellationToken);

            Assert.True(service.HasActiveUploads);
            Assert.False(idle.IsCompleted);

            service.AppendChunk(fileSha, 0, file.Length - 1, file.Length, file, fileSha);
            service.Complete(
                fileSha,
                CompleteRequest(fileSha, "session-active", "TRACK-ACTIVE", "phone-active", "手机"));

            await idle;
            Assert.False(service.HasActiveUploads);
            Assert.Equal([true, false], activity);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task ActiveUploadWaitCanBeCancelledWithoutClearingResumeState()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] file = Encoding.UTF8.GetBytes("cancelled wait payload");
            string fileSha = Sha256(file);
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            var service = CreateService(database, directory);
            service.CreateOrResume(CreateRequest(fileSha, file.Length));
            using var cancellation = new CancellationTokenSource();

            Task wait = service.WaitForIdleAsync(cancellation.Token);
            cancellation.Cancel();

            await Assert.ThrowsAsync<TaskCanceledException>(() => wait);
            Assert.True(service.HasActiveUploads);
            Assert.Equal(0, service.CreateOrResume(CreateRequest(fileSha, file.Length)).Offset);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void MergeOrderInfo_PrefersComputerFieldsAndPreservesMobileRefund()
    {
        var computer = new OrderInfo
        {
            TrackingNumber = "TRACK-1",
            BuyerMessage = "电脑留言",
            ProductInfo = "电脑商品",
            PushTime = new DateTime(2026, 7, 19)
        };
        var mobile = new OrderInfo
        {
            TrackingNumber = "TRACK-1",
            BuyerMessage = "手机留言",
            SellerMemo = "手机备注",
            HasRefund = true,
            IsPrintedRefund = true,
            RefundStatus = "退款处理中",
            PushTime = new DateTime(2026, 7, 20)
        };

        OrderInfo merged = MobileBackupService.MergeOrderInfo(computer, mobile, "TRACK-1")!;

        Assert.Equal("电脑留言", merged.BuyerMessage);
        Assert.Equal("手机备注", merged.SellerMemo);
        Assert.Equal("电脑商品", merged.ProductInfo);
        Assert.True(merged.HasRefund);
        Assert.True(merged.IsPrintedRefund);
        Assert.Equal("退款处理中", merged.RefundStatus);
        Assert.Equal(new DateTime(2026, 7, 20), merged.PushTime);
    }

    [Fact]
    public void DifferentContentWithSameBusinessNameUsesShortHashSuffix()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] file = Encoding.UTF8.GetBytes("new mobile video");
            string sha = Sha256(file);
            DateTime localStart = CompleteRequest(sha, "collision-session", "TRACK-001", "phone-1", "手机")
                .StartedAt.ToLocalTime().DateTime;
            string dateDirectory = Path.Combine(
                directory,
                "recordings",
                "手机备份",
                "手机-PHONE1",
                localStart.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(dateDirectory);
            File.WriteAllText(
                Path.Combine(dateDirectory, $"TRACK-001_{localStart:yyyyMMdd_HHmmss}_发货.mp4"),
                "existing video");
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            var service = CreateService(database, directory);
            service.CreateOrResume(CreateRequest(sha, file.Length));
            service.AppendChunk(sha, 0, file.Length - 1, file.Length, file, sha);

            MobileBackupCompleteResult completed = service.Complete(
                sha,
                CompleteRequest(sha, "collision-session", "TRACK-001", "phone-1", "手机"));

            Assert.Equal(
                $"TRACK-001_{localStart:yyyyMMdd_HHmmss}_发货_{sha[..8]}.mp4",
                Path.GetFileName(database.GetVideoById(completed.RecordId).FilePath));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void FullFileHashMismatchDeletesTemporaryUploadAndRestartsAtZero()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] expected = Encoding.UTF8.GetBytes("expected video");
            byte[] corrupted = Encoding.UTF8.GetBytes("corrupted data");
            Assert.Equal(expected.Length, corrupted.Length);
            string expectedSha = Sha256(expected);
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            var service = CreateService(database, directory);
            MobileBackupCreateRequest request = CreateRequest(expectedSha, expected.Length);
            service.CreateOrResume(request);
            service.AppendChunk(expectedSha, 0, corrupted.Length - 1, corrupted.Length, corrupted, Sha256(corrupted));

            Assert.Throws<MobileBackupFileHashException>(() =>
                service.Complete(expectedSha, CompleteRequest(expectedSha, "session-bad", "", "phone-1", "手机")));
            Assert.Equal(0, service.CreateOrResume(request).Offset);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void SameShaReusesPhysicalFileButCreatesIndependentSearchRecords()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] file = Encoding.UTF8.GetBytes("shared physical video");
            string sha = Sha256(file);
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            var service = CreateService(database, directory);
            service.CreateOrResume(CreateRequest(sha, file.Length));
            service.AppendChunk(sha, 0, file.Length - 1, file.Length, file, sha);
            MobileBackupCompleteResult first = service.Complete(sha, CompleteRequest(sha, "session-a", "TRACK-A", "phone-a", "手机 A"));

            Assert.True(service.CreateOrResume(CreateRequest(sha, file.Length)).FileReady);
            MobileBackupCompleteResult second = service.Complete(sha, CompleteRequest(sha, "session-b", "TRACK-B", "phone-b", "手机 B"));
            VideoRecord firstRecord = database.GetVideoById(first.RecordId);
            VideoRecord secondRecord = database.GetVideoById(second.RecordId);
            Assert.NotEqual(first.RecordId, second.RecordId);
            Assert.Equal(firstRecord.FilePath, secondRecord.FilePath);
            Assert.Equal("TRACK-A", firstRecord.TrackingNumber);
            Assert.Equal("TRACK-B", secondRecord.TrackingNumber);
            Assert.Equal(file.Length, database.GetTotalFileSizeBytes());
            Assert.Single(database.GetActiveStorageVideoFiles());
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void OnePhysicalFileCanCreateMultipleLogicalSessionRecords()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] file = Encoding.UTF8.GetBytes("multi session physical video");
            string sha = Sha256(file);
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            var service = CreateService(database, directory);
            service.CreateOrResume(CreateRequest(sha, file.Length));
            service.AppendChunk(sha, 0, file.Length - 1, file.Length, file, sha);
            var request = new MobileBackupCompleteRequest
            {
                FileSha256 = sha,
                SourceDeviceId = "phone-multi",
                SourceDeviceName = "打包手机",
                Sessions = new List<MobileBackupSessionRequest>
                {
                    new()
                    {
                        SessionId = "segment-1", TrackingNumber = "TRACK-1",
                        StartedAt = new DateTimeOffset(2026, 7, 19, 10, 0, 0, TimeSpan.FromHours(8)),
                        DurationMilliseconds = 5000
                    },
                    new()
                    {
                        SessionId = "segment-2", TrackingNumber = "TRACK-2",
                        StartedAt = new DateTimeOffset(2026, 7, 19, 10, 0, 5, TimeSpan.FromHours(8)),
                        DurationMilliseconds = 6000
                    }
                }
            };

            MobileBackupCompleteResult completed = service.Complete(sha, request);
            MobileBackupCompleteResult repeated = service.Complete(sha, request);

            Assert.Equal(2, completed.RecordIds.Count);
            Assert.True(repeated.AlreadyCompleted);
            Assert.Equal(completed.RecordIds, repeated.RecordIds);
            Assert.Equal(
                database.GetVideoById(completed.RecordIds[0]).FilePath,
                database.GetVideoById(completed.RecordIds[1]).FilePath);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void LaterOrderPushEnrichesOldExternalVideoWithoutPendingFlag()
    {
        string directory = CreateTempDirectory();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            long id = database.InsertMobileBackupRecord(
                "TRACK-LATE", Path.Combine(directory, "late.mp4"), 10, DateTime.Now.AddDays(-30), 5,
                "phone-1", "手机", "late-session", new string('a', 64));
            database.UpdateRecentVideoOrderInfos(new[]
            {
                new OrderInfo
                {
                    TrackingNumber = "TRACK-LATE",
                    BuyerMessage = "后来补全的留言",
                    ProductInfo = "后来补全的商品",
                    IsPrintedRefund = true,
                    PushTime = DateTime.Now
                }
            });

            VideoRecord enriched = database.GetVideoById(id);
            Assert.Equal("后来补全的留言", enriched.BuyerMessage);
            Assert.Equal("后来补全的商品", enriched.ProductInfo);
            Assert.Contains("IsPrintedRefund", enriched.OrderInfoJson, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task BackupApiRequiresDeviceTokenAndReturnsVerificationConfirmation()
    {
        string directory = CreateTempDirectory();
        int port = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            string pcVideoPath = Path.Combine(directory, "pc-recording.mp4");
            await File.WriteAllBytesAsync(
                pcVideoPath,
                TestMediaAssets.TinyValidMp4,
                TestContext.Current.CancellationToken);
            long pcVideoId = database.InsertVideoRecord(
                "PC-ORDER",
                "发货",
                "h264",
                "test",
                pcVideoPath,
                DateTime.Now.AddMinutes(-2));
            using var server = new WebServer(
                database,
                port,
                requireAccessKey: false,
                accessKey: AccessKey,
                listenerHost: "127.0.0.1",
                mobileBackupComputerId: "computer-1",
                mobileBackupComputerName: "打包电脑",
                mobileBackupStateDirectory: Path.Combine(directory, "state"),
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"),
                backupDeviceEnrollmentApprover: _ => BackupDeviceEnrollmentApprovalDecision.Approved);
            server.Start();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;

            using HttpResponseMessage missing = await client.GetAsync("/api/mobile-backup/capabilities", cancellationToken);
            using HttpResponseMessage queryOnly = await client.GetAsync($"/api/mobile-backup/capabilities?key={AccessKey}", cancellationToken);
            using var wrongRequest = new HttpRequestMessage(HttpMethod.Get, "/api/mobile-backup/capabilities");
            wrongRequest.Headers.Add("X-EPM-Access-Key", "wrong-key");
            using HttpResponseMessage wrong = await client.SendAsync(wrongRequest, cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, queryOnly.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

            const string deviceId = "phone-http-device";
            using HttpResponseMessage enrollment = await client.PostAsJsonAsync(
                "/api/mobile-backup/enroll",
                CreateCompatibleEnrollment(deviceId, "测试手机"),
                cancellationToken);
            Assert.Equal(HttpStatusCode.OK, enrollment.StatusCode);
            using JsonDocument enrollmentJson = JsonDocument.Parse(
                await enrollment.Content.ReadAsStringAsync(cancellationToken));
            string deviceToken = enrollmentJson.RootElement.GetProperty("deviceToken").GetString()!;
            Assert.Equal("测试手机", enrollmentJson.RootElement.GetProperty("deviceName").GetString());

            using HttpResponseMessage capabilities = await SendSignedAsync(
                client, HttpMethod.Get, "/api/mobile-backup/capabilities", deviceId, deviceToken, [], cancellationToken);
            using JsonDocument capabilityJson = JsonDocument.Parse(await capabilities.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal("mobile-backup-v2", capabilityJson.RootElement.GetProperty("protocol").GetString());
            Assert.Equal(2, capabilityJson.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(3, capabilityJson.RootElement.GetProperty("authVersion").GetInt32());
            Assert.True(capabilityJson.RootElement.GetProperty("features").GetProperty("videoLibrary").GetBoolean());
            Assert.Equal("host", capabilityJson.RootElement.GetProperty("features").GetProperty("libraryScope").GetString());
            Assert.True(capabilityJson.RootElement.GetProperty("features").GetProperty("deviceVideoClipping").GetBoolean());
            Assert.Equal(4 * 1024 * 1024, capabilityJson.RootElement.GetProperty("maxChunkBytes").GetInt32());

            byte[] file = TestMediaAssets.TinyValidMp4;
            string sha = Sha256(file);
            byte[] createBody = JsonSerializer.SerializeToUtf8Bytes(CreateRequest(sha, file.Length));
            using HttpResponseMessage create = await SendSignedAsync(
                client, HttpMethod.Post, "/api/mobile-backup/uploads", deviceId, deviceToken, createBody, cancellationToken);
            Assert.Equal(HttpStatusCode.OK, create.StatusCode);
            using var chunk = new HttpRequestMessage(HttpMethod.Put, $"/api/mobile-backup/uploads/{sha}/chunks")
            {
                Content = new ByteArrayContent(file)
            };
            chunk.Content.Headers.TryAddWithoutValidation("Content-Range", $"bytes 0-{file.Length - 1}/{file.Length}");
            chunk.Headers.Add("X-Chunk-SHA256", sha);
            AddSignedHeaders(chunk, deviceId, deviceToken, file);
            using HttpResponseMessage chunkResponse = await client.SendAsync(chunk, cancellationToken);
            Assert.Equal(HttpStatusCode.OK, chunkResponse.StatusCode);

            byte[] completeBodyBytes = JsonSerializer.SerializeToUtf8Bytes(
                CompleteRequest(sha, "http-session", "", "spoofed-device", "测试手机"));
            using HttpResponseMessage complete = await SendSignedAsync(
                client,
                HttpMethod.Post,
                $"/api/mobile-backup/uploads/{sha}/complete",
                deviceId,
                deviceToken,
                completeBodyBytes,
                cancellationToken);
            string completeBody = await complete.Content.ReadAsStringAsync(cancellationToken);
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
            using JsonDocument completeJson = JsonDocument.Parse(completeBody);
            Assert.Equal("电脑校验完成，备份成功", completeJson.RootElement.GetProperty("message").GetString());
            Assert.Equal("verified", completeJson.RootElement.GetProperty("status").GetString());

            using HttpResponseMessage videos = await SendSignedAsync(
                client, HttpMethod.Get, "/api/mobile-backup/videos?size=50", deviceId, deviceToken, [], cancellationToken);
            using JsonDocument videoJson = JsonDocument.Parse(await videos.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal(2, videoJson.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(1, videoJson.RootElement.GetProperty("deviceTotal").GetInt32());
            JsonElement video = videoJson.RootElement.GetProperty("data")
                .EnumerateArray()
                .Single(item => item.GetProperty("sourceDeviceId").GetString() == deviceId);
            long videoId = video.GetProperty("id").GetInt64();
            Assert.Equal(deviceId, video.GetProperty("sourceDeviceId").GetString());
            Assert.Equal("http-session", video.GetProperty("sourceSessionId").GetString());
            Assert.Equal(sha, video.GetProperty("contentSha256").GetString());
            JsonElement pcVideo = videoJson.RootElement.GetProperty("data")
                .EnumerateArray()
                .Single(item => item.GetProperty("id").GetInt64() == pcVideoId);
            Assert.Equal("pc", pcVideo.GetProperty("sourceType").GetString());
            Assert.Equal("", pcVideo.GetProperty("sourceDeviceId").GetString());
            Assert.Equal("h264", pcVideo.GetProperty("videoCodec").GetString());
            Assert.True(video.TryGetProperty("videoCodec", out _));
            using HttpResponseMessage pcVideoPlayback = await client.GetAsync(
                pcVideo.GetProperty("playUrl").GetString(),
                cancellationToken);
            Assert.Equal(HttpStatusCode.OK, pcVideoPlayback.StatusCode);
            string playUrl = video.GetProperty("playUrl").GetString()!;
            Assert.Contains("/api/mobile-backup/videos/", playUrl, StringComparison.Ordinal);
            Assert.Contains("?ticket=", playUrl, StringComparison.Ordinal);
            using HttpResponseMessage ticketPlayback = await client.GetAsync(playUrl, cancellationToken);
            Assert.Equal(HttpStatusCode.OK, ticketPlayback.StatusCode);
            using HttpResponseMessage noTicketPlayback = await client.GetAsync(
                $"/api/mobile-backup/videos/{videoId}/play",
                cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, noTicketPlayback.StatusCode);

            const string otherDeviceId = "phone-other-device";
            using HttpResponseMessage otherEnrollment = await client.PostAsJsonAsync(
                "/api/mobile-backup/enroll",
                CreateCompatibleEnrollment(otherDeviceId, "另一台手机"),
                cancellationToken);
            using JsonDocument otherEnrollmentJson = JsonDocument.Parse(
                await otherEnrollment.Content.ReadAsStringAsync(cancellationToken));
            string otherToken = otherEnrollmentJson.RootElement.GetProperty("deviceToken").GetString()!;
            using HttpResponseMessage otherVideos = await SendSignedAsync(
                client, HttpMethod.Get, "/api/mobile-backup/videos?size=50", otherDeviceId, otherToken, [], cancellationToken);
            using JsonDocument otherVideosJson = JsonDocument.Parse(
                await otherVideos.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal(2, otherVideosJson.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(0, otherVideosJson.RootElement.GetProperty("deviceTotal").GetInt32());
            using HttpResponseMessage crossDevicePlay = await SendSignedAsync(
                client,
                HttpMethod.Get,
                $"/api/mobile-backup/videos/{videoId}/play",
                otherDeviceId,
                otherToken,
                [],
                cancellationToken);
            Assert.Equal(HttpStatusCode.OK, crossDevicePlay.StatusCode);

            const string workstationId = "pc-workstation-device";
            using HttpResponseMessage workstationEnrollment = await client.PostAsJsonAsync(
                "/api/mobile-backup/enroll",
                CreateCompatibleEnrollment(workstationId, "录制工位", "pc"),
                cancellationToken);
            using JsonDocument workstationEnrollmentJson = JsonDocument.Parse(
                await workstationEnrollment.Content.ReadAsStringAsync(cancellationToken));
            string workstationToken = workstationEnrollmentJson.RootElement.GetProperty("deviceToken").GetString()!;
            using HttpResponseMessage workstationVideos = await SendSignedAsync(
                client,
                HttpMethod.Get,
                "/api/mobile-backup/videos?size=50",
                workstationId,
                workstationToken,
                [],
                cancellationToken);
            using JsonDocument workstationVideosJson = JsonDocument.Parse(
                await workstationVideos.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal(0, workstationVideosJson.RootElement.GetProperty("total").GetInt32());
            using HttpResponseMessage workstationCrossDevicePlay = await SendSignedAsync(
                client,
                HttpMethod.Get,
                $"/api/mobile-backup/videos/{videoId}/play",
                workstationId,
                workstationToken,
                [],
                cancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, workstationCrossDevicePlay.StatusCode);

            using HttpResponseMessage mobileStatus = await SendSignedAsync(
                client,
                HttpMethod.Get,
                $"/api/mobile-backup/videos/status?ids={videoId},{pcVideoId}",
                otherDeviceId,
                otherToken,
                [],
                cancellationToken);
            using JsonDocument mobileStatusJson = JsonDocument.Parse(
                await mobileStatus.Content.ReadAsStringAsync(cancellationToken));
            Assert.All(
                mobileStatusJson.RootElement.GetProperty("data").EnumerateArray(),
                item => Assert.Equal("available", item.GetProperty("status").GetString()));
            using HttpResponseMessage workstationStatus = await SendSignedAsync(
                client,
                HttpMethod.Get,
                $"/api/mobile-backup/videos/status?ids={videoId}",
                workstationId,
                workstationToken,
                [],
                cancellationToken);
            using JsonDocument workstationStatusJson = JsonDocument.Parse(
                await workstationStatus.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal(
                "missing",
                workstationStatusJson.RootElement.GetProperty("data")[0].GetProperty("status").GetString());
            using HttpResponseMessage crossDeviceAttestation = await SendSignedAsync(
                client,
                HttpMethod.Get,
                $"/api/mobile-backup/records/{videoId}/attestation",
                otherDeviceId,
                otherToken,
                [],
                cancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, crossDeviceAttestation.StatusCode);

            byte[] clipBody = JsonSerializer.SerializeToUtf8Bytes(new
            {
                startSeconds = 0,
                endSeconds = 1.0
            });
            using HttpResponseMessage clipStart = await SendSignedAsync(
                client,
                HttpMethod.Post,
                $"/api/mobile-backup/videos/{videoId}/clip",
                deviceId,
                deviceToken,
                clipBody,
                cancellationToken);
            string clipStartResponse = await clipStart.Content.ReadAsStringAsync(cancellationToken);
            Assert.True(
                clipStart.StatusCode == HttpStatusCode.OK,
                $"Expected clip task to start, response was {(int)clipStart.StatusCode}: {clipStartResponse}");
            using JsonDocument clipStartJson = JsonDocument.Parse(
                clipStartResponse);
            string clipTaskId = clipStartJson.RootElement.GetProperty("taskId").GetString()!;
            using HttpResponseMessage ownClipTask = await SendSignedAsync(
                client,
                HttpMethod.Get,
                $"/api/mobile-backup/clip-tasks/{clipTaskId}",
                deviceId,
                deviceToken,
                [],
                cancellationToken);
            Assert.Equal(HttpStatusCode.OK, ownClipTask.StatusCode);
            using HttpResponseMessage otherClipTask = await SendSignedAsync(
                client,
                HttpMethod.Get,
                $"/api/mobile-backup/clip-tasks/{clipTaskId}",
                otherDeviceId,
                otherToken,
                [],
                cancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, otherClipTask.StatusCode);
            using HttpResponseMessage workstationClip = await SendSignedAsync(
                client,
                HttpMethod.Post,
                $"/api/mobile-backup/videos/{videoId}/clip",
                workstationId,
                workstationToken,
                clipBody,
                cancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, workstationClip.StatusCode);

            using var obsoleteRequest = new HttpRequestMessage(HttpMethod.Get, "/api/mobile-backup/capabilities");
            obsoleteRequest.Headers.TryAddWithoutValidation(BackupRequestAuthentication.VersionHeader, "2");
            using HttpResponseMessage obsolete = await client.SendAsync(obsoleteRequest, cancellationToken);
            Assert.Equal((HttpStatusCode)426, obsolete.StatusCode);

            await Task.Delay(800, cancellationToken);
            using HttpResponseMessage rotatedEnrollment = await client.PostAsJsonAsync(
                "/api/mobile-backup/enroll",
                CreateCompatibleEnrollment(deviceId, "测试手机"),
                cancellationToken);
            using JsonDocument rotatedJson = JsonDocument.Parse(
                await rotatedEnrollment.Content.ReadAsStringAsync(cancellationToken));
            string rotatedToken = rotatedJson.RootElement.GetProperty("deviceToken").GetString()!;
            Assert.NotEqual(deviceToken, rotatedToken);
            using HttpResponseMessage oldTokenResponse = await SendSignedAsync(
                client, HttpMethod.Get, "/api/mobile-backup/capabilities", deviceId, deviceToken, [], cancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, oldTokenResponse.StatusCode);
            using HttpResponseMessage newTokenResponse = await SendSignedAsync(
                client, HttpMethod.Get, "/api/mobile-backup/capabilities", deviceId, rotatedToken, [], cancellationToken);
            Assert.Equal(HttpStatusCode.OK, newTokenResponse.StatusCode);
            HttpStatusCode finalEnrollmentStatus = HttpStatusCode.OK;
            for (int retry = 0; retry < 24 && finalEnrollmentStatus == HttpStatusCode.OK; retry++)
            {
                using HttpResponseMessage allowedRetry = await client.PostAsJsonAsync(
                    "/api/mobile-backup/enroll",
                    CreateCompatibleEnrollment(deviceId, "测试手机"),
                    cancellationToken);
                finalEnrollmentStatus = allowedRetry.StatusCode;
            }
            Assert.Equal((HttpStatusCode)429, finalEnrollmentStatus);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void DeviceDirectoryUsesReadableNameAndStableShortId()
    {
        Assert.Equal(
            "一号打包手机-ABCDEF",
            MobileBackupService.GetDeviceDirectoryName(
                "12345678-1234-1234-1234-1234569abcdef",
                "一号打包手机"));
        Assert.Equal(
            "手机-未知设备",
            MobileBackupService.GetDeviceDirectoryName("", ""));
    }

    [Fact]
    public async Task UserscriptUpdateIncludesKnownMobileOrderReceiverAddress()
    {
        string directory = CreateTempDirectory();
        int port = GetFreeTcpPort();
        string stateDirectory = Path.Combine(directory, "state");
        try
        {
            var receivers = new MobileOrderReceiverRegistry(Path.Combine(stateDirectory, "order-receivers.json"));
            receivers.Register(IPAddress.Parse("192.168.31.205"));
            string registryPath = Path.Combine(stateDirectory, "order-receivers.json");
            JsonArray registryEntries = JsonNode.Parse(File.ReadAllText(registryPath))!.AsArray();
            registryEntries[0]!["LastSeenUtc"] = DateTime.UtcNow.AddMinutes(-6);
            File.WriteAllText(registryPath, registryEntries.ToJsonString());

            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                requireAccessKey: false,
                accessKey: AccessKey,
                listenerHost: "127.0.0.1",
                mobileConnectionUrlProvider: () => $"http://192.168.31.250:{port}/?key={AccessKey}",
                mobileBackupStateDirectory: stateDirectory);
            server.Start();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            using JsonDocument activeDevices = await client.GetFromJsonAsync<JsonDocument>(
                "/api/recording-devices",
                TestContext.Current.CancellationToken) ?? throw new InvalidOperationException();
            using JsonDocument knownDevices = await client.GetFromJsonAsync<JsonDocument>(
                "/api/recording-devices?scope=known",
                TestContext.Current.CancellationToken) ?? throw new InvalidOperationException();
            Assert.Single(activeDevices.RootElement.GetProperty("devices").EnumerateArray());
            Assert.Equal(2, knownDevices.RootElement.GetProperty("devices").GetArrayLength());
            Assert.Contains(
                knownDevices.RootElement.GetProperty("devices").EnumerateArray(),
                device => device.GetProperty("address").GetString() == "http://192.168.31.205:5280"
                    && !device.GetProperty("online").GetBoolean());

            string script = await client.GetStringAsync(
                $"/kuaidizs-order-push.user.js?connect=127.0.0.1:{port}",
                TestContext.Current.CancellationToken);

            Assert.Contains($"\"url\":\"http://192.168.31.250:{port}\"", script);
            Assert.Contains("\"url\":\"http://192.168.31.205:5280\"", script);
            Assert.Contains("// @connect      192.168.31.205", script);
            Assert.DoesNotContain("// @connect      127.0.0.1", script);
            Assert.Contains(
                $"// @updateURL     http://192.168.31.250:{port}/kuaidizs-order-push.user.js",
                script);
            Assert.Contains(
                $"// @downloadURL   http://192.168.31.250:{port}/kuaidizs-order-push.user.js",
                script);
            Assert.DoesNotContain("// @updateURL     127.0.0.1", script);

            string versionLine = ExtractUserscriptVersion(script);
            Assert.Matches(@"^2\.12\.\d+$", versionLine);
            Assert.True(
                File.Exists(Path.Combine(stateDirectory, "userscript-config", "revision.json")),
                "配置修订号状态文件应位于状态目录子目录，避免被上传状态清理误删");
            string secondScript = await client.GetStringAsync(
                $"/kuaidizs-order-push.user.js?connect=127.0.0.1:{port}",
                TestContext.Current.CancellationToken);
            Assert.Equal(versionLine, ExtractUserscriptVersion(secondScript));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    private static string ExtractUserscriptVersion(string script)
    {
        Match match = Regex.Match(script, @"// @version\s+([^\r\n]+)");
        Assert.True(match.Success, "脚本缺少 @version 行");
        return match.Groups[1].Value.Trim();
    }

    [Fact]
    public async Task RecordingLibraryReusesWebPaginationAndStatusEndpoints()
    {
        string directory = CreateTempDirectory();
        int port = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            DateTime sharedTime = new(2026, 7, 19, 12, 0, 0);
            for (int index = 0; index < 23; index++)
            {
                DateTime startTime = index < 3 ? sharedTime : sharedTime.AddMinutes(-index);
                database.InsertMobileBackupRecord(
                    index == 17 ? "SEARCH-TARGET" : $"TRACK-{index:00}",
                    Path.Combine(directory, $"video-{index:00}.mp4"),
                    100 + index,
                    startTime,
                    5,
                    "phone-cursor",
                    "测试手机",
                    $"cursor-session-{index:00}",
                    index.ToString("x64"));
            }
            database.InsertMobileBackupRecord(
                "OTHER-PHONE",
                Path.Combine(directory, "other-phone.mp4"),
                200,
                sharedTime.AddMinutes(1),
                5,
                "phone-other",
                "手机2",
                "other-session",
                new string('f', 64));

            using var server = new WebServer(
                database,
                port,
                requireAccessKey: false,
                accessKey: AccessKey,
                listenerHost: "127.0.0.1",
                mobileBackupStateDirectory: Path.Combine(directory, "state"),
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"));
            server.Start();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            client.DefaultRequestHeaders.Add("X-EPM-Access-Key", AccessKey);
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;

            using JsonDocument first = JsonDocument.Parse(await client.GetStringAsync(
                "/api/videos?page=1&size=10&deviceId=phone-cursor", cancellationToken));
            Assert.Equal(23, first.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(23, first.RootElement.GetProperty("deviceTotal").GetInt32());
            Assert.Equal(1, first.RootElement.GetProperty("page").GetInt32());
            Assert.Equal(10, first.RootElement.GetProperty("data").GetArrayLength());
            long[] firstIds = first.RootElement.GetProperty("data").EnumerateArray()
                .Select(item => item.GetProperty("id").GetInt64()).ToArray();
            Assert.True(firstIds[0] > firstIds[1]);

            using JsonDocument second = JsonDocument.Parse(await client.GetStringAsync(
                "/api/videos?page=2&size=10&deviceId=phone-cursor", cancellationToken));
            long[] secondIds = second.RootElement.GetProperty("data").EnumerateArray()
                .Select(item => item.GetProperty("id").GetInt64()).ToArray();
            Assert.Equal(10, secondIds.Length);
            Assert.Empty(firstIds.Intersect(secondIds));

            using JsonDocument search = JsonDocument.Parse(await client.GetStringAsync(
                "/api/videos?page=1&size=10&keyword=SEARCH-TARGET&deviceId=phone-cursor", cancellationToken));
            Assert.Single(search.RootElement.GetProperty("data").EnumerateArray());
            Assert.Equal(1, search.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(1, search.RootElement.GetProperty("deviceTotal").GetInt32());

            using var allDevicesRequest = new HttpRequestMessage(
                HttpMethod.Get,
                "/api/videos?page=1&size=50");
            allDevicesRequest.Headers.Add("X-EPM-Device-Id", "phone-cursor");
            using HttpResponseMessage allDevicesResponse = await client.SendAsync(
                allDevicesRequest,
                cancellationToken);
            using JsonDocument allDevices = JsonDocument.Parse(
                await allDevicesResponse.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal(24, allDevices.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(23, allDevices.RootElement.GetProperty("deviceTotal").GetInt32());
            Assert.Contains(
                allDevices.RootElement.GetProperty("data").EnumerateArray(),
                item => item.GetProperty("sourceDeviceId").GetString() == "phone-other");

            using JsonDocument statuses = JsonDocument.Parse(await client.GetStringAsync(
                $"/api/videos/status?ids={firstIds[0]},999999", cancellationToken));
            JsonElement[] statusItems = statuses.RootElement.GetProperty("data").EnumerateArray().ToArray();
            Assert.Equal("missing", statusItems[0].GetProperty("status").GetString());
            Assert.False(statusItems[0].GetProperty("exists").GetBoolean());
            Assert.Equal("missing", statusItems[1].GetProperty("status").GetString());
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    private static MobileBackupService CreateService(VideoDatabase database, string directory, Func<string, OrderInfo?>? resolver = null) =>
        new(database, Path.Combine(directory, "state"), () => Path.Combine(directory, "recordings"), resolver);

    private static MobileBackupCreateRequest CreateRequest(string sha, long length) =>
        new() { FileSha256 = sha, TotalBytes = length, MimeType = "video/mp4" };

    private static MobileBackupCompleteRequest CompleteRequest(
        string sha, string sessionId, string trackingNumber, string deviceId, string deviceName) =>
        new()
        {
            FileSha256 = sha,
            SessionId = sessionId,
            TrackingNumber = trackingNumber,
            StartedAt = new DateTimeOffset(2026, 7, 19, 10, 0, 0, TimeSpan.FromHours(8)),
            DurationMilliseconds = 5000,
            SourceDeviceId = deviceId,
            SourceDeviceName = deviceName
        };

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static BackupDeviceEnrollmentRequest CreateCompatibleEnrollment(
        string deviceId,
        string deviceName,
        string deviceKind = "mobile") => new()
    {
        DeviceId = deviceId,
        DeviceName = deviceName,
        DeviceKind = deviceKind,
        ClientVersion = deviceKind == "pc" ? "0.0.32" : "0.5.10",
        ClientBuildNumber = deviceKind == "pc" ? 0 : 11010,
        BackupProtocol = "mobile-backup-v2",
        EnrollmentVersion = 2,
        AuthVersion = 3
    };

    private static async Task<HttpResponseMessage> SendSignedAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string deviceId,
        string credential,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, path);
        if (method != HttpMethod.Get)
        {
            request.Content = new ByteArrayContent(content);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }
        AddSignedHeaders(request, deviceId, credential, content);
        return await client.SendAsync(request, cancellationToken);
    }

    private static void AddSignedHeaders(
        HttpRequestMessage request,
        string deviceId,
        string credential,
        byte[] content)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        string contentHash = BackupRequestAuthentication.ComputeContentHash(content);
        string path = request.RequestUri?.OriginalString ?? "/";
        request.Headers.TryAddWithoutValidation("X-EPM-Device-Id", deviceId);
        request.Headers.TryAddWithoutValidation("X-EPM-Device-Kind", "mobile");
        request.Headers.TryAddWithoutValidation(BackupRequestAuthentication.VersionHeader, "3");
        request.Headers.TryAddWithoutValidation(BackupRequestAuthentication.TimestampHeader, timestamp.ToString());
        request.Headers.TryAddWithoutValidation(BackupRequestAuthentication.NonceHeader, nonce);
        request.Headers.TryAddWithoutValidation(BackupRequestAuthentication.ContentHashHeader, contentHash);
        request.Headers.TryAddWithoutValidation(
            BackupRequestAuthentication.SignatureHeader,
            BackupRequestAuthentication.CreateRequestSignature(
                credential,
                request.Method.Method,
                path,
                timestamp,
                nonce,
                contentHash,
                deviceId));
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"epm-mobile-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        SqliteTestPool.ClearPoolFor(path);
        try { Directory.Delete(path, recursive: true); } catch { }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
