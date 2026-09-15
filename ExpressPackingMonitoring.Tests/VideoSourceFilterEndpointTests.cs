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
    /// 老库里两台设备被发过同一个名字时，补齐映射会给最近还有录像的那台保留原名，
    /// 另一台带上设备号后缀：一台设备一个名字，下拉里不会再出现两台设备合并成一条。
    /// </summary>
    [Fact]
    public async Task VideoSourcesEndpoint_SeparatesDevicesThatSharedAHistoricalName()
    {
        const string accessKey = "secret-web-access-key";
        string directory = Path.Combine(Path.GetTempPath(), $"epm-video-sources-distinct-{Guid.NewGuid():N}");
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
            JsonElement[] items = sources.RootElement.GetProperty("data").EnumerateArray().ToArray();
            Assert.Equal(2, items.Length);
            Assert.Equal(
                "安卓1",
                Assert.Single(items, item => item.GetProperty("deviceId").GetString() == "phone-a")
                    .GetProperty("name").GetString());
            JsonElement renamed = Assert.Single(
                items,
                item => item.GetProperty("deviceId").GetString() == "phone-b");
            Assert.StartsWith("安卓1·", renamed.GetProperty("name").GetString());

            // 各自筛各自的录像，不会互相串。
            using JsonDocument phoneB = JsonDocument.Parse(await client.GetStringAsync(
                $"/api/videos?key={accessKey}&sourceType=external&deviceIds=phone-b&size=20",
                TestContext.Current.CancellationToken));
            Assert.Equal(1, phoneB.RootElement.GetProperty("total").GetInt32());

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

    /// <summary>
    /// 记录不再逐条写昵称，昵称只存在主机登记表里。启动时会用库里已有的录像来源把
    /// "设备号 -> 昵称"补齐：老库升级后，老录像显示的是这台设备最近用过的名字，
    /// 而不是每条记录各自的历史快照。
    /// </summary>
    [Fact]
    public void StartupSeedsDeviceNameMappingFromExistingRecords()
    {
        string stateDirectory = Path.Combine(
            Path.GetTempPath(),
            $"epm-video-sources-seed-{Guid.NewGuid():N}");
        string directory = Path.Combine(stateDirectory, "data");
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
                "phone-b", "手机1", "session-c", "sha-c");
            // 新版本落库的记录不再写昵称，显示名完全靠登记表。
            database.InsertMobileBackupRecord(
                "D", Path.Combine(directory, "d.mp4"), 1, DateTime.Now.AddMinutes(-5), 3,
                "phone-a", "", "session-d", "sha-d");

            using var server = new WebServer(
                database,
                port,
                listenerHost: "127.0.0.1",
                mobileBackupComputerId: Guid.NewGuid().ToString("D"),
                mobileBackupStateDirectory: stateDirectory,
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"),
                nodeId: Guid.NewGuid().ToString("D"),
                nodeName: "来源筛选测试主机",
                deploymentPreset: DeploymentPresets.MobileBackupHost);
            server.Start();

            IReadOnlyDictionary<string, string> names = server.GetCurrentSourceDeviceNames();
            Assert.Equal("安卓1", names["phone-a"]);
            Assert.Equal("手机1", names["phone-b"]);

            // 映射落在登记表文件里：换一个主机实例仍然认得这两台设备。
            using var restarted = new WebServer(
                database,
                TestPortAllocator.GetFreeTcpPort(),
                listenerHost: "127.0.0.1",
                mobileBackupComputerId: Guid.NewGuid().ToString("D"),
                mobileBackupStateDirectory: stateDirectory,
                mobileBackupRecordingRootResolver: () => Path.Combine(directory, "recordings"),
                nodeId: Guid.NewGuid().ToString("D"),
                nodeName: "来源筛选测试主机",
                deploymentPreset: DeploymentPresets.MobileBackupHost);
            restarted.Start();

            IReadOnlyDictionary<string, string> reloaded = restarted.GetCurrentSourceDeviceNames();
            Assert.Equal("安卓1", reloaded["phone-a"]);
            Assert.Equal("手机1", reloaded["phone-b"]);
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(directory);
            try { Directory.Delete(stateDirectory, recursive: true); } catch { }
        }
    }
}
