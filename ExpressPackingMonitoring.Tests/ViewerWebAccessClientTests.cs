using System.Net;
using System.Net.Sockets;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

[Collection("Web server tests")]
public sealed class ViewerWebAccessClientTests
{
    [Theory]
    [InlineData("http://192.168.1.20:5280/", true)]
    [InlineData("http://192.168.1.20:5280", true)]
    [InlineData("http://192.168.1.20:5280/index.html", false)]
    [InlineData("http://192.168.1.20:5280/web/", false)]
    [InlineData("http://192.168.1.20:5280/?key=abc", false)]
    [InlineData("http://192.168.1.20:5280/#frag", false)]
    [InlineData("http://192.168.1.20:5281/", false)]
    public void WebRootAcceptsOnlyStrictRoot(string finalUrl, bool expected)
    {
        Assert.Equal(
            expected,
            WorkstationNetwork.IsWebRoot(new Uri(finalUrl), "http://192.168.1.20:5280/"));
    }

    [Theory]
    [InlineData("192.168.1.20:5280", null, "http://192.168.1.20:5280")]
    [InlineData("192.168.1.20:5280", "abc 123", "http://192.168.1.20:5280/?key=abc%20123")]
    public void WebAccessUrlBuildsKeyedUrl(string address, string? key, string expected)
    {
        Assert.Equal(expected, WorkstationNetwork.BuildWebAccessUrl(address, key));
    }

    [Fact]
    public async Task ProbeAcceptsKeyedRootAndRejectsMissingKey()
    {
        const string accessKey = "secret-web-access-key";
        string directory = Path.Combine(Path.GetTempPath(), $"epm-viewer-probe-{Guid.NewGuid():N}");
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
                mobileBackupComputerId: Guid.NewGuid().ToString("D"),
                mobileBackupStateDirectory: Path.Combine(directory, "uploads"),
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"),
                nodeId: Guid.NewGuid().ToString("D"),
                nodeName: "查看端测试主机",
                deploymentPreset: DeploymentPresets.MobileBackupHost);
            server.Start();

            WorkstationNetwork.WebAccessProbeResult authorized =
                await WorkstationNetwork.ProbeWebAccessAsync(
                    $"127.0.0.1:{port}",
                    accessKey,
                    TestContext.Current.CancellationToken);
            WorkstationNetwork.WebAccessProbeResult denied =
                await WorkstationNetwork.ProbeWebAccessAsync(
                    $"127.0.0.1:{port}",
                    null,
                    TestContext.Current.CancellationToken);

            Assert.Equal(WorkstationNetwork.WebAccessProbeResult.Authorized, authorized);
            Assert.Equal(WorkstationNetwork.WebAccessProbeResult.Unauthorized, denied);
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
