using System.Net;
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
public sealed class HostDiscoveryTests
{
    [Fact]
    public void LanDiscoveryDoesNotUseTheSystemProxy()
    {
        using SocketsHttpHandler handler = WorkstationNetwork.CreateLanHttpMessageHandler();

        Assert.False(handler.UseProxy);
    }

    [Theory]
    [InlineData("http://[::1]:5280", "[::1]:5280")]
    [InlineData("[fe80::1]", "[fe80::1]:5280")]
    [InlineData("fe80::1", "[fe80::1]:5280")]
    public void NormalizeAddressPreservesIpv6LiteralAndPort(string input, string expected)
    {
        Assert.Equal(expected, WorkstationNetwork.NormalizeAddress(input));
    }

    [Fact]
    public async Task NodeInfoApiReturnsStablePublicHostIdentityWithoutSecrets()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"epm-node-info-{Guid.NewGuid():N}");
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
                mobileBackupComputerId: Guid.NewGuid().ToString("D"),
                mobileBackupStateDirectory: Path.Combine(directory, "uploads"),
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"),
                nodeId: nodeId,
                nodeName: "一号手机备份主机",
                deploymentPreset: DeploymentPresets.MobileBackupHost);
            server.Start();

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            string body = await client.GetStringAsync("/api/node-info", TestContext.Current.CancellationToken);
            PackingProofNodeInfo? node = JsonSerializer.Deserialize<PackingProofNodeInfo>(body);

            Assert.NotNull(node);
            Assert.True(node.IsValidHost);
            Assert.Equal(nodeId, node.NodeId);
            Assert.Equal("一号手机备份主机", node.NodeName);
            Assert.Equal(DeploymentPresets.MobileBackupHost, node.Preset);
            Assert.Contains(PackingProofCapabilities.Host, node.Capabilities);
            Assert.DoesNotContain("secret-web-access-key", body, StringComparison.Ordinal);
            Assert.DoesNotContain("accessKey", body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DiscoveryFindsAnIsolatedLocalPackingProofHost()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"epm-host-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        int port = GetFreeTcpPort();
        string nodeId = Guid.NewGuid().ToString("D");
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupComputerId: Guid.NewGuid().ToString("D"),
                mobileBackupStateDirectory: Path.Combine(directory, "uploads"),
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"),
                nodeId: nodeId,
                nodeName: "本地发现测试主机",
                deploymentPreset: DeploymentPresets.MobileBackupHost);
            server.Start();

            IReadOnlyList<PackingProofNodeInfo> hosts = await WorkstationNetwork.DiscoverHostsAsync(
                null,
                [$"127.0.0.1:{port}"],
                WorkstationNetwork.GetNodeInfoAsync,
                token: TestContext.Current.CancellationToken);

            PackingProofNodeInfo host = Assert.Single(hosts);
            Assert.Equal(nodeId, host.NodeId);
            Assert.Equal("本地发现测试主机", host.NodeName);
            Assert.Equal($"http://127.0.0.1:{port}", host.Address);
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task OrdinaryHttpServiceIsNotRecognizedAsPackingProofHost()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        CancellationToken token = TestContext.Current.CancellationToken;
        Task responseTask = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync(token);
            await using NetworkStream stream = accepted.GetStream();
            byte[] requestBuffer = new byte[2048];
            _ = await stream.ReadAsync(requestBuffer, token);
            byte[] response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}");
            await stream.WriteAsync(response, token);
        }, token);

        PackingProofNodeInfo? node = await WorkstationNetwork.GetNodeInfoAsync(
            $"127.0.0.1:{port}",
            token);
        await responseTask;

        Assert.Null(node);
    }

    [Fact]
    public async Task ConnectionResetDuringNodeProbeIsTreatedAsUnavailable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        CancellationToken token = TestContext.Current.CancellationToken;
        Task resetTask = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync(token);
            await using NetworkStream stream = accepted.GetStream();
            byte[] requestBuffer = new byte[2048];
            _ = await stream.ReadAsync(requestBuffer, token);
            accepted.Client.LingerState = new LingerOption(enable: true, seconds: 0);
        }, token);

        PackingProofNodeInfo? node = await WorkstationNetwork.GetNodeInfoAsync(
            $"127.0.0.1:{port}",
            token);
        await resetTask;

        Assert.Null(node);
    }

    [Fact]
    public async Task DiscoveryValidatesSavedAddressFirstAndReturnsEveryUniqueHost()
    {
        string savedNodeId = Guid.NewGuid().ToString("D");
        string otherNodeId = Guid.NewGuid().ToString("D");
        var probes = new List<string>();
        var probeLock = new object();

        Task<PackingProofNodeInfo?> Probe(string address, CancellationToken _)
        {
            lock (probeLock)
                probes.Add(address);
            string? nodeId = address switch
            {
                "192.168.1.10:5280" => savedNodeId,
                "192.168.1.20:5280" => otherNodeId,
                "192.168.1.21:5280" => otherNodeId,
                _ => null
            };
            return Task.FromResult(nodeId == null ? null : ValidNode(nodeId, address));
        }

        IReadOnlyList<PackingProofNodeInfo> hosts = await WorkstationNetwork.DiscoverHostsAsync(
            "192.168.1.10:5280",
            ["192.168.1.20:5280", "192.168.1.21:5280", "192.168.1.99:5280"],
            Probe,
            token: TestContext.Current.CancellationToken);

        Assert.Equal("192.168.1.10:5280", probes[0]);
        Assert.Equal(2, hosts.Count);
        Assert.Equal(savedNodeId, hosts[0].NodeId);
        Assert.Contains(hosts, host => host.NodeId == otherNodeId);
    }

    [Fact]
    public async Task NodeIdResolutionUsesVerifiedSavedAddressWithoutDiscovery()
    {
        string nodeId = Guid.NewGuid().ToString("D");
        int discoveryCount = 0;

        PackingProofNodeInfo? resolved = await WorkstationNetwork.FindHostByNodeIdAsync(
            nodeId,
            "192.168.1.10:5280",
            (address, _) => Task.FromResult<PackingProofNodeInfo?>(ValidNode(nodeId, address)),
            (_, _) =>
            {
                discoveryCount++;
                return Task.FromResult<IReadOnlyList<PackingProofNodeInfo>>([]);
            },
            TestContext.Current.CancellationToken);

        Assert.NotNull(resolved);
        Assert.Equal("http://192.168.1.10:5280", resolved.Address);
        Assert.Equal(0, discoveryCount);
    }

    [Fact]
    public async Task NodeIdResolutionIgnoresWrongIdentityAndReturnsDiscoveredAddress()
    {
        string targetNodeId = Guid.NewGuid().ToString("D");
        string wrongNodeId = Guid.NewGuid().ToString("D");
        PackingProofNodeInfo discovered = ValidNode(targetNodeId, "192.168.1.30:5280");

        PackingProofNodeInfo? resolved = await WorkstationNetwork.FindHostByNodeIdAsync(
            targetNodeId,
            "192.168.1.10:5280",
            (address, _) => Task.FromResult<PackingProofNodeInfo?>(ValidNode(wrongNodeId, address)),
            (progress, _) =>
            {
                progress.Report(discovered);
                return Task.FromResult<IReadOnlyList<PackingProofNodeInfo>>([discovered]);
            },
            TestContext.Current.CancellationToken);

        Assert.Same(discovered, resolved);
    }

    [Fact]
    public async Task NodeIdResolutionDoesNotSubstituteAnotherDiscoveredHost()
    {
        PackingProofNodeInfo other = ValidNode(
            Guid.NewGuid().ToString("D"),
            "192.168.1.20:5280");

        PackingProofNodeInfo? resolved = await WorkstationNetwork.FindHostByNodeIdAsync(
            Guid.NewGuid().ToString("D"),
            "192.168.1.10:5280",
            (_, _) => Task.FromResult<PackingProofNodeInfo?>(null),
            (progress, _) =>
            {
                progress.Report(other);
                return Task.FromResult<IReadOnlyList<PackingProofNodeInfo>>([other]);
            },
            TestContext.Current.CancellationToken);

        Assert.Null(resolved);
    }

    [Fact]
    public void ViewerRecoverySelectsPreviouslyBoundHostAtItsCurrentAddress()
    {
        string boundNodeId = Guid.NewGuid().ToString("D");
        PackingProofNodeInfo otherHost = ValidNode(
            Guid.NewGuid().ToString("D"),
            "192.168.1.20:5280");
        PackingProofNodeInfo movedHost = ValidNode(
            boundNodeId,
            "192.168.1.30:5280");

        PackingProofNodeInfo recovered = Assert.IsType<PackingProofNodeInfo>(
            ViewerClientWindow.FindPreviouslyBoundHost(
                [otherHost, movedHost],
                boundNodeId.ToUpperInvariant()));

        Assert.Same(movedHost, recovered);
        Assert.Equal("http://192.168.1.30:5280", recovered.Address);
    }

    [Fact]
    public void ViewerRecoveryDoesNotAutomaticallySelectADifferentHost()
    {
        PackingProofNodeInfo host = ValidNode(
            Guid.NewGuid().ToString("D"),
            "192.168.1.20:5280");

        PackingProofNodeInfo? recovered = ViewerClientWindow.FindPreviouslyBoundHost(
            [host],
            Guid.NewGuid().ToString("D"));

        Assert.Null(recovered);
    }

    [Fact]
    public async Task InvalidAndTimedOutCandidatesDoNotAbortDiscovery()
    {
        Task<PackingProofNodeInfo?> Probe(string address, CancellationToken token) =>
            address.EndsWith(".20:5280", StringComparison.Ordinal)
                ? Task.FromResult<PackingProofNodeInfo?>(ValidNode(Guid.NewGuid().ToString("D"), address))
                : IgnoreTimeoutAsync(token);

        IReadOnlyList<PackingProofNodeInfo> hosts = await WorkstationNetwork.DiscoverHostsAsync(
            null,
            ["192.168.1.10:5280", "192.168.1.20:5280", "not-a-host"],
            Probe,
            token: TestContext.Current.CancellationToken);

        Assert.Single(hosts);
        Assert.Equal("http://192.168.1.20:5280", hosts[0].Address);
    }

    [Fact]
    public void DiscoveryUsesConfiguredAndDefaultHostPorts()
    {
        Assert.Equal([5300, 5280], WorkstationNetwork.GetDiscoveryPorts(5300));
        Assert.Equal([5280], WorkstationNetwork.GetDiscoveryPorts(5280));
    }

    [Fact]
    public void DiscoveryUsesTheActualIpv4SubnetInsteadOfOnlyTheLocalSlash24()
    {
        IReadOnlyList<IPAddress> addresses = WorkstationNetwork.EnumerateSubnetAddresses(
            IPAddress.Parse("192.168.30.10"),
            IPAddress.Parse("255.255.254.0"));

        Assert.Contains(IPAddress.Parse("192.168.31.250"), addresses);
        Assert.DoesNotContain(IPAddress.Parse("192.168.30.0"), addresses);
        Assert.DoesNotContain(IPAddress.Parse("192.168.31.255"), addresses);
        Assert.Equal(510, addresses.Count);
    }

    [Fact]
    public void BroadSubnetsUseABoundedLocalScanRange()
    {
        IReadOnlyList<IPAddress> addresses = WorkstationNetwork.EnumerateSubnetAddresses(
            IPAddress.Parse("10.20.30.40"),
            IPAddress.Parse("255.255.0.0"));

        Assert.Equal(254, addresses.Count);
        Assert.Contains(IPAddress.Parse("10.20.30.1"), addresses);
        Assert.Contains(IPAddress.Parse("10.20.30.254"), addresses);
        Assert.DoesNotContain(IPAddress.Parse("10.20.31.1"), addresses);
    }

    private static async Task<PackingProofNodeInfo?> IgnoreTimeoutAsync(CancellationToken token)
    {
        await Task.Delay(5, token);
        return null;
    }

    private static PackingProofNodeInfo ValidNode(string nodeId, string address) =>
        new()
        {
            Protocol = PackingProofNodeInfo.ExpectedProtocol,
            ProtocolVersion = PackingProofNodeInfo.SupportedProtocolVersion,
            NodeId = nodeId,
            NodeName = $"主机 {nodeId[..4]}",
            Preset = DeploymentPresets.RecordingHost,
            Capabilities = [PackingProofCapabilities.Host],
            HttpPort = 5280,
            Address = $"http://{address}"
        };

    private static int GetFreeTcpPort() =>
        TestPortAllocator.GetFreeTcpPort();
}
