using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

[Collection("Web server tests")]
public sealed class RecordingExtensionDataTests
{
    [Fact]
    public void ExtensionFields_UpsertLatestValueAndKeepNamespacesSeparate()
    {
        string directory = CreateDirectory();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            string sessionId = "pc-session-001";
            database.UpsertRecordingExtensionFields(sessionId, "scale.example", "scale", new Dictionary<string, string> { ["weight"] = "1.00 kg" }, DateTime.UtcNow.AddMinutes(-1));
            database.UpsertRecordingExtensionFields(sessionId, "scale.example", "scale", new Dictionary<string, string> { ["weight"] = "1.25 kg" }, DateTime.UtcNow);
            database.UpsertRecordingExtensionFields(sessionId, "camera.example", "camera", new Dictionary<string, string> { ["weight"] = "unknown" }, DateTime.UtcNow);

            IReadOnlyList<RecordingExtensionField> fields = database.GetRecordingExtensionFields(sessionId);
            Assert.Equal(2, fields.Count);
            Assert.Contains(fields, field => field.Namespace == "scale.example" && field.FieldName == "weight" && field.Value == "1.25 kg");
            Assert.Contains(fields, field => field.Namespace == "camera.example" && field.Value == "unknown");
        }
        finally { SqliteTestPool.ClearPoolFor(directory); TryDelete(directory); }
    }

    [Fact]
    public void LegacyDatabase_CreatesRecordingExtensionDataTable()
    {
        string directory = CreateDirectory();
        string databasePath = Path.Combine(directory, "legacy.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE VideoRecords (Id INTEGER PRIMARY KEY, OrderId TEXT NOT NULL, Mode TEXT NOT NULL, FilePath TEXT NOT NULL, StartTime TEXT NOT NULL);";
                command.ExecuteNonQuery();
            }
            using (var database = new VideoDatabase(databasePath))
                database.UpsertRecordingExtensionFields("legacy-session", "legacy", "test", new Dictionary<string, string> { ["value"] = "ok" }, DateTime.UtcNow);
            using var verify = new SqliteConnection($"Data Source={databasePath}");
            verify.Open();
            using var command2 = verify.CreateCommand();
            command2.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'RecordingExtensionData';";
            Assert.Equal(1L, Convert.ToInt64(command2.ExecuteScalar()));
        }
        finally { SqliteTestPool.ClearPoolFor(directory); TryDelete(directory); }
    }

    [Fact]
    public async Task RecordingExtensionApi_RequiresKeyAndSupportsActiveSessionLifecycle()
    {
        string directory = CreateDirectory();
        int port = GetFreeTcpPort();
        const string accessKey = "recording-extension-test-key";
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            long recordId = database.InsertVideoRecord("TRACK-001", "发货", "h264", "", Path.Combine(directory, "recording.mp4"), DateTime.Now, recordingSessionId: "session-active");
            using var server = new WebServer(database, port, requireAccessKey: true, accessKey: accessKey, listenerHost: "127.0.0.1", mobileBackupComputerId: Guid.NewGuid().ToString("D"), mobileBackupStateDirectory: Path.Combine(directory, "uploads"), mobileBackupRecordingRootResolver: () => directory, nodeId: Guid.NewGuid().ToString("D"), nodeName: "扩展测试主机", deploymentPreset: DeploymentPresets.MobileBackupHost);
            server.Start();

            using var unauthorized = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using HttpResponseMessage unauthorizedResponse = await unauthorized.GetAsync("/api/extensions/v1/recordings/active", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedResponse.StatusCode);

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            client.DefaultRequestHeaders.Add("X-EPM-Access-Key", accessKey);
            using HttpResponseMessage activeResponse = await client.GetAsync("/api/extensions/v1/recordings/active", TestContext.Current.CancellationToken);
            using JsonDocument active = JsonDocument.Parse(await activeResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("session-active", active.RootElement.GetProperty("recordings")[0].GetProperty("recordingSessionId").GetString());

            using HttpResponseMessage post = await client.PostAsJsonAsync("/api/extensions/v1/recordings/session-active/data", new { @namespace = "scale.example", providerId = "scale", fields = new { weight = "1.25 kg", length = "30 cm" } }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);
            using HttpResponseMessage dataResponse = await client.GetAsync("/api/extensions/v1/recordings/session-active/data", TestContext.Current.CancellationToken);
            using JsonDocument data = JsonDocument.Parse(await dataResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(2, data.RootElement.GetProperty("fields").GetArrayLength());

            database.UpdateVideoRecordOnStop(recordId, DateTime.Now, 1, 10, "completed");
            using HttpResponseMessage endedPost = await client.PostAsJsonAsync("/api/extensions/v1/recordings/session-active/data", new { @namespace = "scale.example", providerId = "scale", fields = new { weight = "2 kg" } }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Conflict, endedPost.StatusCode);
        }
        finally { SqliteTestPool.ClearPoolFor(directory); TryDelete(directory); }
    }

    private static string CreateDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "epm-recording-extension-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
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
