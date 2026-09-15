using System.Text.Json;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 来源筛选接口必须给出设备当前的名字。录像记录保存的是写入当时的名字快照，
/// 设备改名后老记录仍带旧昵称，接口不能把老名字当成一台独立设备返回，
/// 否则回放窗口、网页端与手机端的筛选里都会留下已经不存在的老名字。
/// </summary>
[Collection("Web server tests")]
public sealed class VideoSourceFilterEndpointTests
{
    [Fact]
    public async Task VideoSourcesEndpoint_ReturnsLatestNameInsteadOfStaleSnapshot()
    {
        const string accessKey = "secret-web-access-key";
        string directory = Path.Combine(Path.GetTempPath(), $"epm-video-sources-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        int port = TestPortAllocator.GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            // 老名字刻意取"手机2"：旧实现按字典序取 MAX(SourceDeviceName)，
            // 手(0xE6..) 排在 安(0xE5..) 之后，旧行为会返回老名字，这个用例才抓得住回归。
            database.InsertMobileBackupRecord(
                "A", Path.Combine(directory, "a.mp4"), 1, DateTime.Now.AddMinutes(-10), 3,
                "phone-a", "手机2", "session-a", "sha-a");
            database.InsertMobileBackupRecord(
                "B", Path.Combine(directory, "b.mp4"), 1, DateTime.Now.AddMinutes(-5), 3,
                "phone-a", "安卓1", "session-b", "sha-b");

            using var server = new WebServer(
                database,
                port,
                requireAccessKey: true,
                accessKey: accessKey,
                listenerHost: "127.0.0.1",
                mobileBackupComputerId: Guid.NewGuid().ToString("D"),
                mobileBackupStateDirectory: Path.Combine(directory, "uploads"),
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"),
                nodeId: Guid.NewGuid().ToString("D"),
                nodeName: "来源筛选测试主机",
                deploymentPreset: DeploymentPresets.MobileBackupHost);
            server.Start();

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using JsonDocument doc = JsonDocument.Parse(await client.GetStringAsync(
                $"/api/video-sources?key={accessKey}",
                TestContext.Current.CancellationToken));

            JsonElement source = Assert.Single(
                doc.RootElement.GetProperty("data").EnumerateArray(),
                item => item.GetProperty("deviceId").GetString() == "phone-a");

            Assert.Equal("安卓1", source.GetProperty("name").GetString());
            Assert.Equal(2, source.GetProperty("videoCount").GetInt32());
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// 两台设备同名时下拉只出现一项，但这一项必须带上两台设备的设备号：
    /// 前端按集合筛选，改名那台设备改名前的记录才不会漏。
    /// </summary>
    [Fact]
    public async Task VideoSourcesEndpoint_MergedSameNameDevicesKeepEveryDeviceId()
    {
        const string accessKey = "secret-web-access-key";
        string directory = Path.Combine(Path.GetTempPath(), $"epm-video-sources-merged-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        int port = TestPortAllocator.GetFreeTcpPort();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            database.InsertMobileBackupRecord(
                "A", Path.Combine(directory, "a.mp4"), 1, DateTime.Now.AddMinutes(-30), 3,
                "phone-a", "手机2", "session-a", "sha-a");
            database.InsertMobileBackupRecord(
                "B", Path.Combine(directory, "b.mp4"), 1, DateTime.Now.AddMinutes(-20), 3,
                "phone-a", "安卓1", "session-b", "sha-b");
            database.InsertMobileBackupRecord(
                "C", Path.Combine(directory, "c.mp4"), 1, DateTime.Now.AddMinutes(-10), 3,
                "phone-b", "安卓1", "session-c", "sha-c");

            using var server = new WebServer(
                database,
                port,
                requireAccessKey: true,
                accessKey: accessKey,
                listenerHost: "127.0.0.1",
                mobileBackupComputerId: Guid.NewGuid().ToString("D"),
                mobileBackupStateDirectory: Path.Combine(directory, "uploads"),
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"),
                nodeId: Guid.NewGuid().ToString("D"),
                nodeName: "来源筛选测试主机",
                deploymentPreset: DeploymentPresets.MobileBackupHost);
            server.Start();

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using JsonDocument sources = JsonDocument.Parse(await client.GetStringAsync(
                $"/api/video-sources?key={accessKey}",
                TestContext.Current.CancellationToken));
            JsonElement merged = Assert.Single(sources.RootElement.GetProperty("data").EnumerateArray());
            Assert.Equal("安卓1", merged.GetProperty("name").GetString());
            Assert.Equal(
                new[] { "phone-a", "phone-b" },
                merged.GetProperty("deviceIds").EnumerateArray().Select(id => id.GetString()).ToArray());

            using JsonDocument videos = JsonDocument.Parse(await client.GetStringAsync(
                $"/api/videos?key={accessKey}&sourceType=external&deviceIds=phone-a,phone-b&size=20",
                TestContext.Current.CancellationToken));
            Assert.Equal(3, videos.RootElement.GetProperty("total").GetInt32());
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }
}
