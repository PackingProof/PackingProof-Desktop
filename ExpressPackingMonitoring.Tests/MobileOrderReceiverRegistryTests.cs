using System.Net;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class MobileOrderReceiverRegistryTests
{
    [Fact]
    public void AutomaticMobileNamesUseStableIncrementingNicknames()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mobile-receivers-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "order-receivers.json");
        try
        {
            var registry = new MobileOrderReceiverRegistry(path);
            MobileOrderReceiverInfo? first = registry.Register(
                IPAddress.Parse("192.168.31.201"),
                "mobile-device-0001",
                "设备 ABCDEF");
            MobileOrderReceiverInfo? second = registry.Register(
                IPAddress.Parse("192.168.31.202"),
                "mobile-device-0002",
                "设备 123456");
            MobileOrderReceiverInfo? reconnected = registry.Register(
                IPAddress.Parse("192.168.31.203"),
                "mobile-device-0001",
                "设备 ABCDEF");

            Assert.Equal("从机1", first?.NodeName);
            Assert.Equal("从机2", second?.NodeName);
            Assert.Equal("从机1", reconnected?.NodeName);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void DifferentDevicesSharingAddressReceiveDifferentNicknames()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mobile-receivers-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "order-receivers.json");
        try
        {
            var registry = new MobileOrderReceiverRegistry(
                path,
                () => new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc));
            MobileOrderReceiverInfo? first = registry.Register(
                IPAddress.Parse("192.168.31.201"),
                "mobile-device-0001",
                "本机");
            MobileOrderReceiverInfo? second = registry.Register(
                IPAddress.Parse("192.168.31.201"),
                "mobile-device-0002",
                "本机");

            Assert.Equal("从机1", first?.NodeName);
            Assert.Equal("从机2", second?.NodeName);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void NewDevicesUseTwoCharacterPlatformPrefixes()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mobile-receivers-{Guid.NewGuid():N}");
        try
        {
            var registry = new MobileOrderReceiverRegistry(Path.Combine(directory, "order-receivers.json"));
            Assert.Equal("安卓1", registry.Register(IPAddress.Parse("192.168.31.201"), "android-device-0001", "本机", deviceKind: "mobile", platform: "android")?.NodeName);
            Assert.Equal("苹果1", registry.Register(IPAddress.Parse("192.168.31.202"), "ios-device-0001", "本机", deviceKind: "mobile", platform: "ios")?.NodeName);
            Assert.Equal("电脑1", registry.Register(IPAddress.Parse("192.168.31.203"), "pc-device-0001", "本机", deviceKind: "pc", platform: "windows")?.NodeName);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// 安卓、苹果、电脑各自独立编号，不能全部落到同一个前缀。
    /// 之前手机端从不发送平台，主机只能按"从机"兜底，多台手机就都成了"从机N"。
    /// </summary>
    [Fact]
    public void RegisterNumbersEachPlatformIndependently()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mobile-receivers-{Guid.NewGuid():N}");
        try
        {
            var registry = new MobileOrderReceiverRegistry(Path.Combine(directory, "order-receivers.json"));

            Assert.Equal(
                "安卓1",
                registry.Register(IPAddress.Parse("192.168.31.201"), "android-1", "本机", deviceKind: "mobile", platform: "android")?.NodeName);
            Assert.Equal(
                "安卓2",
                registry.Register(IPAddress.Parse("192.168.31.202"), "android-2", "本机", deviceKind: "mobile", platform: "android")?.NodeName);
            Assert.Equal(
                "苹果1",
                registry.Register(IPAddress.Parse("192.168.31.203"), "ios-1", "本机", deviceKind: "mobile", platform: "ios")?.NodeName);
            Assert.Equal(
                "电脑1",
                registry.Register(IPAddress.Parse("192.168.31.204"), "pc-1", "本机", deviceKind: "pc", platform: "windows")?.NodeName);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// 备份上传、能力查询这些路径不带平台信息，注册表要沿用首次识别到的类型与平台，
    /// 否则同一台手机会在不同路径下被改回"从机N"，界面看起来像被改过名。
    /// </summary>
    [Fact]
    public void RegisterRemembersPlatformWhenLaterRequestsOmitIt()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mobile-receivers-{Guid.NewGuid():N}");
        try
        {
            var registry = new MobileOrderReceiverRegistry(Path.Combine(directory, "order-receivers.json"));
            registry.Register(
                IPAddress.Parse("192.168.31.201"),
                "android-device-0001",
                "本机",
                deviceKind: "mobile",
                platform: "android");

            MobileOrderReceiverInfo? later = registry.Register(
                IPAddress.Parse("192.168.31.201"),
                "android-device-0001",
                "安卓1");

            Assert.Equal("安卓1", later?.NodeName);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>先按"从机"登记、之后才带上平台时，昵称要跟着平台改成"安卓N"。</summary>
    [Fact]
    public void RegisterUpgradesAutomaticNameOncePlatformBecomesKnown()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mobile-receivers-{Guid.NewGuid():N}");
        try
        {
            var registry = new MobileOrderReceiverRegistry(Path.Combine(directory, "order-receivers.json"));
            MobileOrderReceiverInfo? before = registry.Register(
                IPAddress.Parse("192.168.31.201"),
                "android-device-0001",
                "本机");
            MobileOrderReceiverInfo? after = registry.Register(
                IPAddress.Parse("192.168.31.201"),
                "android-device-0001",
                "从机1",
                deviceKind: "mobile",
                platform: "android");

            Assert.Equal("从机1", before?.NodeName);
            Assert.Equal("安卓1", after?.NodeName);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// 用户改过的昵称不能被下一次心跳改回去。手机每 15 秒都会把本地缓存的旧自动名报回来，
    /// 没有这条规则的话改名 15 秒后就自己变回"安卓N"。
    /// </summary>
    [Fact]
    public void CustomNameSurvivesAutomaticNameFromLaterHeartbeats()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mobile-receivers-{Guid.NewGuid():N}");
        try
        {
            var registry = new MobileOrderReceiverRegistry(Path.Combine(directory, "order-receivers.json"));
            registry.Register(
                IPAddress.Parse("192.168.31.201"),
                "android-device-0001",
                "本机",
                deviceKind: "mobile",
                platform: "android");

            Assert.True(registry.TrySetCustomName("android-device-0001", "打包台A", out string error), error);
            // 手机随后照旧上报它缓存里的自动名。
            MobileOrderReceiverInfo? afterHeartbeat = registry.Register(
                IPAddress.Parse("192.168.31.201"),
                "android-device-0001",
                "安卓1",
                deviceKind: "mobile",
                platform: "android");
            // 原生备份上传路径连名字都不带。
            MobileOrderReceiverInfo? afterUpload = registry.Register(
                IPAddress.Parse("192.168.31.201"),
                "android-device-0001",
                "");

            Assert.Equal("打包台A", afterHeartbeat?.NodeName);
            Assert.Equal("打包台A", afterUpload?.NodeName);
            Assert.True(afterUpload?.Customized);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// 主机不允许两台设备同名：改名撞名时直接拒绝，让用户换名字，
    /// 而不是悄悄改成"名字 2"（那样用户以为改成功了，实际名字并不是他要的）。
    /// </summary>
    [Fact]
    public void CustomNameIsRejectedWhenAnotherDeviceAlreadyUsesIt()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mobile-receivers-{Guid.NewGuid():N}");
        try
        {
            var registry = new MobileOrderReceiverRegistry(Path.Combine(directory, "order-receivers.json"));
            registry.Register(IPAddress.Parse("192.168.31.201"), "device-1", "本机");
            registry.Register(IPAddress.Parse("192.168.31.202"), "device-2", "本机");

            Assert.True(registry.TrySetCustomName("device-1", "打包台A", out _));
            Assert.False(registry.TrySetCustomName("device-2", "打包台A", out string error));
            Assert.Contains("打包台A", error);

            IReadOnlyList<MobileOrderReceiverInfo> devices = registry.GetKnownRecordingDevices();
            Assert.Equal("打包台A", devices.Single(item => item.NodeId == "device-1").NodeName);
            Assert.NotEqual("打包台A", devices.Single(item => item.NodeId == "device-2").NodeName);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>设备自己报来的名字如果和别人撞名，退回按平台自动编号，保证不会出现同名设备。</summary>
    [Fact]
    public void ClientReportedDuplicateNameIsReplacedByAutomaticName()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mobile-receivers-{Guid.NewGuid():N}");
        try
        {
            var registry = new MobileOrderReceiverRegistry(Path.Combine(directory, "order-receivers.json"));
            registry.Register(
                IPAddress.Parse("192.168.31.201"),
                "device-1",
                "打包台A",
                deviceKind: "mobile",
                platform: "android");

            MobileOrderReceiverInfo? second = registry.Register(
                IPAddress.Parse("192.168.31.202"),
                "device-2",
                "打包台A",
                deviceKind: "mobile",
                platform: "android");

            Assert.NotEqual("打包台A", second?.NodeName);
            Assert.StartsWith("安卓", second?.NodeName ?? "", StringComparison.Ordinal);
            Assert.Equal(
                2,
                registry.GetKnownRecordingDevices()
                    .Select(item => item.NodeName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// 老版本曾经给多台设备发过同一个名字，那些设备只要上线就要被分开命名，
    /// 否则筛选下拉里会出现同名设备。
    /// </summary>
    [Fact]
    public void LegacyDuplicateNamesAreSeparatedWhenDeviceComesOnline()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mobile-receivers-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "order-receivers.json");
        try
        {
            Directory.CreateDirectory(directory);
            // 模拟历史数据：两台设备都叫"从机1"。
            File.WriteAllText(path, """
                [
                  {"Address":"192.168.31.201","LastSeenUtc":"2026-08-23T00:00:00Z","NodeId":"device-1","NodeName":"从机1","DeviceKind":"mobile","Platform":"android","Port":5280,"Capabilities":["recording","order-receiver"]},
                  {"Address":"192.168.31.202","LastSeenUtc":"2026-08-23T00:00:00Z","NodeId":"device-2","NodeName":"从机1","DeviceKind":"mobile","Platform":"android","Port":5280,"Capabilities":["recording","order-receiver"]}
                ]
                """);

            var registry = new MobileOrderReceiverRegistry(path, () => new DateTime(2026, 8, 23, 0, 0, 1, DateTimeKind.Utc));
            registry.Register(IPAddress.Parse("192.168.31.201"), "device-1", "本机", deviceKind: "mobile", platform: "android");

            IReadOnlyList<string> names = registry.GetKnownRecordingDevices()
                .Select(item => item.NodeName)
                .ToArray();
            Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>用户自定义昵称不随活跃期清理丢掉，否则 30 天后筛选又退回老快照名。</summary>
    [Fact]
    public void CustomNameIsKeptAfterRetentionWindow()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mobile-receivers-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "order-receivers.json");
        DateTime now = new(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);
        try
        {
            var registry = new MobileOrderReceiverRegistry(path, () => now);
            registry.Register(IPAddress.Parse("192.168.31.201"), "device-1", "本机");
            Assert.True(registry.TrySetCustomName("device-1", "打包台A", out _));

            now = now.AddDays(31);
            MobileOrderReceiverInfo device = Assert.Single(registry.GetKnownRecordingDevices());
            Assert.Equal("打包台A", device.NodeName);
            // 昵称要校验，空名或超长名一律拒绝，避免整台设备因为空名从设备目录里消失。
            Assert.False(registry.TrySetCustomName("device-1", "", out string emptyError));
            Assert.False(registry.TrySetCustomName("device-1", new string('长', 21), out string longError));
            Assert.NotEmpty(emptyError);
            Assert.NotEmpty(longError);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RegisterPersistsPrivateMobileAddressesAndRejectsPublicAddresses()
    {
        string directory = Path.Combine(Path.GetTempPath(), "packingproof-order-receivers-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "receivers.json");
        try
        {
            var registry = new MobileOrderReceiverRegistry(path);
            registry.Register(IPAddress.Parse("192.168.31.205"));
            registry.Register(IPAddress.Parse("8.8.8.8"));
            registry.Register(IPAddress.Loopback);

            Assert.Equal(new[] { "192.168.31.205:5280" }, registry.GetAuthorities());
            Assert.Equal(new[] { "192.168.31.205:5280" }, new MobileOrderReceiverRegistry(path).GetAuthorities());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RegisterKeepsAllAddressesWithinRetentionWindow()
    {
        string directory = Path.Combine(Path.GetTempPath(), "packingproof-order-receivers-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "receivers.json");
        try
        {
            var registry = new MobileOrderReceiverRegistry(path);
            for (int index = 1; index <= 8; index++)
                registry.Register(IPAddress.Parse($"192.168.31.{index}"));

            IReadOnlyList<string> addresses = registry.GetAuthorities();
            Assert.Equal(8, addresses.Count);
            Assert.Equal("192.168.31.8:5280", addresses[0]);
            Assert.Contains("192.168.31.1:5280", addresses);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void DeviceTurnsOfflineAfterThreeMissedHeartbeatsAndExpiresAfterThirtyDays()
    {
        string directory = Path.Combine(Path.GetTempPath(), "packingproof-order-receivers-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "receivers.json");
        DateTime now = new(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);
        try
        {
            var registry = new MobileOrderReceiverRegistry(path, () => now);
            registry.Register(IPAddress.Parse("192.168.31.205"), "mobile-device-0001", "设备 ABCDEF");
            Assert.True(Assert.Single(registry.GetKnownRecordingDevices()).Online);

            now = now.AddSeconds(46);
            Assert.False(Assert.Single(registry.GetKnownRecordingDevices()).Online);

            now = now.AddDays(31);
            Assert.Empty(registry.GetKnownRecordingDevices());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// 昵称台账要比设备保留期活得久：设备掉出保留期后，老记录仍要按同一个名字显示，
    /// 不能退回记录里的历史快照。
    /// </summary>
    [Fact]
    public void RememberedNamesOutliveRetentionPruningAndReload()
    {
        string directory = Path.Combine(Path.GetTempPath(), "packingproof-order-receivers-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "receivers.json");
        DateTime now = new(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);
        try
        {
            var registry = new MobileOrderReceiverRegistry(path, () => now);
            registry.Register(
                IPAddress.Parse("192.168.31.201"),
                "android-device-0001",
                "设备 A1B2C3",
                deviceKind: "mobile",
                platform: "android");

            now = now.AddDays(40);
            registry.Register(
                IPAddress.Parse("192.168.31.202"),
                "android-device-0002",
                "设备 D4E5F6",
                deviceKind: "mobile",
                platform: "android");

            Assert.DoesNotContain(
                registry.GetKnownRecordingDevices(),
                item => item.NodeId == "android-device-0001");
            Assert.Contains(
                registry.GetRememberedNames(),
                item => item.NodeId == "android-device-0001" && item.Name == "安卓1");

            // 重启后台账仍在：设备表里已经没有这台设备，名字却还认得。
            var restarted = new MobileOrderReceiverRegistry(path, () => now);
            Assert.Contains(
                restarted.GetRememberedNames(),
                item => item.NodeId == "android-device-0001" && item.Name == "安卓1");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RememberedNamesFollowUserRename()
    {
        string directory = Path.Combine(Path.GetTempPath(), "packingproof-order-receivers-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new MobileOrderReceiverRegistry(Path.Combine(directory, "receivers.json"));
            registry.Register(
                IPAddress.Parse("192.168.31.201"),
                "android-device-0001",
                "设备 A1B2C3",
                deviceKind: "mobile",
                platform: "android");

            Assert.True(registry.TrySetCustomName("android-device-0001", "东侧打包手机", out _));

            RememberedDeviceName remembered = Assert.Single(
                registry.GetRememberedNames(),
                item => item.NodeId == "android-device-0001");
            Assert.Equal("东侧打包手机", remembered.Name);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// 电脑昵称表已经算好的名字，登记表要原样收下：两边各按"电脑N"编号时，
    /// 主机占着电脑1，同一台电脑工位会在两张表里分别叫电脑2和电脑1。
    /// </summary>
    [Fact]
    public void TrustedNameIsKeptInsteadOfRenumbered()
    {
        string directory = Path.Combine(Path.GetTempPath(), "packingproof-order-receivers-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new MobileOrderReceiverRegistry(Path.Combine(directory, "receivers.json"));
            MobileOrderReceiverInfo? trusted = registry.Register(
                IPAddress.Parse("192.168.31.203"),
                "pc-device-0001",
                "电脑2",
                deviceKind: "pc",
                platform: "windows",
                trustProvidedName: true);

            Assert.Equal("电脑2", trusted?.NodeName);

            // 不信任的自动名仍然按"电脑N"重新编号，老行为不变。
            MobileOrderReceiverInfo? renumbered = registry.Register(
                IPAddress.Parse("192.168.31.204"),
                "pc-device-0002",
                "电脑9",
                deviceKind: "pc",
                platform: "windows");

            Assert.Equal("电脑3", renumbered?.NodeName);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
