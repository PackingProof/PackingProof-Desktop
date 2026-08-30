using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.ViewModels;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

[Collection("Web server tests")]
public sealed class ConnectedClientTests
{
    [Theory]
    [InlineData("mobile-app", "phone-1", "host-1", true)]
    [InlineData("recording-workstation", "pc-1", "host-1", true)]
    [InlineData("recording-workstation", "host-1", "host-1", false)]
    [InlineData("web-desktop", "browser-1", "host-1", false)]
    public void HostBackupCardsIncludePhoneAndRecordingComputerButExcludeSelf(
        string clientType,
        string nodeId,
        string localNodeId,
        bool expected)
    {
        var client = new ConnectedClientInfo(
            nodeId,
            clientType,
            "设备",
            "192.168.1.20",
            DateTimeOffset.UtcNow,
            nodeId,
            clientType == "recording-workstation" ? "pc" : "mobile",
            5280,
            []);

        Assert.Equal(
            expected,
            MainViewModel.ShouldIncludeBackupDeviceClient(client, localNodeId));
    }

    [Theory]
    [InlineData("host-1", "host-1", false)]
    [InlineData("HOST-1", "host-1", false)]
    [InlineData("", "host-1", false)]
    [InlineData("phone-1", "host-1", true)]
    public void BackupDeviceIdentityExcludesLocalNodeId(
        string deviceId,
        string localNodeId,
        bool expected)
    {
        Assert.Equal(expected, BackupDeviceIdentity.IsRemote(deviceId, localNodeId));
    }

    [Fact]
    public void RegistryDeduplicatesSameClientButCountsDifferentClientTypes()
    {
        var clock = new MutableTimeProvider();
        using var registry = new ConnectedClientRegistry(clock, startCleanupTimer: false);
        var changedCounts = new List<int>();
        registry.Changed += clients => changedCounts.Add(clients.Count);

        registry.Heartbeat(Heartbeat("shared-client", "web-desktop", "电脑网页"), "192.168.1.20");
        registry.Heartbeat(Heartbeat("shared-client", "web-desktop", "电脑网页"), "192.168.1.20");
        registry.Heartbeat(Heartbeat("shared-client", "userscript", "快递端油猴脚本"), "192.168.1.20");

        Assert.Equal(2, registry.GetSnapshot().Count);
        Assert.Equal(new[] { 1, 1, 2 }, changedCounts);
    }

    [Fact]
    public void ConnectedDeviceCountDeduplicatesClientsByRemoteAddress()
    {
        using var registry = new ConnectedClientRegistry(startCleanupTimer: false);
        registry.Heartbeat(Heartbeat("desktop-001", "web-desktop", "电脑网页"), "::ffff:192.168.1.20");
        registry.Heartbeat(Heartbeat("script-001", "userscript", "快递端油猴脚本"), "192.168.1.20");
        registry.Heartbeat(Heartbeat("mobile-001", "web-mobile", "手机网页"), "192.168.1.21");

        Assert.Equal(3, registry.GetSnapshot().Count);
        Assert.Equal(2, ConnectedClientRegistry.CountDistinctAddresses(registry.GetSnapshot()));
    }

    [Fact]
    public void RegistryExpiresAndActivelyDisconnectsClients()
    {
        var clock = new MutableTimeProvider();
        using var registry = new ConnectedClientRegistry(clock, startCleanupTimer: false);
        registry.Heartbeat(Heartbeat("desktop-001", "web-desktop", "电脑网页"), "192.168.1.20");
        registry.Heartbeat(Heartbeat("station-001", "print-station", "打印工位程序"), "192.168.1.21");

        registry.Heartbeat(Heartbeat("station-001", "print-station", "打印工位程序", connected: false), "192.168.1.21");
        Assert.Single(registry.GetSnapshot());

        clock.Advance(TimeSpan.FromSeconds(ConnectedClientRegistry.ExpirationSeconds + 1));
        Assert.Empty(registry.GetSnapshot());
        using var restarted = new ConnectedClientRegistry(clock, startCleanupTimer: false);
        Assert.Empty(restarted.GetSnapshot());
    }

