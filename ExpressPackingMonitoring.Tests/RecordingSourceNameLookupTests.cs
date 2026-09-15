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

    [Fact]
    public void Build_FallsBackToRememberedNameAfterDeviceLeftRetention()
    {
        IReadOnlyDictionary<string, string> names = RecordingSourceNameLookup.Build(
            [],
            [],
            [new RememberedDeviceName("android-device-0001", "安卓1")]);

        // 设备掉出保留期后，老记录里的"从机1"不能又冒出来。
        Assert.Equal("安卓1", RecordingSourceNameLookup.Resolve(names, "android-device-0001", "从机1"));
    }

    [Fact]
    public void Build_PrefersLiveNamesOverRememberedOnes()
    {
        IReadOnlyDictionary<string, string> names = RecordingSourceNameLookup.Build(
            [Mobile("node-1", "安卓9")],
            [Client("node-2", "电脑工位 · 电脑2")],
            [
                new RememberedDeviceName("node-1", "安卓1"),
                new RememberedDeviceName("node-2", "从机2"),
                new RememberedDeviceName("node-3", "从机3")
            ]);

        Assert.Equal("安卓9", names["node-1"]);
        Assert.Equal("电脑工位 · 电脑2", names["node-2"]);
        Assert.Equal("从机3", names["node-3"]);
    }

    /// <summary>
    /// 设备掉出保留期后编号会被新设备复用，台账里的老名字不能再盖回去，
    /// 否则一台设备的名字会同时属于两台设备。
    /// </summary>
    [Fact]
    public void Build_KeepsOneNamePerDeviceWhenRememberedNameWasReused()
    {
        IReadOnlyDictionary<string, string> names = RecordingSourceNameLookup.Build(
            [Mobile("android-device-0002", "安卓1")],
            [],
            [
                new RememberedDeviceName("android-device-0001", "安卓1"),
                new RememberedDeviceName("android-device-0003", "从机3")
            ]);

        Assert.Equal("安卓1", names["android-device-0002"]);
        Assert.False(names.ContainsKey("android-device-0001"));
        Assert.Equal("从机3", names["android-device-0003"]);
        Assert.Equal(names.Count, names.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
