using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 录像记录里保存的是写入当时的来源名快照，设备改名后老记录仍带旧昵称。
/// 这里锁住"按设备号取当前昵称、取不到才退回快照名"的规则。
/// </summary>
public sealed class RecordingSourceNameLookupTests
{
    private static MobileOrderReceiverInfo Mobile(string nodeId, string nodeName) =>
        new(nodeId, nodeName, "192.168.31.20", 5280, [], Online: true);

    private static ConnectedClientInfo Client(string nodeId, string displayName) =>
        new(
            nodeId,
            "mobile-app",
            displayName,
            "192.168.31.21:5280",
            DateTimeOffset.UtcNow,
            nodeId,
            "mobile",
            5280,
            []);

    [Fact]
    public void Resolve_PrefersCurrentNicknameOverRecordedSnapshot()
    {
        IReadOnlyDictionary<string, string> names = RecordingSourceNameLookup.Build(
            [Mobile("android-device-0001", "安卓1")],
            []);

        // 记录里还是改名前的"从机1"，下拉里必须显示当前的"安卓1"。
        Assert.Equal("安卓1", RecordingSourceNameLookup.Resolve(names, "android-device-0001", "从机1"));
    }

    [Fact]
    public void Resolve_FallsBackToSnapshotForUnknownDevice()
    {
        IReadOnlyDictionary<string, string> names = RecordingSourceNameLookup.Build(
            [Mobile("android-device-0001", "安卓1")],
            []);

        Assert.Equal("从机9", RecordingSourceNameLookup.Resolve(names, "gone-device", "从机9"));
        Assert.Equal("从机9", RecordingSourceNameLookup.Resolve(names, "", "从机9"));
        Assert.Equal("", RecordingSourceNameLookup.Resolve(null, "android-device-0001", ""));
    }

    [Fact]
    public void Build_PrefersRegisteredNicknameOverClientDisplayName()
    {
        IReadOnlyDictionary<string, string> names = RecordingSourceNameLookup.Build(
            [Mobile("node-1", "安卓1")],
            [Client("node-1", "本机")]);

        Assert.Equal("安卓1", names["node-1"]);
    }

    [Fact]
    public void Build_IgnoresEmptyIdsAndNames()
    {
        IReadOnlyDictionary<string, string> names = RecordingSourceNameLookup.Build(
            [Mobile("", "安卓1"), Mobile("node-2", "  ")],
            [Client("node-3", "")]);

        Assert.Empty(names);
    }

    [Fact]
    public void Resolve_MatchesDeviceIdIgnoringCase()
    {
        IReadOnlyDictionary<string, string> names = RecordingSourceNameLookup.Build(
            [Mobile("ANDROID-Device-0001", "安卓1")],
            []);

        Assert.Equal("安卓1", RecordingSourceNameLookup.Resolve(names, "android-device-0001", "从机1"));
    }
}