    [Fact]
    public void RegistryEnforcesPerAddressAndGlobalCapacity()
    {
        using var perAddress = new ConnectedClientRegistry(startCleanupTimer: false);
        for (int index = 0; index < ConnectedClientRegistry.MaxClientsPerAddress; index++)
            perAddress.Heartbeat(Heartbeat($"client-{index:000}", "web-desktop", "电脑网页"), "192.168.1.20");
        ConnectedClientValidationException addressError = Assert.Throws<ConnectedClientValidationException>(() =>
            perAddress.Heartbeat(Heartbeat("client-overflow", "web-desktop", "电脑网页"), "192.168.1.20"));
        Assert.Equal("too_many_clients", addressError.ErrorCode);

        using var global = new ConnectedClientRegistry(startCleanupTimer: false);
        for (int index = 0; index < ConnectedClientRegistry.MaxClients; index++)
        {
            global.Heartbeat(
                Heartbeat($"global-{index:000}", "web-mobile", "手机网页"),
                $"192.168.{index / 16}.{index % 16 + 1}");
        }
        ConnectedClientValidationException globalError = Assert.Throws<ConnectedClientValidationException>(() =>
            global.Heartbeat(Heartbeat("global-overflow", "web-mobile", "手机网页"), "10.0.0.1"));
        Assert.Equal("connection_registry_full", globalError.ErrorCode);
    }

    [Theory]
    [InlineData("short", "web-desktop", "电脑网页", "invalid_client_id")]
    [InlineData("valid-client", "unknown", "未知", "invalid_client_type")]
    [InlineData("valid-client", "web-desktop", "", "invalid_display_name")]
    public void RegistryRejectsInvalidHeartbeat(string clientId, string type, string name, string errorCode)
    {
        using var registry = new ConnectedClientRegistry(startCleanupTimer: false);
        ConnectedClientValidationException error = Assert.Throws<ConnectedClientValidationException>(() =>
            registry.Heartbeat(Heartbeat(clientId, type, name), "192.168.1.20"));
        Assert.Equal(errorCode, error.ErrorCode);
    }

    [Fact]
    public async Task HeartbeatApiRegistersWithoutExposingConnectedClientDetails()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"epm-connected-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        int port = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupStateDirectory: Path.Combine(directory, "uploads"),
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"));
            server.Start();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            CancellationToken token = TestContext.Current.CancellationToken;

            var heartbeat = Heartbeat("browser-client-001", "web-desktop", "电脑网页");
            using HttpResponseMessage first = await client.PostAsJsonAsync("/api/connections/heartbeat", heartbeat, token);
            using HttpResponseMessage repeated = await client.PostAsJsonAsync("/api/connections/heartbeat", heartbeat, token);
            string body = await first.Content.ReadAsStringAsync(token);
            using JsonDocument payload = JsonDocument.Parse(body);

            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
            Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(ConnectedClientRegistry.ExpirationSeconds, payload.RootElement.GetProperty("expiresInSeconds").GetInt32());
            JsonElement mobileUpdate = payload.RootElement.GetProperty("mobileAppUpdate");
            Assert.Equal(11036, mobileUpdate.GetProperty("minimumBuildNumber").GetInt32());
            Assert.Equal(
                "当前 APP 版本过低，需要更新",
                mobileUpdate.GetProperty("message").GetString());
            Assert.False(payload.RootElement.TryGetProperty("clients", out _));
            Assert.False(payload.RootElement.TryGetProperty("count", out _));
            ConnectedClientInfo registered = Assert.Single(server.GetConnectedClients());
            Assert.Equal("browser-client-001", registered.ClientId);

            var mobileHeartbeat = Heartbeat("mobile-client-001", "mobile-app", "设备 A1B2C3");
            using HttpResponseMessage mobileResponse = await client.PostAsJsonAsync(
                "/api/connections/heartbeat",
                mobileHeartbeat,
                token);
            using JsonDocument mobilePayload = JsonDocument.Parse(
                await mobileResponse.Content.ReadAsStringAsync(token));
            Assert.Equal(
                "设备 A1B2C3",
                mobilePayload.RootElement.GetProperty("assignedDisplayName").GetString());

