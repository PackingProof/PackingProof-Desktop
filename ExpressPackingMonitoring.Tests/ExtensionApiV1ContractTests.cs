using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

[Collection("Web server tests")]
public sealed class ExtensionApiV1ContractTests
{
    private const string AccessKey = "extension-api-v1-fixture-key";

    [Fact]
    public async Task Capabilities_AdvertiseStableV1FeaturesAndLimits()
    {
        await WithServerAsync(async (client, _, _) =>
        {
            using HttpResponseMessage response = await client.GetAsync(
                "/api/extensions/v1/capabilities",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using JsonDocument payload = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            JsonElement root = payload.RootElement;
            Assert.Equal("v1", root.GetProperty("apiVersion").GetString());
            Assert.Equal("PackingProof", root.GetProperty("product").GetString());
            Assert.True(root.GetProperty("accessKeyRequired").GetBoolean());

            JsonElement features = root.GetProperty("features");
            Assert.True(features.GetProperty("ordersWrite").GetBoolean());
            Assert.True(features.GetProperty("signedScanTasks").GetBoolean());
            Assert.True(features.GetProperty("totalItemCount").GetBoolean());
            Assert.True(features.GetProperty("mergedOrderCount").GetBoolean());
            Assert.True(features.GetProperty("providerId").GetBoolean());
            Assert.True(features.GetProperty("recordingMetadataWrite").GetBoolean());
            Assert.True(features.GetProperty("watermarkFields").GetBoolean());

            JsonElement limits = root.GetProperty("limits");
            Assert.Equal(200, limits.GetProperty("maxOrdersPerRequest").GetInt32());
            Assert.Equal(1024 * 1024, limits.GetProperty("maxRequestBytes").GetInt32());
            Assert.Equal(4000, limits.GetProperty("maxFieldCharacters").GetInt32());
        });
    }

    [Fact]
    public async Task OrdersFixture_PreservesPublishedFieldsAndProviderIdentity()
    {
        await WithServerAsync(async (client, server, _) =>
        {
            using HttpResponseMessage response = await PostFixtureAsync(
                client,
                "/api/extensions/v1/orders",
                "orders.request.json");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using JsonDocument accepted = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.True(accepted.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("v1", accepted.RootElement.GetProperty("apiVersion").GetString());
            Assert.Equal("fixture.erp", accepted.RootElement.GetProperty("providerId").GetString());
            Assert.Equal(1, accepted.RootElement.GetProperty("receivedCount").GetInt32());

            OrderInfo? order = server.GetOrderInfo("EXT-FIXTURE-001");
            Assert.NotNull(order);
            Assert.Equal("ORDER-FIXTURE-001", order.OrderId);
            Assert.Equal("请轻放", order.BuyerMessage);
            Assert.Equal("核对颜色", order.SellerMemo);
            Assert.Equal("蓝色水杯 ×3", order.ProductInfo);
            Assert.Equal(3, order.TotalItemCount);
            Assert.Equal(2, order.MergedOrderCount);
            Assert.Equal("fixture.erp", order.ProviderId);
        });
    }

    [Fact]
    public async Task Orders_ReturnStableErrorsForUnsupportedVersionAndInvalidProvider()
    {
        await WithServerAsync(async (client, _, _) =>
        {
            using HttpResponseMessage unsupported = await PostJsonAsync(
                client,
                "/api/extensions/v1/orders",
                "{\"apiVersion\":\"v2\",\"providerId\":\"fixture.erp\",\"orders\":[{\"trackingNumber\":\"EXT-1\"}]}");
            Assert.Equal(HttpStatusCode.UpgradeRequired, unsupported.StatusCode);
            await AssertErrorCodeAsync(unsupported, "extension_api_version_unsupported");

            using HttpResponseMessage missingProvider = await PostJsonAsync(
                client,
                "/api/extensions/v1/orders",
                "{\"apiVersion\":\"v1\",\"providerId\":\"\",\"orders\":[{\"trackingNumber\":\"EXT-1\"}]}");
            Assert.Equal(HttpStatusCode.BadRequest, missingProvider.StatusCode);
            await AssertErrorCodeAsync(missingProvider, "provider_required");
        });
    }

    [Fact]
    public async Task RecordingDataFixture_FollowsActiveSessionLifecycleAndStableErrors()
    {
        await WithServerAsync(async (client, _, database) =>
        {
            string recordingPath = Path.Combine(Path.GetTempPath(), $"extension-contract-{Guid.NewGuid():N}.mp4");
            long recordId = database.InsertVideoRecord(
                "EXT-FIXTURE-001",
                "发货",
                "h264",
                "",
                recordingPath,
                DateTime.Now,
                recordingSessionId: "fixture-session");

            using HttpResponseMessage written = await PostFixtureAsync(
                client,
                "/api/extensions/v1/recordings/fixture-session/data",
                "recording-data.request.json");
            Assert.Equal(HttpStatusCode.OK, written.StatusCode);

            using HttpResponseMessage read = await client.GetAsync(
                "/api/extensions/v1/recordings/fixture-session/data",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            using JsonDocument payload = JsonDocument.Parse(
                await read.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            JsonElement[] fields = payload.RootElement.GetProperty("fields").EnumerateArray().ToArray();
            Assert.Equal(2, fields.Length);
            Assert.Contains(fields, field =>
                field.GetProperty("namespace").GetString() == "fixture.scale"
                && field.GetProperty("fieldName").GetString() == "weight"
                && field.GetProperty("value").GetString() == "1.25 kg");

            database.UpdateVideoRecordOnStop(recordId, DateTime.Now, 1, 10, "completed");
            using HttpResponseMessage ended = await PostFixtureAsync(
                client,
                "/api/extensions/v1/recordings/fixture-session/data",
                "recording-data.request.json");
            Assert.Equal(HttpStatusCode.Conflict, ended.StatusCode);
            await AssertErrorCodeAsync(ended, "recording_not_active");
        });
    }

    private static async Task WithServerAsync(
        Func<HttpClient, WebServer, VideoDatabase, Task> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "epm-extension-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        int port = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                requireAccessKey: true,
                accessKey: AccessKey,
                listenerHost: "127.0.0.1",
                nodeId: "fixture-host",
                nodeName: "扩展契约测试主机");
            server.Start();

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            client.DefaultRequestHeaders.Add("X-EPM-Access-Key", AccessKey);
            await action(client, server, database);
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static Task<HttpResponseMessage> PostFixtureAsync(HttpClient client, string path, string fileName)
    {
        string content = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ExtensionApiV1", fileName));
        return PostJsonAsync(client, path, content);
    }

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, string content) =>
        client.PostAsync(
            path,
            new StringContent(content, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

    private static async Task AssertErrorCodeAsync(HttpResponseMessage response, string expected)
    {
        using JsonDocument payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(expected, payload.RootElement.GetProperty("errorCode").GetString());
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
