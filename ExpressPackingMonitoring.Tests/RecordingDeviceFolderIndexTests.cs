using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 录像目录按完整设备号命名，昵称可以随时改，所以昵称不写进文件夹名。
/// 用户在录像文件夹里靠"设备对照表.txt"把目录对上设备昵称。
/// </summary>
public sealed class RecordingDeviceFolderIndexTests
{
    private static MobileOrderReceiverInfo Device(string nodeId, string nodeName, DateTime lastSeenUtc) =>
        new(nodeId, nodeName, "192.168.31.20", 5280, [], Online: true, LastSeenUtc: lastSeenUtc);

    [Fact]
    public void BuildContent_ListsFolderNameNicknameAndLastSeenTime()
    {
        DateTime lastSeen = new DateTime(2026, 9, 15, 2, 0, 0, DateTimeKind.Utc);
        string content = RecordingDeviceFolderIndex.BuildContent(
            [
                Device("device-123456", "安卓1", lastSeen),
                Device("device-abcdef", "打包台A", new DateTime(2026, 9, 12, 10, 3, 0, DateTimeKind.Utc))
            ],
            new DateTime(2026, 9, 15, 10, 0, 0));

        Assert.Contains("设备对照表", content);
        Assert.Contains("device-123456", content);
        Assert.Contains("安卓1", content);
        Assert.Contains("device-abcdef", content);
        Assert.Contains("打包台A", content);
        Assert.Contains("最后在线", content);
        Assert.Contains("目录\t昵称\t设备号\t最后在线（本机时间）", content);
        // 只写真实时间，不再判"在线/离线"。
        Assert.Contains(lastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), content);
        Assert.DoesNotContain("\t在线", content);
        Assert.DoesNotContain("离线", content);
    }

    /// <summary>设备从没上报过心跳时，写"从未记录"，不能拿当前时间冒充最后在线。</summary>
    [Fact]
    public void BuildContent_WithoutLastSeenTime_SaysNeverRecorded()
    {
        string content = RecordingDeviceFolderIndex.BuildContent(
            [new MobileOrderReceiverInfo("device-123456", "安卓1", "192.168.31.20", 5280, [], Online: false)],
            new DateTime(2026, 9, 15, 10, 0, 0));

        Assert.Contains("从未记录", content);
    }

    [Fact]
    public void BuildContent_WithoutDevices_SaysSoInsteadOfFailing()
    {
        string content = RecordingDeviceFolderIndex.BuildContent([], new DateTime(2026, 9, 15, 10, 0, 0));

        Assert.Contains("还没有设备", content);
    }

    [Fact]
    public void TryWrite_WritesIndexFileIntoRecordingRoot()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"epm-device-index-{Guid.NewGuid():N}");
        try
        {
            Assert.True(RecordingDeviceFolderIndex.TryWrite(
                directory,
                [Device("device-123456", "安卓1", new DateTime(2026, 9, 15, 2, 0, 0, DateTimeKind.Utc))],
                new DateTime(2026, 9, 15, 10, 0, 0)));

            string path = Path.Combine(directory, RecordingDeviceFolderIndex.FileName);
            Assert.True(File.Exists(path));
            Assert.Contains("device-123456", File.ReadAllText(path));
            Assert.Contains("安卓1", File.ReadAllText(path));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    /// <summary>录像根目录没配置时不能因为这份辅助文件报错，更不能影响录像本身。</summary>
    [Fact]
    public void TryWrite_WithoutRecordingRoot_IsSkipped()
    {
        Assert.False(RecordingDeviceFolderIndex.TryWrite("", [], DateTime.Now));
        Assert.False(RecordingDeviceFolderIndex.TryWrite(null, [], DateTime.Now));
    }
}