            using HttpResponseMessage invalid = await client.PostAsJsonAsync(
                "/api/connections/heartbeat",
                Heartbeat("browser-client-002", "invalid", "非法设备"),
                token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task OrderReceiverOnlyServerRejectsVideoAndBackupHostRoutes()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"epm-order-receiver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        int port = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupStateDirectory: Path.Combine(directory, "uploads"),
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"),
                deploymentPreset: DeploymentPresets.RecordingWorkstation,
                orderReceiverOnly: true);
            server.Start();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            CancellationToken token = TestContext.Current.CancellationToken;

            using HttpResponseMessage nodeInfo = await client.GetAsync("/api/node-info", token);
            using HttpResponseMessage videos = await client.GetAsync("/api/videos", token);
            using HttpResponseMessage backup = await client.GetAsync(
                "/api/mobile-backup/capabilities",
                token);

            Assert.Equal(HttpStatusCode.OK, nodeInfo.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, videos.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, backup.StatusCode);
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RegistryStoresMobileAppVersionFromHeartbeat()
    {
        using var registry = new ConnectedClientRegistry(startCleanupTimer: false);
        ConnectedClientHeartbeat heartbeat = Heartbeat(
            "mobile-client-001",
            "mobile-app",
            "手机1");
        heartbeat.AppVersion = "0.5.6";
        heartbeat.AppBuildNumber = 11006;

        registry.Heartbeat(heartbeat, "192.168.1.31");

        ConnectedClientInfo client = Assert.Single(registry.GetSnapshot());
        Assert.Equal("0.5.6", client.AppVersion);
        Assert.Equal(11006, client.AppBuildNumber);
    }

    [Fact]
    public void RegistryAcceptsRecordingWorkstationHeartbeat()
    {
        using var registry = new ConnectedClientRegistry(startCleanupTimer: false);
        ConnectedClientHeartbeat heartbeat = Heartbeat(
            "recording-node-001",
            "recording-workstation",
            "录像工位");
        heartbeat.NodeId = "recording-node-001";
        heartbeat.DeviceType = "pc";
        heartbeat.OrderReceiverPort = 5280;
        heartbeat.Capabilities = ["recording", "order-receiver"];

        registry.Heartbeat(heartbeat, "192.168.1.30");

        ConnectedClientInfo client = Assert.Single(registry.GetSnapshot());
        Assert.Equal("recording-workstation", client.ClientType);
        Assert.Equal("pc", client.DeviceType);
        Assert.Contains("recording", client.Capabilities);
        Assert.Contains("order-receiver", client.Capabilities);
    }

    [Theory]
    [InlineData("/api/node-info", "GET", true)]
    [InlineData("/api/orderinfo", "POST", true)]
    [InlineData("/api/order-lookup/pending", "GET", true)]
    [InlineData("/api/connections/heartbeat", "POST", true)]
    [InlineData("/api/userscripts/sample/download.user.js", "GET", true)]
    [InlineData("/api/userscripts/sample/download", "GET", true)]
    [InlineData("/PackingProof-Order-Integration-KDZS.user.js", "GET", true)]
    [InlineData("/kuaidizs-order-push.user.js", "GET", true)]
    [InlineData("/", "GET", false)]
    [InlineData("/api/videos", "GET", false)]
    [InlineData("/api/mobile-backup/capabilities", "GET", false)]
    [InlineData("/api/mobile-backup/uploads", "POST", false)]
    public void OrderReceiverOnlyModeExposesOnlyOrderIntegrationRoutes(
        string path,
        string method,
        bool expected)
    {
        Assert.Equal(expected, WebServer.IsOrderReceiverPathAllowed(path, method));
    }

    private static ConnectedClientHeartbeat Heartbeat(
        string clientId,
        string type,
        string name,
        bool? connected = null) =>
        new() { ClientId = clientId, ClientType = type, DisplayName = name, Connected = connected };

    private static int GetFreeTcpPort() =>
        TestPortAllocator.GetFreeTcpPort();

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
