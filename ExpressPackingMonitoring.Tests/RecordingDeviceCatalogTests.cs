using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

[Collection("Web server tests")]
public sealed class RecordingDeviceCatalogTests
{
    [Fact]
    public void UserscriptTargetStatusUsesCompactMainWindowCopy()
    {
        var config = new AppConfig();
        Assert.Equal(
            "暂无订单接收设备",
            UserscriptTargetState.GetStatus(config, []).StatusText);
        Assert.Equal(
            "安装订单联动",
            UserscriptTargetState.GetStatus(config, []).ButtonText);

        var phone = new RecordingDeviceInfo
        {
            NodeId = "phone-1",
            NodeName = "手机1",
            DeviceType = "mobile",
            Address = "http://192.168.1.31:5280",
            Online = true
        };
        Assert.Equal(
            "未配置订单联动",
            UserscriptTargetState.GetStatus(config, [phone]).StatusText);
        Assert.Equal(
            "安装订单联动",
            UserscriptTargetState.GetStatus(config, [phone]).ButtonText);
    }

    [Fact]
    public void UserscriptTargetSignatureChangesOnlyForOrderReceiverAddresses()
    {
        var config = new AppConfig();
        var phone = new RecordingDeviceInfo
        {
            NodeId = "phone-1",
            NodeName = "手机1",
            DeviceType = "mobile",
            Address = "http://192.168.1.31:5280",
            Online = true
        };
        config.LastUserscriptTargetSignature = UserscriptTargetState.BuildSignature([phone]);

        phone.Online = false;
        phone.NodeName = "新的手机名称";
        phone.NodeId = "replacement-phone-id";
        phone.DeviceType = "future-recorder";
        UserscriptTargetStatus unchanged = UserscriptTargetState.GetStatus(config, [phone]);
        Assert.Equal("订单联动已就绪", unchanged.StatusText);

        phone.Address = "http://192.168.1.32:5280";
        UserscriptTargetStatus changed = UserscriptTargetState.GetStatus(config, [phone]);
        Assert.Equal("需要更新订单联动", changed.StatusText);
        Assert.Equal("安装订单联动", changed.ButtonText);
    }

    private static readonly string[] RecorderCapabilities =
    [
        PackingProofCapabilities.Recording,
        PackingProofCapabilities.OrderReceiver
    ];

    [Fact]
    public void RecordingHostAndOnlineMobileRecorderAreIncluded()
    {
        string hostNodeId = Guid.NewGuid().ToString("D");
        string mobileNodeId = Guid.NewGuid().ToString("D");

        IReadOnlyList<RecordingDeviceInfo> devices = RecordingDeviceCatalog.Build(
            DeploymentPresets.RecordingHost,
            hostNodeId,
            "一号电脑录像",
            5280,
            "http://192.168.1.20:5280",
            [new MobileOrderReceiverInfo(
                mobileNodeId,
                "打包手机一",
                "192.168.1.31",
                5281,
                RecorderCapabilities,
                Online: true)],
            connectedClients: null);

        Assert.Equal(2, devices.Count);
        RecordingDeviceInfo host = Assert.Single(devices, device => device.NodeId == hostNodeId);
        Assert.Equal("pc", host.DeviceType);
        Assert.Equal("http://192.168.1.20:5280", host.Address);
        Assert.Contains(PackingProofCapabilities.Recording, host.Capabilities);
        Assert.Contains(PackingProofCapabilities.OrderReceiver, host.Capabilities);

        RecordingDeviceInfo mobile = Assert.Single(devices, device => device.NodeId == mobileNodeId);
        Assert.Equal("mobile", mobile.DeviceType);
        Assert.Equal("http://192.168.1.31:5281", mobile.Address);
    }

