using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UpdateCore;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class WebFeatureGateTests
{
    [Fact]
    public void FullHostAllowsMediaIntegrationAndMobileAppRoutes()
    {
        var gate = new WebFeatureGate(Compose(FullFeatures(), ProductRoleCapabilityMapper.RecordingHost));

        Assert.True(gate.Evaluate("/", "GET").Allowed);
        Assert.True(gate.Evaluate("/api/videos", "GET").Allowed);
        Assert.True(gate.Evaluate("/api/photos", "GET").Allowed);
        Assert.True(gate.Evaluate("/api/photos/1/download", "GET").Allowed);
        Assert.True(gate.Evaluate("/api/orderinfo", "POST").Allowed);
        Assert.True(gate.Evaluate("/api/extensions/v1/enroll", "POST").Allowed);
        Assert.True(gate.Evaluate("/api/mobile-app-download", "GET").Allowed);
        Assert.True(gate.Evaluate("/api/local-settings", "GET").Allowed);
    }

    [Fact]
    public void PhotoRoutesRequirePhotoCaptureEvenWhenMediaPlaybackIsEnabled()
    {
        ProductFeatures features = FullFeatures() with { PhotoCapture = false };
        var gate = new WebFeatureGate(Compose(
            features,
            ProductRoleCapabilityMapper.RecordingHost));

        Assert.True(gate.Evaluate("/api/videos", "GET").Allowed);
        WebFeatureGateDecision decision =
            gate.Evaluate("/api/photos/1/download", "GET");
        Assert.False(decision.Allowed);
        Assert.Equal(404, decision.StatusCode);
        Assert.Equal("photoCapture", decision.Capability);
        Assert.Equal("photos", decision.RouteGroup);
    }

    [Fact]
    public void LocalOnlyBrandDoesNotExposeAnyWebRoute()
    {
        ProductFeatures local = LocalCaptureFeatures();
        EffectiveProductCapabilities effective = Compose(
            local,
            ProductRoleCapabilityMapper.RecordingHost);
        var gate = new WebFeatureGate(effective);

        Assert.False(effective.WebHostEnabled);
        Assert.False(gate.Evaluate("/", "GET").Allowed);
        Assert.False(gate.Evaluate("/api/videos", "GET").Allowed);
        Assert.False(gate.Evaluate("/api/mobile-app-download", "GET").Allowed);
        Assert.False(gate.Evaluate("/api/clips/example", "GET").Allowed);
        Assert.False(gate.Evaluate("/api/clip-previews/example", "GET").Allowed);
    }

    [Fact]
    public void RecordingWorkstationKeepsOrderOnlyAndRejectsMediaAndMobileApp()
    {
        EffectiveProductCapabilities effective = Compose(
            FullFeatures(),
            ProductRoleCapabilityMapper.RecordingWorkstation);
        var gate = new WebFeatureGate(effective);

        Assert.True(gate.Evaluate("/api/orderinfo", "POST").Allowed);
        Assert.True(gate.Evaluate("/api/extensions/v1/capabilities", "GET").Allowed);
        Assert.True(gate.Evaluate("/api/extensions/v1/heartbeat", "POST").Allowed);
        Assert.True(gate.Evaluate("/api/extensions/v1/scan-tasks/next", "GET").Allowed);
        Assert.True(gate.Evaluate("/api/extensions/v1/scan-results", "POST").Allowed);
        Assert.True(gate.Evaluate("/api/extensions/v1/recording-queries", "POST").Allowed);
        Assert.Equal(
            200,
            gate.Evaluate("/", "GET").StatusCode);
        Assert.Equal(
            "integration-landing",
            gate.Evaluate("/", "GET").RouteGroup);
        Assert.Equal(
            404,
            gate.Evaluate("/api/mobile-app-download", "GET").StatusCode);
        Assert.Equal(
            404,
            gate.Evaluate("/api/local-settings", "GET").StatusCode);
    }

    [Fact]
    public void DisabledExtensionApiReturnsStableCapabilityError()
    {
        ProductFeatures features = FullFeatures() with
        {
            ExtensionApi = false,
            ExtensionMarket = false
        };
        EffectiveProductCapabilities effective = Compose(
            features,
            ProductRoleCapabilityMapper.RecordingHost);
        var gate = new WebFeatureGate(effective);

        WebFeatureGateDecision decision =
            gate.Evaluate("/api/extensions/v1/enroll", "POST");
        Assert.False(decision.Allowed);
        Assert.Equal(403, decision.StatusCode);
        Assert.Equal("capability_disabled", decision.ErrorCode);
        Assert.Equal("extensionApi", decision.Capability);
        Assert.True(gate.Evaluate("/api/extensions/v1/capabilities", "GET").Allowed);
    }

    [Fact]
    public void MobileBackupHostWithoutClipCapabilityCannotReachClipRoutes()
    {
        ProductFeatures features = FullFeatures() with
        {
            Clip = false,
            Compress = false
        };
        EffectiveProductCapabilities effective = Compose(
            features,
            ProductRoleCapabilityMapper.MobileBackupHost);
        var gate = new WebFeatureGate(effective);

        Assert.True(gate.Evaluate("/api/mobile-backup/videos", "GET").Allowed);
        Assert.Equal(
            403,
            gate.Evaluate("/api/mobile-backup/clip-tasks/example", "GET").StatusCode);
        Assert.Equal(
            403,
            gate.Evaluate("/api/mobile-backup/videos/1/clip", "POST").StatusCode);
        Assert.Equal(
            404,
            gate.Evaluate("/api/clips/example", "GET").StatusCode);
    }

    [Fact]
    public void MobileBackupClipRequiresClipEvenWhenCompressionIsEnabled()
    {
        ProductFeatures features = FullFeatures() with
        {
            Clip = false,
            Compress = true
        };
        var gate = new WebFeatureGate(Compose(
            features,
            ProductRoleCapabilityMapper.MobileBackupHost));

        Assert.Equal(
            403,
            gate.Evaluate("/api/mobile-backup/videos/1/clip", "POST").StatusCode);
        Assert.Equal(
            403,
            gate.Evaluate("/api/mobile-backup/clip-tasks/example", "GET").StatusCode);
        Assert.Equal(
            403,
            gate.Evaluate("/api/mobile-backup/clips/example", "GET").StatusCode);
        Assert.Equal(
            403,
            gate.Evaluate("/api/mobile-backup/clip-previews/example", "GET").StatusCode);
        Assert.True(gate.Evaluate("/api/mobile-backup/videos", "GET").Allowed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MobileBackupClipRequiresBothBackupAndClipRouteGroups(bool mobileBackupReceive)
    {
        ProductFeatures features = FullFeatures() with
        {
            RemoteMediaAccess = false,
            Clip = true,
            Compress = false,
            MobileBackupReceive = mobileBackupReceive
        };
        var gate = new WebFeatureGate(Compose(
            features,
            mobileBackupReceive
                ? ProductRoleCapabilityMapper.MobileBackupHost
                : ProductRoleCapabilityMapper.RecordingHost));

        Assert.Equal(
            403,
            gate.Evaluate("/api/mobile-backup/videos/1/clip", "POST").StatusCode);
        Assert.Equal(
            403,
            gate.Evaluate("/api/mobile-backup/clip-tasks/example", "GET").StatusCode);
        if (mobileBackupReceive)
        {
            Assert.True(gate.Evaluate("/api/mobile-backup/videos", "GET").Allowed);
        }
    }

    [Fact]
    public void BrowserSnapshotContainsAllCapabilitiesInputsAndRouteGroups()
    {
        EffectiveProductCapabilities effective = Compose(
            FullFeatures(),
            ProductRoleCapabilityMapper.RecordingHost);
        object snapshotObject = WebFeatureGate.CreateBrowserSnapshot(effective);
        using JsonDocument document = JsonDocument.Parse(
            JsonSerializer.Serialize(snapshotObject));
        JsonElement root = document.RootElement;

        Assert.Equal(ProductCapabilitySchemaVersion.Current,
            root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.GetProperty("webHostEnabled").GetBoolean());
        Assert.Equal(16, root.GetProperty("capabilities").EnumerateObject().Count());
        Assert.Equal(5, root.GetProperty("inputs").EnumerateObject().Count());
        Assert.Contains(
            root.GetProperty("routeGroups").EnumerateArray()
                .Select(item => item.GetString()),
            item => item == "media-library");
    }

    [Fact]
    public async Task IndexPageReceivesProductFeatureSnapshot()
    {
        int port = GetFreeTcpPort();
        string directory = Path.Combine(
            Path.GetTempPath(),
            "packingproof-web-feature-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            EffectiveProductCapabilities effective = Compose(
                FullFeatures(),
                ProductRoleCapabilityMapper.RecordingHost);
            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupStateDirectory: Path.Combine(directory, "state"),
                deploymentPreset: DeploymentPresets.RecordingHost,
                effectiveCapabilities: effective);
            server.Start();
            using var client = new HttpClient();
            string html = await client.GetStringAsync(
                $"http://127.0.0.1:{port}/",
                TestContext.Current.CancellationToken);
            using JsonDocument nodeInfo = JsonDocument.Parse(
                await client.GetStringAsync(
                    $"http://127.0.0.1:{port}/api/node-info",
                    TestContext.Current.CancellationToken));

            Assert.Contains("window.__productFeatures=", html, StringComparison.Ordinal);
            Assert.Contains("\"schemaVersion\":1", html, StringComparison.Ordinal);
            Assert.Contains("\"mediaLibrary\":true", html, StringComparison.Ordinal);
            Assert.Equal(
                ProductCapabilitySchemaVersion.Current,
                nodeInfo.RootElement
                    .GetProperty("capabilitySchemaVersion")
                    .GetInt32());
            Assert.True(nodeInfo.RootElement.GetProperty("webHostEnabled").GetBoolean());
            Assert.Contains(
                nodeInfo.RootElement
                    .GetProperty("webRouteGroups")
                    .EnumerateArray()
                    .Select(item => item.GetString()),
                group => group == "media-library");
            Assert.DoesNotContain(
                "__PACKINGPROOF_PRODUCT_FEATURES_JSON__",
                html,
                StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DisabledMobileAndExtensionCapabilitiesAreAbsentFromWebOutputs()
    {
        int port = GetFreeTcpPort();
        string directory = Path.Combine(
            Path.GetTempPath(),
            "packingproof-web-feature-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            ProductFeatures features = FullFeatures() with
            {
                MobileApp = false,
                ExtensionApi = false,
                ExtensionMarket = false
            };
            EffectiveProductCapabilities effective = Compose(
                features,
                ProductRoleCapabilityMapper.RecordingHost);
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupStateDirectory: Path.Combine(directory, "state"),
                deploymentPreset: DeploymentPresets.RecordingHost,
                effectiveCapabilities: effective);
            server.Start();
            using var client = new HttpClient();
            string html = await client.GetStringAsync(
                $"http://127.0.0.1:{port}/",
                TestContext.Current.CancellationToken);
            using JsonDocument capabilities = JsonDocument.Parse(
                await client.GetStringAsync(
                    $"http://127.0.0.1:{port}/api/extensions/v1/capabilities",
                    TestContext.Current.CancellationToken));

            Assert.Contains("\"mobileApp\":false", html, StringComparison.Ordinal);
            Assert.Contains("\"mobileReleasesUrl\":\"\"", html, StringComparison.Ordinal);
            Assert.False(
                capabilities.RootElement
                    .GetProperty("extensionApiEnabled")
                    .GetBoolean());
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RecordingWorkstationCanReachEnabledExtensionLifecycleRoutes()
    {
        int port = GetFreeTcpPort();
        string directory = Path.Combine(
            Path.GetTempPath(),
            "packingproof-web-feature-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            EffectiveProductCapabilities effective = Compose(
                FullFeatures(),
                ProductRoleCapabilityMapper.RecordingWorkstation);
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupStateDirectory: Path.Combine(directory, "state"),
                deploymentPreset: DeploymentPresets.RecordingWorkstation,
                orderReceiverOnly: true,
                effectiveCapabilities: effective);
            server.Start();
            using var client = new HttpClient();
            using HttpResponseMessage response = await client.GetAsync(
                $"http://127.0.0.1:{port}/api/extensions/v1/scan-tasks/next",
                TestContext.Current.CancellationToken);

            Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
            Assert.True(
                response.StatusCode is HttpStatusCode.Unauthorized
                    or HttpStatusCode.Forbidden);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task WebServerPausesAdmissionDuringModeTransition()
    {
        int port = GetFreeTcpPort();
        string directory = Path.Combine(
            Path.GetTempPath(),
            "packingproof-web-feature-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupStateDirectory: Path.Combine(directory, "state"),
                deploymentPreset: DeploymentPresets.RecordingHost,
                effectiveCapabilities: Compose(
                    FullFeatures(),
                    ProductRoleCapabilityMapper.RecordingHost));
            server.Start();
            using var client = new HttpClient();
            string url = $"http://127.0.0.1:{port}/api/node-info";

            Assert.Equal(
                HttpStatusCode.OK,
                (await client.GetAsync(
                    url,
                    TestContext.Current.CancellationToken)).StatusCode);
            server.PauseRequestAdmission();
            using HttpResponseMessage paused = await client.GetAsync(
                url,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, paused.StatusCode);
            Assert.Contains(
                "mode_transition",
                await paused.Content.ReadAsStringAsync(
                    TestContext.Current.CancellationToken),
                StringComparison.Ordinal);
            server.ResumeRequestAdmission();
            Assert.Equal(
                HttpStatusCode.OK,
                (await client.GetAsync(
                    url,
                    TestContext.Current.CancellationToken)).StatusCode);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WebServerRefusesToStartWhenCapabilitiesDisableHost()
    {
        using var database = new VideoDatabase(":memory:");
        EffectiveProductCapabilities effective = Compose(
            LocalCaptureFeatures(),
            ProductRoleCapabilityMapper.RecordingHost);
        using var server = new WebServer(
            database,
            GetFreeTcpPort(),
            listenerHost: "127.0.0.1",
            deploymentPreset: DeploymentPresets.RecordingHost,
            effectiveCapabilities: effective);

        Assert.Throws<InvalidOperationException>(() => server.Start());
    }

    [Fact]
    public void UdpAnnounceCanRepresentLanOnlyHostWithoutWebServer()
    {
        string nodeId = Guid.NewGuid().ToString("D");
        byte[] packet = UdpDiscoveryProtocol.EncodeAnnounce(
            nodeId,
            httpPort: 0,
            ProductCapabilitySchemaVersion.Current,
            webHostEnabled: false,
            routeGroups: []);

        Assert.True(UdpDiscoveryProtocol.TryParseAnnounce(
            packet,
            out string parsedNodeId,
            out int webPort,
            out int schemaVersion,
            out bool webHostEnabled,
            out IReadOnlyList<string> routeGroups));
        Assert.Equal(nodeId, parsedNodeId);
        Assert.Equal(0, webPort);
        Assert.Equal(ProductCapabilitySchemaVersion.Current, schemaVersion);
        Assert.False(webHostEnabled);
        Assert.Empty(routeGroups);
        var discoveryNode = new PackingProofNodeInfo
        {
            Protocol = PackingProofNodeInfo.ExpectedProtocol,
            NodeId = nodeId,
            WebHostEnabled = false,
            HttpPort = 0,
            Address = "192.168.1.20"
        };
        Assert.True(discoveryNode.IsDiscoveryOnly);
        var reporter = new ExpressPackingMonitoring.WorkstationNetwork.HostReporter(
            progress: null);
        reporter.Report(discoveryNode);
        Assert.Single(reporter.ToList());
    }

    private static ProductFeatures FullFeatures() =>
        new(
            VideoRecording: true,
            PhotoCapture: true,
            MediaLibrary: true,
            RemoteMediaAccess: true,
            LanCollaboration: true,
            ComputerBackupReceive: true,
            MobileBackupReceive: true,
            MobileApp: true,
            NasArchive: true,
            Clip: true,
            Compress: true,
            ExtensionApi: true,
            ExtensionMarket: true,
            Userscript: true,
            OrderReceiver: true,
            Tts: true,
            Inputs: new InputFeatures(true, true, true, true, true));

    private static ProductFeatures LocalCaptureFeatures() =>
        FullFeatures() with
        {
            RemoteMediaAccess = false,
            LanCollaboration = false,
            ComputerBackupReceive = false,
            MobileBackupReceive = false,
            MobileApp = false,
            NasArchive = false,
            Clip = false,
            Compress = false,
            ExtensionApi = false,
            ExtensionMarket = false,
            Userscript = false,
            OrderReceiver = false,
            Tts = false
        };

    private static EffectiveProductCapabilities Compose(
        ProductFeatures features,
        string preset) =>
        EffectiveProductCapabilities.Compose(
            features,
            ProductRoleCapabilityMapper.Get(preset),
            RuntimeEnvironment.FullyAvailable,
            new ProductCapabilityIdentity("web-test", 1, new string('0', 64)));

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
