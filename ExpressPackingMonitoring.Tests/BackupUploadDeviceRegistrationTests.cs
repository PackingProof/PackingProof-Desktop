using System.Net;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 备份上传完成时该往记录里写哪个来源名。
///
/// 客户端自报的名字可能是它本地的旧值，主机分配的昵称才是这台设备当前唯一的名字；
/// 电脑工位更不能让手机登记表另起一套"电脑N"编号，否则同一台电脑会出现两个昵称。
/// </summary>
public sealed class BackupUploadDeviceRegistrationTests
{
    [Fact]
    public void ComputerUploadKeepsComputerRegistryNameInsteadOfMintingAnotherOne()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            var computerRegistry = new RecordingComputerNicknameRegistry(
                Path.Combine(directory, "computer-nicknames.json"));
            computerRegistry.RegisterHost("host-node-0001", "电脑1", customized: false);
            var mobileRegistry = new MobileOrderReceiverRegistry(
                Path.Combine(directory, "order-receivers.json"));

            // 电脑工位还没应用主机分配结果，自报的仍是它本地的"电脑1"。
            string sourceName = BackupUploadDeviceRegistration.RegisterAndResolveSourceName(
                mobileRegistry,
                computerRegistry,
                IPAddress.Parse("192.168.31.51"),
                "pc-workstation-0001",
                "电脑1",
                deviceKind: "pc",
                platform: "windows");

            Assert.Equal("电脑2", sourceName);
            // 登记表里也必须是同一个名字，否则设备对照表、筛选下拉会和设备目录对不上。
            MobileOrderReceiverInfo registered = Assert.Single(
                mobileRegistry.GetKnownRecordingDevices(),
                item => item.NodeId == "pc-workstation-0001");
            Assert.Equal("电脑2", registered.NodeName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 上传成功的设备要标记成"有录像"：昵称映射是录像显示名的唯一来源，
    /// 这台设备即使长期不再上线，也不能按活跃时间把它连同昵称一起清掉。
    /// </summary>
    [Fact]
    public void UploadedDeviceSurvivesRetentionPruning()
    {
        string directory = CreateTemporaryDirectory();
        DateTime now = new(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);
        try
        {
            var computerRegistry = new RecordingComputerNicknameRegistry(
                Path.Combine(directory, "computer-nicknames.json"));
            var mobileRegistry = new MobileOrderReceiverRegistry(
                Path.Combine(directory, "order-receivers.json"),
                () => now);

            BackupUploadDeviceRegistration.RegisterAndResolveSourceName(
                mobileRegistry,
                computerRegistry,
                IPAddress.Parse("192.168.31.61"),
                "android-device-0001",
                "设备 A1B2C3",
                deviceKind: "mobile",
                platform: "android");

            now = now.AddDays(120);
            Assert.Equal(
                "安卓1",
                Assert.Single(
                    mobileRegistry.GetKnownRecordingDevices(),
                    item => item.NodeId == "android-device-0001").NodeName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MobileUploadUsesAssignedNicknameInsteadOfClientReportedName()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            var computerRegistry = new RecordingComputerNicknameRegistry(
                Path.Combine(directory, "computer-nicknames.json"));
            var mobileRegistry = new MobileOrderReceiverRegistry(
                Path.Combine(directory, "order-receivers.json"));

            // 手机本地缓存的名字还是改名前的"从机1"。
            string sourceName = BackupUploadDeviceRegistration.RegisterAndResolveSourceName(
                mobileRegistry,
                computerRegistry,
                IPAddress.Parse("192.168.31.61"),
                "android-device-0001",
                "从机1",
                deviceKind: "mobile",
                platform: "android");

            Assert.Equal("安卓1", sourceName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>主机完全不认识这台设备时（地址不是局域网私有地址，登记被拒）才退回自报名字。</summary>
    [Fact]
    public void UnknownDeviceFallsBackToClientReportedName()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            var computerRegistry = new RecordingComputerNicknameRegistry(
                Path.Combine(directory, "computer-nicknames.json"));
            var mobileRegistry = new MobileOrderReceiverRegistry(
                Path.Combine(directory, "order-receivers.json"));

            string sourceName = BackupUploadDeviceRegistration.RegisterAndResolveSourceName(
                mobileRegistry,
                computerRegistry,
                IPAddress.Loopback,
                "android-device-0002",
                "外部设备",
                deviceKind: "mobile",
                platform: "android");

            Assert.Equal("外部设备", sourceName);
            Assert.Empty(mobileRegistry.GetKnownRecordingDevices());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ComputerUploadDoesNotOverwriteUserCustomizedName()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            var computerRegistry = new RecordingComputerNicknameRegistry(
                Path.Combine(directory, "computer-nicknames.json"));
            computerRegistry.Assign("pc-workstation-0001", "东侧打包电脑", customized: true);
            var mobileRegistry = new MobileOrderReceiverRegistry(
                Path.Combine(directory, "order-receivers.json"));

            string sourceName = BackupUploadDeviceRegistration.RegisterAndResolveSourceName(
                mobileRegistry,
                computerRegistry,
                IPAddress.Parse("192.168.31.51"),
                "pc-workstation-0001",
                "电脑1",
                deviceKind: "pc",
                platform: "windows");

            Assert.Equal("东侧打包电脑", sourceName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"epm-upload-name-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