    [Fact]
    public void RecordingWorkstationIncludesItsOwnOrderReceiverAddress()
    {
        string workstationNodeId = Guid.NewGuid().ToString("D");

        IReadOnlyList<RecordingDeviceInfo> devices = RecordingDeviceCatalog.Build(
            DeploymentPresets.RecordingWorkstation,
            workstationNodeId,
            "电脑2",
            5280,
            "http://192.168.1.42:5280",
            mobileOrderReceivers: null,
            connectedClients: null);

        RecordingDeviceInfo workstation = Assert.Single(devices);
        Assert.Equal(workstationNodeId, workstation.NodeId);
        Assert.Equal("电脑2", workstation.NodeName);
        Assert.Equal("pc", workstation.DeviceType);
        Assert.Equal("http://192.168.1.42:5280", workstation.Address);
        Assert.Contains(PackingProofCapabilities.Recording, workstation.Capabilities);
        Assert.Contains(PackingProofCapabilities.OrderReceiver, workstation.Capabilities);
    }

    [Fact]
    public void ViewerAndOrdinaryMobileBackupHostAreNotRecordingDevices()
    {
        foreach (string preset in new[]
                 {
                     DeploymentPresets.ViewerClient,
                     DeploymentPresets.MobileBackupHost
                 })
        {
            IReadOnlyList<RecordingDeviceInfo> devices = RecordingDeviceCatalog.Build(
                preset,
                Guid.NewGuid().ToString("D"),
                "非录像主机",
                5280,
                "http://192.168.1.20:5280",
                mobileOrderReceivers: null,
                connectedClients: null);

            Assert.Empty(devices);
        }
    }

    [Fact]
    public void CatalogRequiresBothCapabilitiesAndDeduplicatesNodeAndEndpoint()
    {
        string sharedNodeId = Guid.NewGuid().ToString("D");
        var candidates = new[]
        {
            new MobileOrderReceiverInfo(
                sharedNodeId,
                "打包手机一",
                "192.168.1.31",
                5281,
                RecorderCapabilities,
                Online: true),
            new MobileOrderReceiverInfo(
                sharedNodeId,
                "重复节点",
                "192.168.1.32",
                5281,
                RecorderCapabilities,
                Online: true),
            new MobileOrderReceiverInfo(
                Guid.NewGuid().ToString("D"),
                "重复地址",
                "192.168.1.31",
                5281,
                RecorderCapabilities,
                Online: true),
            new MobileOrderReceiverInfo(
                Guid.NewGuid().ToString("D"),
                "缺少订单能力",
                "192.168.1.33",
                5281,
                [PackingProofCapabilities.Recording],
                Online: true),
            new MobileOrderReceiverInfo(
                Guid.NewGuid().ToString("D"),
                "离线设备",
                "192.168.1.34",
                5281,
                RecorderCapabilities,
                Online: false)
        };

        IReadOnlyList<RecordingDeviceInfo> devices = RecordingDeviceCatalog.Build(
            DeploymentPresets.MobileBackupHost,
            Guid.NewGuid().ToString("D"),
            "手机备份主机",
            5280,
            "http://192.168.1.20:5280",
            candidates,
            connectedClients: null);

        RecordingDeviceInfo device = Assert.Single(devices);
        Assert.Equal(sharedNodeId, device.NodeId);
    }

    [Fact]
    public void CapableMobileHeartbeatIsIncludedButViewerHeartbeatIsExcluded()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string mobileNodeId = Guid.NewGuid().ToString("D");
        var clients = new[]
        {
            new ConnectedClientInfo(
                "mobile-client-001",
                "mobile-app",
                "打包手机一",
                "192.168.1.31",
                now,
                mobileNodeId,
                "mobile",
                5281,
                RecorderCapabilities),
            new ConnectedClientInfo(
                "viewer-client-001",
                "web-desktop",
                "查看客户端",
                "192.168.1.32",
                now,
                Guid.NewGuid().ToString("D"),
                "pc",
                5280,
                RecorderCapabilities)
        };

