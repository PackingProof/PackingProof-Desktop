using System.Net;
using System.Net.Sockets;
using System.Text;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

[Collection("Web server tests")]
public sealed class RecordingComputerNicknameTests
{
    [Theory]
    [InlineData(DeploymentPresets.RecordingHost)]
    [InlineData(DeploymentPresets.RecordingWorkstation)]
    public void PcRecorderMigratesMachineNameToFirstAutomaticNickname(string preset)
    {
        var config = new AppConfig
        {
            DeploymentPreset = preset,
            NodeName = Environment.MachineName
        };

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal("电脑1", config.NodeName);
        Assert.False(config.NodeNameCustomized);
    }

    [Fact]
    public void PcRecorderPreservesLegacyCustomNickname()
    {
        var config = new AppConfig
        {
            DeploymentPreset = DeploymentPresets.RecordingHost,
            NodeName = "东侧打包台"
        };

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal("东侧打包台", config.NodeName);
        Assert.True(config.NodeNameCustomized);
    }

    [Fact]
    public void NonRecordingRoleKeepsMachineNameFallback()
    {
        var config = new AppConfig
        {
            DeploymentPreset = DeploymentPresets.ViewerClient,
            NodeName = ""
        };

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal(Environment.MachineName, config.NodeName);
        Assert.False(config.NodeNameCustomized);
    }

    [Fact]
    public void RegistryAssignsStableNumbersAndPersistsByNodeId()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "computer-nicknames.json");
        try
        {
            var firstRun = new RecordingComputerNicknameRegistry(path);
            firstRun.RegisterHost("host-node", "电脑1", customized: false);

            Assert.Equal("电脑2", firstRun.Assign("workstation-a", "电脑1", customized: false));
            Assert.Equal("电脑3", firstRun.Assign("workstation-b", Environment.MachineName, customized: false));

            var restarted = new RecordingComputerNicknameRegistry(path);
            Assert.Equal("电脑2", restarted.Assign("workstation-a", "电脑1", customized: false));
            Assert.Equal("电脑3", restarted.Assign("workstation-b", "电脑1", customized: false));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RegistryHonorsCustomNamesAndMakesDuplicatesDistinct()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "computer-nicknames.json");
        try
        {
            var registry = new RecordingComputerNicknameRegistry(path);

            Assert.Equal("打包电脑", registry.Assign("workstation-a", " 打包电脑 ", customized: true));
            Assert.Equal("打包电脑 2", registry.Assign("workstation-b", "打包电脑", customized: true));
            Assert.Equal("打包电脑", registry.Assign("workstation-a", "打包电脑", customized: true));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(" 电脑2 ", true, "电脑2")]
    [InlineData("", false, "")]
    [InlineData("123456789012345678901", false, "123456789012345678901")]
    [InlineData("电脑\n2", false, "电脑\n2")]
    public void SettingsValidatesComputerNickname(string value, bool expected, string normalized)
    {
        bool valid = SettingsWindow.TryNormalizeComputerNickname(value, out string actual);

        Assert.Equal(expected, valid);
        Assert.Equal(normalized, actual);
    }

    [Fact]
    public async Task RecordingHostHeartbeatAssignsNicknameAndPublishesItToDeviceCatalog()
    {
        string directory = CreateTemporaryDirectory();
        int port = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupStateDirectory: Path.Combine(directory, "host-state"),
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"),
                nodeId: "host-node-0001",
                nodeName: "电脑1",
                deploymentPreset: DeploymentPresets.RecordingHost);
            server.Start();

            RecordingWorkstationHeartbeatResult heartbeat =
                await WorkstationNetwork.SendRecordingWorkstationHeartbeatAsync(
                    $"127.0.0.1:{port}",
                    "workstation-node-0001",
                    "电脑1",
                    5299,
                    nicknameCustomized: false,
                    token: TestContext.Current.CancellationToken);

            Assert.True(heartbeat.Online);
            Assert.Equal("电脑2", heartbeat.AssignedDisplayName);
            ConnectedClientInfo client = Assert.Single(server.GetConnectedClients());
            Assert.Equal("电脑2", client.DisplayName);
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RecordingDeviceCatalogIncludesAssignedWorkstationNickname()
    {
        var client = new ConnectedClientInfo(
            "workstation-node-0001",
            "recording-workstation",
            "电脑2",
            "192.168.1.22",
            DateTimeOffset.UtcNow,
            "workstation-node-0001",
            "pc",
            5280,
            ["recording", "order-receiver"]);

        RecordingDeviceInfo workstation = Assert.Single(
            RecordingDeviceCatalog.Build(
                DeploymentPresets.RecordingHost,
                "host-node-0001",
                "电脑1",
                5280,
                "http://192.168.1.20:5280",
                [],
                [client]),
            device => device.NodeId == "workstation-node-0001");

        Assert.Equal("电脑2", workstation.NodeName);
        Assert.Equal("pc", workstation.DeviceType);
    }

    [Fact]
    public async Task WorkstationHeartbeatAcceptsOldHostWithoutAssignedName()
    {
        int port = GetFreeTcpPort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Task serverTask = Task.Run(async () =>
        {
            HttpListenerContext context = await listener.GetContextAsync()
                .WaitAsync(TestContext.Current.CancellationToken);
            byte[] response = Encoding.UTF8.GetBytes("{\"ok\":true}");
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = response.Length;
            await context.Response.OutputStream.WriteAsync(response);
            context.Response.Close();
        }, TestContext.Current.CancellationToken);

        RecordingWorkstationHeartbeatResult result =
            await WorkstationNetwork.SendRecordingWorkstationHeartbeatAsync(
                $"127.0.0.1:{port}",
                "workstation-node-0001",
                "电脑1",
                5280,
                token: TestContext.Current.CancellationToken);
        await serverTask;

        Assert.True(result.Online);
        Assert.Equal("", result.AssignedDisplayName);
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"epm-computer-name-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
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
