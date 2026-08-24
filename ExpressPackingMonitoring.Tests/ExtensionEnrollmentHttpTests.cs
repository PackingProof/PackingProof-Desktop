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

    [Fact]
    public async Task SignedTaskHttpLifecycle_PollsAcknowledgesAndPersistsResult()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "epm-extension-signed-task-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string databasePath = Path.Combine(directory, "videos.db");
        int port = GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(databasePath);
            database.InsertVideoRecord(
                "EXT-TASK-001",
                "发货",
                "h264",
                "",
                Path.Combine(directory, "task.mp4"),
                DateTime.Now,
                recordingSessionId: "task-session");
            var authorizations = new ExtensionAuthorizationStore(directory);
            using var server = new WebServer(
                database,
                port,
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
            OrderInfo? appliedOrder = null;
            using var runtime = new ExtensionRuntime(
                database,
                databasePath,
                "fixture-host",
                authorizations,
                (_, _) => { },
                order => appliedOrder = order);
            server.ConfigureExtensionTaskApi(
                runtime.Broker,
                runtime.Coordinator,
                runtime.ProcessAvailableResults);
            server.Start();

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using HttpResponseMessage enrollment = await client.PostAsync(
                "/api/extensions/v1/enroll",
                JsonContent(RequestJson()),
                TestContext.Current.CancellationToken);
            enrollment.EnsureSuccessStatusCode();
            using JsonDocument enrollmentPayload = JsonDocument.Parse(
                await enrollment.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            string credential = enrollmentPayload.RootElement.GetProperty("credential").GetString()!;
            int generation = enrollmentPayload.RootElement.GetProperty("credentialGeneration").GetInt32();

            ExtensionScanDelivery expectedDelivery = Assert.Single(runtime.Publish(
                "fixture-host",
                "task-session",
                "EXT-TASK-001",
                "发货").Deliveries);
            const string pollPath = "/api/extensions/v1/scan-tasks/next?waitSeconds=0";
            using HttpResponseMessage polled = await client.SendAsync(
                SignedRequest(HttpMethod.Get, pollPath, "", credential, generation, new string('f', 32)),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, polled.StatusCode);
            using JsonDocument taskPayload = JsonDocument.Parse(
                await polled.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(expectedDelivery.DeliveryId, taskPayload.RootElement.GetProperty("deliveryId").GetString());
            Assert.Equal("task-session", taskPayload.RootElement.GetProperty("recordingSessionId").GetString());
            string taskId = taskPayload.RootElement.GetProperty("taskId").GetString()!;

            string ackPath = $"/api/extensions/v1/scan-tasks/{expectedDelivery.DeliveryId}/ack";
            string ackBody = JsonSerializer.Serialize(new { taskId });
            using HttpResponseMessage acknowledged = await client.SendAsync(
                SignedRequest(HttpMethod.Post, ackPath, ackBody, credential, generation, new string('1', 32)),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, acknowledged.StatusCode);

            const string resultPath = "/api/extensions/v1/scan-results";
            string resultBody = JsonSerializer.Serialize(new
            {
                deliveryId = expectedDelivery.DeliveryId,
                taskId,
                providerId = "fixture.erp",
                resultId = "task-result-001",
                revision = 1,
                status = "found",
                observedAt = DateTimeOffset.UtcNow,
                orders = new[]
                {
                    new
                    {
                        trackingNumber = "EXT-TASK-001",
                        orderId = "ORDER-TASK-001",
                        totalItemCount = 2,
                        products = new[] { new { name = "测试商品", quantity = 2 } },
                        refundState = "none"
                    }
                },
                measurements = Array.Empty<object>()
            });
            using HttpResponseMessage accepted = await client.SendAsync(
                SignedRequest(HttpMethod.Post, resultPath, resultBody, credential, generation, new string('2', 32)),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            Assert.True(SpinWait.SpinUntil(
                () => Volatile.Read(ref appliedOrder)?.OrderId == "ORDER-TASK-001",
                TimeSpan.FromSeconds(3)));

            using HttpResponseMessage duplicate = await client.SendAsync(
                SignedRequest(HttpMethod.Post, resultPath, resultBody, credential, generation, new string('3', 32)),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
            using JsonDocument duplicatePayload = JsonDocument.Parse(
                await duplicate.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.True(duplicatePayload.RootElement.GetProperty("duplicate").GetBoolean());
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SignedRecordingQuery_SearchesPollsAndDownloadsWithoutWebKey()
    {
        string directory = Path.Combine(Path.GetTempPath(), "epm-extension-recording-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        int port = GetFreeTcpPort();
        try
        {
            byte[] expectedVideo = [10, 20, 30, 40, 50];
            string videoPath = Path.Combine(directory, "recording.mp4");
            await File.WriteAllBytesAsync(videoPath, expectedVideo, TestContext.Current.CancellationToken);
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            DateTime startedAt = DateTime.Now.AddMinutes(-2);
            long recordId = database.InsertVideoRecord("BOT-TRACK-001", "发货", "h264", "", videoPath, startedAt);
            database.UpdateVideoRecordOnStop(recordId, startedAt.AddMinutes(1), 60, expectedVideo.Length, "手动");
            var authorizations = new ExtensionAuthorizationStore(directory);
            using var server = new WebServer(
                database,
                port,
                requireAccessKey: true,
                accessKey: "web-key-must-not-authorize-bot",
                listenerHost: "127.0.0.1",
                mobileBackupStateDirectory: directory,
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
                    [ExtensionPermissions.RecordingsSearch, ExtensionPermissions.RecordingsDownload, ExtensionPermissions.RecordingsDelivery],
                    [])),
                TestContext.Current.CancellationToken);
            enrollment.EnsureSuccessStatusCode();
            using JsonDocument enrollmentPayload = JsonDocument.Parse(
                await enrollment.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            string credential = enrollmentPayload.RootElement.GetProperty("credential").GetString()!;
            int generation = enrollmentPayload.RootElement.GetProperty("credentialGeneration").GetInt32();

            const string createPath = "/api/extensions/v1/recording-queries";
            string createBody = JsonSerializer.Serialize(new { trackingNumber = "BOT-TRACK-001" });
            using HttpResponseMessage created = await client.SendAsync(
                SignedRequest(HttpMethod.Post, createPath, createBody, credential, generation, new string('3', 32)),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
            using JsonDocument createdPayload = JsonDocument.Parse(
                await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            string queryId = createdPayload.RootElement.GetProperty("queryId").GetString()!;

            string queryPath = $"/api/extensions/v1/recording-queries/{queryId}";
            JsonDocument? readyPayload = null;
            for (int attempt = 0; attempt < 100 && readyPayload == null; attempt++)
            {
                string nonce = attempt.ToString("x32");
                using HttpResponseMessage queried = await client.SendAsync(
                    SignedRequest(HttpMethod.Get, queryPath, "", credential, generation, nonce),
                    TestContext.Current.CancellationToken);
                queried.EnsureSuccessStatusCode();
                JsonDocument payload = JsonDocument.Parse(
                    await queried.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                if (payload.RootElement.GetProperty("status").GetString() == "ready") readyPayload = payload;
                else
                {
                    payload.Dispose();
                    await Task.Delay(20, TestContext.Current.CancellationToken);
                }
            }
            Assert.NotNull(readyPayload);
            using (readyPayload)
            {
                JsonElement recording = Assert.Single(readyPayload.RootElement.GetProperty("recordings").EnumerateArray());
                Assert.Equal("h264", recording.GetProperty("videoCodec").GetString());
                Assert.Equal(60, recording.GetProperty("durationSeconds").GetDouble());
                Assert.Equal(expectedVideo.Length, recording.GetProperty("fileSizeBytes").GetInt64());
                Assert.Equal("recording.mp4", recording.GetProperty("fileName").GetString());
                string deliveryPath = $"/api/extensions/v1/recording-queries/{queryId}/recordings/{recordId}/deliveries";
                string deliveryBody = JsonSerializer.Serialize(new { profile = "source_codec_target_size", maxFileSizeMb = 190 });
                using HttpResponseMessage deliveryCreated = await client.SendAsync(
                    SignedRequest(HttpMethod.Post, deliveryPath, deliveryBody, credential, generation, new string('d', 32)),
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.Accepted, deliveryCreated.StatusCode);
                using JsonDocument deliveryPayload = JsonDocument.Parse(
                    await deliveryCreated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                Assert.Equal(queryId, deliveryPayload.RootElement.GetProperty("queryId").GetString());
                Assert.Equal(recordId, deliveryPayload.RootElement.GetProperty("recordingId").GetInt64());
                Assert.Equal("recording_转码.mp4", deliveryPayload.RootElement.GetProperty("fileName").GetString());
                string downloadPath = recording.GetProperty("downloadUrl").GetString()!;
                using HttpResponseMessage downloaded = await client.SendAsync(
                    SignedRequest(HttpMethod.Get, downloadPath, "", credential, generation, new string('a', 32)),
                    TestContext.Current.CancellationToken);
                downloaded.EnsureSuccessStatusCode();
                Assert.Equal(expectedVideo, await downloaded.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            }
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