        IReadOnlyList<RecordingDeviceInfo> devices = RecordingDeviceCatalog.Build(
            DeploymentPresets.MobileBackupHost,
            Guid.NewGuid().ToString("D"),
            "手机备份主机",
            5280,
            "http://192.168.1.20:5280",
            mobileOrderReceivers: null,
            clients);

        RecordingDeviceInfo mobile = Assert.Single(devices);
        Assert.Equal(mobileNodeId, mobile.NodeId);
        Assert.Equal("http://192.168.1.31:5281", mobile.Address);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5280")]
    [InlineData("http://169.254.1.20:5280")]
    [InlineData("http://8.8.8.8:5280")]
    [InlineData("not-an-address")]
    public void CatalogRejectsAddressesThatAreNotUsableOnTheLan(string address)
    {
        IReadOnlyList<RecordingDeviceInfo> devices = RecordingDeviceCatalog.Build(
            DeploymentPresets.MobileBackupHost,
            Guid.NewGuid().ToString("D"),
            "手机备份主机",
            5280,
            "http://192.168.1.20:5280",
            [new MobileOrderReceiverInfo(
                Guid.NewGuid().ToString("D"),
                "不可访问设备",
                address,
                5280,
                RecorderCapabilities,
                Online: true)],
            connectedClients: null);

        Assert.Empty(devices);
    }

