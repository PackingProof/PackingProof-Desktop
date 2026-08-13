using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

[Collection("Web server tests")]
public sealed class ViewerWebAccessHostTests
{
    [Fact]
    public async Task NodeInfoExposesAccessProtectionState()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"epm-viewer-nodeinfo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        int port = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                requireAccessKey: true,
                accessKey: "secret-web-access-key",
                listenerHost: "127.0.0.1",
                mobileBackupComputerId: Guid.NewGuid().ToString("D"),
                mobileBackupStateDirectory: Path.Combine(directory, "uploads"),
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"),
                nodeId: Guid.NewGuid().ToString("D"),
                nodeName: "查看端测试主机",
                deploymentPreset: DeploymentPresets.MobileBackupHost);
            server.Start();

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            string body = await client.GetStringAsync(
                "/api/node-info",
                TestContext.Current.CancellationToken);
            using JsonDocument doc = JsonDocument.Parse(body);

            Assert.True(doc.RootElement.GetProperty("accessProtected").GetBoolean());
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ViewerEnrollmentReturnsWebAccessUrlAfterApproval()
    {
        const string accessKey = "secret-web-access-key";
        string expectedUrl = $"http://192.168.1.20:5280/?key={accessKey}";
        string directory = Path.Combine(Path.GetTempPath(), $"epm-viewer-enroll-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        int port = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                requireAccessKey: true,
                accessKey: accessKey,
                listenerHost: "127.0.0.1",
                mobileConnectionUrlProvider: () => expectedUrl,
                mobileBackupComputerId: Guid.NewGuid().ToString("D"),
                mobileBackupStateDirectory: Path.Combine(directory, "uploads"),
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"),
                nodeId: Guid.NewGuid().ToString("D"),
                nodeName: "查看端测试主机",
                deploymentPreset: DeploymentPresets.MobileBackupHost,
                backupDeviceEnrollmentApprover: _ => BackupDeviceEnrollmentApprovalDecision.Approved);
            server.Start();

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                "/api/mobile-backup/enroll",
                new
                {
                    deviceId = "viewer-device-0001",
                    deviceName = "Mac 查看端",
                    deviceKind = "viewer",
                    platform = "macos",
                    clientVersion = "0.0.49",
                    clientBuildNumber = 0,
                    backupProtocol = "mobile-backup-v2",
                    enrollmentVersion = 2,
                    authVersion = 3
                },
                TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using JsonDocument doc = JsonDocument.Parse(body);

            Assert.Equal(expectedUrl, doc.RootElement.GetProperty("webAccessUrl").GetString());
            Assert.Equal("Mac 查看端", doc.RootElement.GetProperty("deviceName").GetString());
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
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
