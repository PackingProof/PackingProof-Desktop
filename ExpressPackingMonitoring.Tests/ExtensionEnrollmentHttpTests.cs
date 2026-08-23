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

    [Fact]
    public async Task SignedHeartbeat_AuthenticatesCredentialAndRejectsReplay()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "epm-extension-signed-heartbeat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        int port = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                requireAccessKey: true,
                accessKey: "web-key-is-not-an-extension-credential",
                listenerHost: "127.0.0.1",
                mobileBackupStateDirectory: directory,
                extensionApiEnabled: true);
            var authorizations = new ExtensionAuthorizationStore(directory);
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
            using HttpResponseMessage unsigned = await client.PostAsync(
                "/api/extensions/v1/heartbeat",
                JsonContent("{}"),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
            using JsonDocument unsignedPayload = JsonDocument.Parse(
                await unsigned.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(
                "extension_auth_required",
                unsignedPayload.RootElement.GetProperty("errorCode").GetString());

            using HttpResponseMessage enrollment = await client.PostAsync(
                "/api/extensions/v1/enroll",
                JsonContent(RequestJson()),
                TestContext.Current.CancellationToken);
            using JsonDocument enrollmentPayload = JsonDocument.Parse(
                await enrollment.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            string credential = enrollmentPayload.RootElement.GetProperty("credential").GetString()!;
            int generation = enrollmentPayload.RootElement.GetProperty("credentialGeneration").GetInt32();
            string nonce = new string('a', 32);

            using HttpResponseMessage accepted = await client.SendAsync(
                SignedHeartbeat(credential, generation, nonce),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            using JsonDocument acceptedPayload = JsonDocument.Parse(
                await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.True(acceptedPayload.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(
                "extension-fixture-01",
                acceptedPayload.RootElement.GetProperty("extensionInstanceId").GetString());
            ExtensionAuthorizationContext heartbeatState = Assert.Single(
                authorizations.GetAll(includeRevoked: false));
            Assert.NotNull(heartbeatState.LastSeenUtc);
            Assert.Equal("1.2", heartbeatState.RuntimeVersion);

            using HttpResponseMessage permissionDenied = await client.SendAsync(
                SignedRequest(
                    HttpMethod.Get,
                    "/api/extensions/v1/recordings/active",
                    "",
                    credential,
                    generation,
                    new string('b', 32)),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, permissionDenied.StatusCode);
            using JsonDocument permissionPayload = JsonDocument.Parse(
                await permissionDenied.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(
                "extension_permission_denied",
                permissionPayload.RootElement.GetProperty("errorCode").GetString());

            using HttpResponseMessage replayed = await client.SendAsync(
                SignedHeartbeat(credential, generation, nonce),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Conflict, replayed.StatusCode);
            using JsonDocument replayPayload = JsonDocument.Parse(
                await replayed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(
                "extension_auth_replay_detected",
                replayPayload.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SignedCredential_CanUseLegacyExtensionEndpointsWithinApprovedPermissions()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "epm-extension-signed-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        int port = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            database.InsertVideoRecord(
                "EXT-SIGNED-001",
                "发货",
                "h264",
                "",
                Path.Combine(directory, "signed.mp4"),
                DateTime.Now,
                recordingSessionId: "signed-session");
            var authorizations = new ExtensionAuthorizationStore(directory);
            using var server = new WebServer(
                database,
                port,
                requireAccessKey: true,
                accessKey: "web-key-must-not-authorize-signed-extension",
                listenerHost: "127.0.0.1",
                mobileBackupStateDirectory: directory,
                nodeId: "fixture-host",
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
            using HttpResponseMessage enrollment = await client.PostAsync(
                "/api/extensions/v1/enroll",
                JsonContent(RequestJson(
                    [
                        ExtensionPermissions.OrdersWrite,
                        ExtensionPermissions.RecordingsActiveRead,
                        ExtensionPermissions.RecordingFieldsWrite
                    ],
                    [])),
                TestContext.Current.CancellationToken);
            enrollment.EnsureSuccessStatusCode();
            using JsonDocument enrollmentPayload = JsonDocument.Parse(
                await enrollment.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            string credential = enrollmentPayload.RootElement.GetProperty("credential").GetString()!;
            int generation = enrollmentPayload.RootElement.GetProperty("credentialGeneration").GetInt32();

            const string ordersPath = "/api/extensions/v1/orders";
            string ordersBody = JsonSerializer.Serialize(new
            {
                apiVersion = "v1",
                providerId = "fixture.erp",
                orders = new[] { new { trackingNumber = "EXT-SIGNED-001", orderId = "ORDER-SIGNED-001" } }
            });
            using HttpResponseMessage orders = await client.SendAsync(
                SignedRequest(HttpMethod.Post, ordersPath, ordersBody, credential, generation, new string('b', 32)),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, orders.StatusCode);

            const string activePath = "/api/extensions/v1/recordings/active";
            using HttpResponseMessage active = await client.SendAsync(
                SignedRequest(HttpMethod.Get, activePath, "", credential, generation, new string('c', 32)),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, active.StatusCode);

            const string dataPath = "/api/extensions/v1/recordings/signed-session/data";
            string dataBody = JsonSerializer.Serialize(new
            {
                @namespace = "fixture.scale",
                providerId = "fixture.erp",
                fields = new { weight = "1.25 kg" }
            });
            using HttpResponseMessage written = await client.SendAsync(
                SignedRequest(HttpMethod.Post, dataPath, dataBody, credential, generation, new string('d', 32)),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, written.StatusCode);

            string spoofedBody = ordersBody.Replace("fixture.erp", "spoofed.erp", StringComparison.Ordinal);
            using HttpResponseMessage spoofed = await client.SendAsync(
                SignedRequest(HttpMethod.Post, ordersPath, spoofedBody, credential, generation, new string('e', 32)),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, spoofed.StatusCode);
            using JsonDocument spoofedPayload = JsonDocument.Parse(
                await spoofed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(
                "extension_provider_forbidden",
                spoofedPayload.RootElement.GetProperty("errorCode").GetString());
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
        Assert.Equal(
            LanRequestCategory.Heartbeat,
            WebServer.ClassifyRequest("POST", "/api/extensions/v1/heartbeat"));
        Assert.False(WebServer.RequiresAccessKey("/api/extensions/v1/enroll"));
    }

    private static string RequestJson(
        IReadOnlyList<string>? permissions = null,
        IReadOnlyList<string>? capabilities = null) => JsonSerializer.Serialize(new
    {
        requestId = "request-fixture-01",
        requestSecret = new string('a', 64),
        extensionInstanceId = "extension-fixture-01",
        providerId = "fixture.erp",
        displayName = "测试 ERP 扩展",
        version = "1.0",
        source = "https://example.test/extension",
        requestedPermissions = permissions ??
        [
            ExtensionPermissions.ScanTasksRead,
            ExtensionPermissions.ScanResultsWrite
        ],
        requestedCapabilities = capabilities ?? [ExtensionScanCapabilities.OrderLookup]
    });

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static HttpRequestMessage SignedHeartbeat(
        string credential,
        int credentialGeneration,
        string nonce)
    {
        const string path = "/api/extensions/v1/heartbeat";
        return SignedRequest(
            HttpMethod.Post,
            path,
            "{\"version\":\"1.2\",\"capabilities\":[\"orders.lookup\"]}",
            credential,
            credentialGeneration,
            nonce);
    }

    private static HttpRequestMessage SignedRequest(
        HttpMethod method,
        string path,
        string bodyText,
        string credential,
        int credentialGeneration,
        string nonce)
    {
        byte[] body = Encoding.UTF8.GetBytes(bodyText);
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string contentHash = ExtensionRequestSignature.ComputeContentHash(body);
        string signature = ExtensionRequestSignature.Create(
            credential,
            method.Method,
            path,
            timestamp,
            nonce,
            contentHash,
            "extension-fixture-01",
            credentialGeneration);
        var request = new HttpRequestMessage(method, path);
        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new("application/json");
        }
        request.Headers.TryAddWithoutValidation(ExtensionRequestSignature.VersionHeader, "1");
        request.Headers.TryAddWithoutValidation(
            ExtensionRequestSignature.InstanceIdHeader,
            "extension-fixture-01");
        request.Headers.TryAddWithoutValidation(
            ExtensionRequestSignature.CredentialGenerationHeader,
            credentialGeneration.ToString());
        request.Headers.TryAddWithoutValidation(
            ExtensionRequestSignature.TimestampHeader,
            timestamp.ToString());
        request.Headers.TryAddWithoutValidation(ExtensionRequestSignature.NonceHeader, nonce);
        request.Headers.TryAddWithoutValidation(
            ExtensionRequestSignature.ContentHashHeader,
            contentHash);
        request.Headers.TryAddWithoutValidation(
            ExtensionRequestSignature.SignatureHeader,
            signature);
        return request;
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
