using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

[Collection("Web server tests")]
public sealed class ExtensionEnrollmentHttpTests
{
    [Fact]
    public void Constructor_DoesNotCreateExtensionStateBeforeEnrollment()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "epm-extension-enrollment-lazy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                GetFreeTcpPort(),
                listenerHost: "127.0.0.1",
                mobileBackupStateDirectory: directory);

            Assert.False(Directory.Exists(Path.Combine(directory, "extensions")));
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DisabledExtensionApi_RejectsEnrollmentWithoutCreatingState()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "epm-extension-enrollment-disabled-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        int port = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupStateDirectory: directory);
            server.Start();

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using HttpResponseMessage capabilities = await client.GetAsync(
                "/api/extensions/v1/capabilities",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, capabilities.StatusCode);
            using JsonDocument capabilityPayload = JsonDocument.Parse(
                await capabilities.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.False(capabilityPayload.RootElement.GetProperty("extensionApiEnabled").GetBoolean());

            using HttpResponseMessage enrollment = await client.PostAsync(
                "/api/extensions/v1/enroll",
                JsonContent(RequestJson()),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, enrollment.StatusCode);
            using JsonDocument enrollmentPayload = JsonDocument.Parse(
                await enrollment.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(
                "extension_disabled",
                enrollmentPayload.RootElement.GetProperty("errorCode").GetString());
            Assert.False(Directory.Exists(Path.Combine(directory, "extensions")));
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Enroll_UsesExtensionCredentialWithoutWebAccessKey()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "epm-extension-enrollment-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        int port = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            var authorizations = new ExtensionAuthorizationStore(directory);
            using var server = new WebServer(
                database,
                port,
                requireAccessKey: true,
                accessKey: "web-access-key-must-not-be-required",
                listenerHost: "127.0.0.1",
                mobileBackupStateDirectory: directory,
                nodeId: "host-node-fixture",
                nodeName: "测试主机",
                extensionApiEnabled: true);
            server.ConfigureExtensionEnrollment(
                authorizations,
                request => new ExtensionEnrollmentApprovalResult
                {
                    Disposition = ExtensionEnrollmentApprovalDisposition.Approved,
                    ApprovedPermissions = request.RequestedPermissions,
                    ApprovedCapabilities = request.RequestedCapabilities,
                    RoutingScope = ExtensionRoutingScope.AllLocalRecordingNodes
                });
            server.Start();

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using HttpResponseMessage response = await client.PostAsync(
                "/api/extensions/v1/enroll",
                JsonContent(RequestJson()),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? "");
            Assert.Contains("no-cache", response.Headers.Pragma.Select(value => value.Name));
            using JsonDocument payload = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("v1", payload.RootElement.GetProperty("apiVersion").GetString());
            string credential = payload.RootElement.GetProperty("credential").GetString() ?? "";
            Assert.NotEmpty(credential);
            Assert.True(authorizations.TryAuthenticate(
                "extension-fixture-01",
                credential,
                out ExtensionAuthorizationContext? authorization));
            Assert.NotNull(authorization);
            Assert.True(authorization.HasPermission(ExtensionPermissions.ScanTasksRead));
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(1, HttpStatusCode.Forbidden, "extension_enrollment_denied")]
    [InlineData(2, HttpStatusCode.ServiceUnavailable, "extension_enrollment_approval_unavailable")]
    public async Task Enroll_ReturnsStructuredApprovalFailure(
        int dispositionValue,
        HttpStatusCode expectedStatus,
        string expectedErrorCode)
    {
        var disposition = (ExtensionEnrollmentApprovalDisposition)dispositionValue;
        string directory = Path.Combine(
            Path.GetTempPath(),
            "epm-extension-enrollment-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        int port = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupStateDirectory: directory,
                extensionApiEnabled: true);
            server.ConfigureExtensionEnrollment(
                new ExtensionAuthorizationStore(directory),
                _ => new ExtensionEnrollmentApprovalResult
                {
                    Disposition = disposition
                });
            server.Start();

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using HttpResponseMessage response = await client.PostAsync(
                "/api/extensions/v1/enroll",
                JsonContent(RequestJson()),
                TestContext.Current.CancellationToken);

            Assert.Equal(expectedStatus, response.StatusCode);
            using JsonDocument payload = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(expectedErrorCode, payload.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EnrollmentRoute_IsRateLimitedAndDoesNotUseWebAccessKey()
    {
        Assert.Equal(
            LanRequestCategory.Enrollment,
            WebServer.ClassifyRequest("POST", "/api/extensions/v1/enroll"));
        Assert.False(WebServer.RequiresAccessKey("/api/extensions/v1/enroll"));
    }

    private static string RequestJson() => JsonSerializer.Serialize(new
    {
        requestId = "request-fixture-01",
        requestSecret = new string('a', 64),
        extensionInstanceId = "extension-fixture-01",
        providerId = "fixture.erp",
        displayName = "测试 ERP 扩展",
        version = "1.0",
        source = "https://example.test/extension",
        requestedPermissions = new[]
        {
            ExtensionPermissions.ScanTasksRead,
            ExtensionPermissions.ScanResultsWrite
        },
        requestedCapabilities = new[] { ExtensionScanCapabilities.OrderLookup }
    });

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