    [Fact]
    public void MobileRegistryKeepsStableIdentityAndOnlyReturnsRecentlySeenDevices()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"epm-recorder-registry-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "receivers.json");
        DateTime now = new(2026, 7, 25, 0, 0, 0, DateTimeKind.Utc);
        string nodeId = Guid.NewGuid().ToString("D");
        try
        {
            var registry = new MobileOrderReceiverRegistry(path, () => now);
            registry.Register(
                IPAddress.Parse("192.168.1.31"),
                nodeId,
                "打包手机一",
                5281,
                RecorderCapabilities);

            MobileOrderReceiverInfo device = Assert.Single(registry.GetRecordingDevices());
            Assert.Equal(nodeId, device.NodeId);
            Assert.Equal("打包手机一", device.NodeName);
            Assert.Equal(5281, device.Port);

            now = now.AddMinutes(6);
            Assert.Empty(registry.GetRecordingDevices());
            Assert.Single(registry.GetAuthorities());
            MobileOrderReceiverInfo known = Assert.Single(registry.GetKnownRecordingDevices());
            Assert.False(known.Online);
            Assert.Equal(5281, known.Port);

            now = now.AddDays(91);
            Assert.Empty(registry.GetKnownRecordingDevices());
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RecordingDevicesApiExposesRecordingHostWithoutAccessKey()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"epm-recorders-api-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        int port = GetFreeTcpPort();
        string nodeId = Guid.NewGuid().ToString("D");
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                requireAccessKey: true,
                accessKey: "secret-web-access-key",
                listenerHost: "127.0.0.1",
                mobileConnectionUrlProvider: () => $"http://192.168.1.20:{port}/?key=secret-web-access-key",
                mobileBackupStateDirectory: Path.Combine(directory, "uploads"),
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"),
                nodeId: nodeId,
                nodeName: "一号电脑录像",
                deploymentPreset: DeploymentPresets.RecordingHost);
            server.Start();

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using JsonDocument payload = await client.GetFromJsonAsync<JsonDocument>(
                "/api/recording-devices",
                TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("接口未返回 JSON");
            JsonElement device = Assert.Single(payload.RootElement.GetProperty("devices").EnumerateArray());

            Assert.Equal(nodeId, device.GetProperty("nodeId").GetString());
            Assert.Equal("一号电脑录像", device.GetProperty("nodeName").GetString());
            Assert.Equal($"http://192.168.1.20:{port}", device.GetProperty("address").GetString());
            Assert.DoesNotContain(
                "secret-web-access-key",
                payload.RootElement.GetRawText(),
                StringComparison.Ordinal);
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task UserscriptEndpointRejectsEmptyRecorderList()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"epm-empty-recorders-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        int port = GetFreeTcpPort();
        try
        {
            string sourcePath = Path.Combine(directory, "order-integration.user.js");
            File.WriteAllText(sourcePath, """
                // ==UserScript==
                // @name PackingProof Test Integration
                // @namespace packingproof.test
                // @version 1.0
                // PACKING_PROOF_CONNECT_TARGETS
                // PACKING_PROOF_UPDATE_URLS
                // ==/UserScript==
                const PACKING_PROOF_RECORDERS = [];
                const PACKING_PROOF_HOST = null;
                """, Encoding.UTF8);
            string userscriptDirectory = Path.Combine(directory, "userscripts");
            UserscriptDescriptor userscript = new UserscriptCatalog(userscriptDirectory).Import(sourcePath);
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileConnectionUrlProvider: () => $"http://192.168.1.20:{port}",
                mobileBackupStateDirectory: Path.Combine(directory, "uploads"),
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"),
                nodeId: Guid.NewGuid().ToString("D"),
                nodeName: "手机备份主机",
                deploymentPreset: DeploymentPresets.MobileBackupHost,
                userscriptDirectory: userscriptDirectory);
            server.Start();

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using HttpResponseMessage response = await client.GetAsync(
                $"/api/userscripts/{userscript.Id}/download",
                TestContext.Current.CancellationToken);
            string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using JsonDocument payload = JsonDocument.Parse(body);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(
                "当前没有发现可接收订单的录像设备",
                payload.RootElement.GetProperty("error").GetString());
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task TestOrderBroadcastSendsToEveryUniqueOnlineDevice()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"epm-test-order-broadcast-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        int firstPort = GetFreeTcpPort();
        int secondPort = GetFreeTcpPort();
        try
        {
            using var firstDatabase = new VideoDatabase(Path.Combine(directory, "first.db"));
            using var secondDatabase = new VideoDatabase(Path.Combine(directory, "second.db"));
            using var firstServer = new WebServer(
                firstDatabase,
                firstPort,
                listenerHost: "127.0.0.1",
                mobileBackupStateDirectory: Path.Combine(directory, "first-state"),
                nodeId: "first",
                nodeName: "电脑",
                deploymentPreset: DeploymentPresets.RecordingHost);
            using var secondServer = new WebServer(
                secondDatabase,
                secondPort,
                listenerHost: "127.0.0.1",
                mobileBackupStateDirectory: Path.Combine(directory, "second-state"),
                nodeId: "second",
                nodeName: "手机1",
                deploymentPreset: DeploymentPresets.RecordingHost);
            StartWebServerWithRetry(firstServer);
            StartWebServerWithRetry(secondServer);
            var devices = new[]
            {
                new RecordingDeviceInfo
                {
                    NodeId = "first",
                    NodeName = "电脑",
                    Address = $"http://127.0.0.1:{firstPort}",
                    Online = true
                },
                new RecordingDeviceInfo
                {
                    NodeId = "first-copy",
                    NodeName = "重复电脑",
                    Address = $"http://127.0.0.1:{firstPort}",
                    Online = true
                },
                new RecordingDeviceInfo
                {
                    NodeId = "second",
                    NodeName = "手机1",
                    Address = $"http://127.0.0.1:{secondPort}",
                    Online = true
                },
                new RecordingDeviceInfo
                {
                    NodeId = "offline",
                    NodeName = "手机2",
                    Address = $"http://127.0.0.1:{GetFreeTcpPort()}",
                    Online = false
                }
            };

            WorkstationNetwork.TestOrderBroadcastResult result =
                await WorkstationNetwork.SendTestOrderToRecordingDevicesAsync(
                    devices,
                    TestContext.Current.CancellationToken);

            Assert.Equal(2, result.Devices.Count);
            Assert.Equal(2, result.SuccessCount);
            Assert.Equal(0, result.FailureCount);
            Assert.All(result.Devices, device =>
            {
                Assert.True(device.Sent, device.ErrorMessage);
                Assert.True(device.MonitorConfirmed, device.ErrorMessage);
                Assert.Equal(1, device.TestCount);
            });
            string summary = WorkstationNetwork.FormatTestOrderBroadcastResult(result);
            Assert.Contains("成功 2 台，失败 0 台", summary, StringComparison.Ordinal);
            Assert.Contains("电脑", summary, StringComparison.Ordinal);
            Assert.Contains("手机1", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("手机2", summary, StringComparison.Ordinal);
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task TestOrderBroadcastReportsNoOnlineDevices()
    {
        WorkstationNetwork.TestOrderBroadcastResult result =
            await WorkstationNetwork.SendTestOrderToRecordingDevicesAsync(
                [
                    new RecordingDeviceInfo
                    {
                        NodeId = "offline",
                        NodeName = "手机1",
                        Address = "http://192.168.1.31:5280",
                        Online = false
                    }
                ],
                TestContext.Current.CancellationToken);

        Assert.False(result.HasTargets);
        Assert.Equal("当前没有在线的录像设备", result.ErrorMessage);
        Assert.Equal("当前没有在线的录像设备", WorkstationNetwork.FormatTestOrderBroadcastResult(result));
    }

    [Fact]
    public async Task TestOrderBroadcastKeepsSuccessfulDeviceWhenAnotherDeviceFails()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"epm-test-order-partial-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        int successPort = GetFreeTcpPort();
        int failurePort = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                successPort,
                listenerHost: "127.0.0.1",
                mobileBackupStateDirectory: Path.Combine(directory, "state"),
                nodeId: "success",
                nodeName: "电脑",
                deploymentPreset: DeploymentPresets.RecordingHost);
            server.Start();
            using var failureServer = new HttpListener();
            failureServer.Prefixes.Add($"http://127.0.0.1:{failurePort}/");
            failureServer.Start();
            Task failureResponse = Task.Run(async () =>
            {
                HttpListenerContext context = await failureServer.GetContextAsync()
                    .WaitAsync(TestContext.Current.CancellationToken);
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                context.Response.Close();
            }, TestContext.Current.CancellationToken);
            var devices = new[]
            {
                new RecordingDeviceInfo
                {
                    NodeId = "success",
                    NodeName = "电脑",
                    Address = $"http://127.0.0.1:{successPort}",
                    Online = true
                },
                new RecordingDeviceInfo
                {
                    NodeId = "failure",
                    NodeName = "手机1",
                    Address = $"http://127.0.0.1:{failurePort}",
                    Online = true
                }
            };

            WorkstationNetwork.TestOrderBroadcastResult result =
                await WorkstationNetwork.SendTestOrderToRecordingDevicesAsync(
                    devices,
                    TestContext.Current.CancellationToken);
            await failureResponse;

            Assert.Equal(1, result.SuccessCount);
            Assert.Equal(1, result.FailureCount);
            Assert.True(result.Devices.Single(device => device.NodeId == "success").Succeeded);
            WorkstationNetwork.TestOrderDeviceResult failed =
                result.Devices.Single(device => device.NodeId == "failure");
            Assert.False(failed.Succeeded);
            Assert.Equal("HTTP 500", failed.ErrorMessage);
            string summary = WorkstationNetwork.FormatTestOrderBroadcastResult(result);
            Assert.Contains("成功 1 台，失败 1 台", summary, StringComparison.Ordinal);
            Assert.Contains("手机1", summary, StringComparison.Ordinal);
            Assert.Contains("HTTP 500", summary, StringComparison.Ordinal);
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static int GetFreeTcpPort() =>
        TestPortAllocator.GetFreeTcpPort();

    private static void StartWebServerWithRetry(WebServer server)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                server.Start();
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Thread.Sleep(200);
            }
        }

        throw lastError ?? new InvalidOperationException("WebServer 启动失败");
    }
}
