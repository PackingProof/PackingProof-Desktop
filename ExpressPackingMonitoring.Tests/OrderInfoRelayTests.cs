using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

[Collection("Web server tests")]
public sealed class OrderInfoRelayTests
{
    [Fact]
    public async Task BroadcastEndpoint_DeliversToLocalRecordingHost()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"epm-order-relay-{Guid.NewGuid():N}");
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
                mobileBackupStateDirectory: Path.Combine(directory, "state"),
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"),
                nodeId: nodeId,
                nodeName: "本机录像主机",
                deploymentPreset: DeploymentPresets.RecordingHost);
            var received = new TaskCompletionSource<IReadOnlyList<OrderInfo>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            server.OrderInfoReceived += orders => received.TrySetResult(orders);
            server.Start();

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using var content = new StringContent(
                $"{{\"orders\":[{{\"trackingNumber\":\"TEST-1\",\"isTest\":true}}],\"targetNodeIds\":[\"{nodeId}\"]}}",
                Encoding.UTF8,
                "application/json");
            using HttpResponseMessage response = await client.PostAsync(
                "/api/orderinfo/broadcast",
                content,
                TestContext.Current.CancellationToken);
            string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using JsonDocument document = JsonDocument.Parse(body);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            JsonElement result = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
            Assert.True(result.GetProperty("ok").GetBoolean());
            Assert.Equal(nodeId, result.GetProperty("nodeId").GetString());
            Assert.Equal(1, result.GetProperty("testCount").GetInt32());
            Assert.Single(await received.Task.WaitAsync(
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken));

            using var unconfiguredContent = new StringContent(
                "{\"orders\":[{\"trackingNumber\":\"TEST-2\"}],\"targetNodeIds\":[\"other-node\"]}",
                Encoding.UTF8,
                "application/json");
            using HttpResponseMessage unconfiguredResponse = await client.PostAsync(
                "/api/orderinfo/broadcast",
                unconfiguredContent,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Conflict, unconfiguredResponse.StatusCode);

            string tooManyTargets = string.Join(',', Enumerable.Range(1, 9).Select(index => $"\"node-{index}\""));
            using var tooManyContent = new StringContent(
                $"{{\"orders\":[{{\"trackingNumber\":\"TEST-3\"}}],\"targetNodeIds\":[{tooManyTargets}]}}",
                Encoding.UTF8,
                "application/json");
            using HttpResponseMessage tooManyResponse = await client.PostAsync(
                "/api/orderinfo/broadcast",
                tooManyContent,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, tooManyResponse.StatusCode);
            using JsonDocument tooManyDocument = JsonDocument.Parse(
                await tooManyResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(8, tooManyDocument.RootElement.GetProperty("maxTargets").GetInt32());
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task BroadcastAsync_StartsRemoteDeliveriesConcurrently()
    {
        RecordingDeviceInfo[] devices =
        [
            Device("node-a", "http://192.168.1.20:5280"),
            Device("node-b", "http://192.168.1.21:5280")
        ];
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;

        IReadOnlyList<OrderInfoRelayResult> results = await OrderInfoRelay.BroadcastAsync(
            devices,
            "host-node",
            [new OrderInfo { TrackingNumber = "TEST-1" }],
            async (device, _, _) =>
            {
                if (Interlocked.Increment(ref started) == devices.Length)
                    bothStarted.TrySetResult();
                await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
                return Success(device);
            },
            Success,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.Ok));
    }

    [Fact]
    public async Task BroadcastAsync_RejectsSuccessReportedByDifferentNode()
    {
        RecordingDeviceInfo device = Device("expected-node", "http://192.168.1.20:5280");

        IReadOnlyList<OrderInfoRelayResult> results = await OrderInfoRelay.BroadcastAsync(
            [device],
            "host-node",
            [new OrderInfo { TrackingNumber = "TEST-1" }],
            (_, _, _) => Task.FromResult(new OrderInfoRelayResult(
                "different-node", "其他设备", "pc", device.Address, true, 200)),
            Success,
            TestContext.Current.CancellationToken);

        OrderInfoRelayResult result = Assert.Single(results);
        Assert.False(result.Ok);
        Assert.Equal("identity_mismatch", result.Error);
        Assert.Equal(device.NodeId, result.NodeId);
    }

    [Fact]
    public async Task SendAsync_VerifiesNodeBeforePostingOrders()
    {
        var requests = new List<HttpMethod>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.Method);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"nodeId\":\"different-node\"}", Encoding.UTF8, "application/json")
            };
        }));
        RecordingDeviceInfo device = Device("expected-node", "http://192.168.1.20:5280");

        OrderInfoRelayResult result = await OrderInfoRelay.SendAsync(
            device,
            [new OrderInfo { TrackingNumber = "TEST-1" }],
            client,
            TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal("identity_mismatch", result.Error);
        Assert.Equal([HttpMethod.Get], requests);
    }

    [Fact]
    public async Task SendAsync_UsesCamelCaseAndAcceptsLegacyResponseWithoutNodeId()
    {
        string? requestBody = null;
        int requestIndex = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            if (requestIndex++ == 0)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"nodeId\":\"expected-node\"}", Encoding.UTF8, "application/json")
                };
            }

            requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                // Older receivers do not echo nodeId in their POST response.
                Content = new StringContent("{\"ok\":true,\"testCount\":1}", Encoding.UTF8, "application/json")
            };
        }));
        RecordingDeviceInfo device = Device("expected-node", "http://192.168.1.20:5280");

        OrderInfoRelayResult result = await OrderInfoRelay.SendAsync(
            device,
            [new OrderInfo { TrackingNumber = "TEST-1", IsTest = true }],
            client,
            TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(1, result.TestCount);
        Assert.NotNull(requestBody);
        string body = requestBody!;
        Assert.Contains("\"trackingNumber\":\"TEST-1\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"TrackingNumber\"", body, StringComparison.Ordinal);
        Assert.Contains("\"isTest\":true", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_RejectsExplicitIdentityMismatchInDeliveryResponse()
    {
        int requestIndex = 0;
        using var client = new HttpClient(new StubHandler(_ =>
        {
            requestIndex++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    requestIndex == 1
                        ? "{\"nodeId\":\"expected-node\"}"
                        : "{\"ok\":true,\"nodeId\":\"different-node\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        }));
        RecordingDeviceInfo device = Device("expected-node", "http://192.168.1.20:5280");

        OrderInfoRelayResult result = await OrderInfoRelay.SendAsync(
            device,
            [new OrderInfo { TrackingNumber = "TEST-1" }],
            client,
            TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal("identity_mismatch", result.Error);
    }

    private static RecordingDeviceInfo Device(string nodeId, string address) => new()
    {
        NodeId = nodeId,
        NodeName = nodeId,
        DeviceType = "pc",
        Address = address,
        Online = true
    };

    private static OrderInfoRelayResult Success(RecordingDeviceInfo device) => new(
        device.NodeId,
        device.NodeName,
        device.DeviceType,
        device.Address,
        true,
        200,
        1);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
