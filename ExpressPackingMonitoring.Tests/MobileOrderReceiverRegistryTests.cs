using System.Net;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class MobileOrderReceiverRegistryTests
{
    /// <summary>
    /// 实质变化（新设备、改名）必须立刻落盘：这份表是录像来源名的唯一来源，
    /// 丢一次就意味着老录像只剩设备号。
    /// </summary>
    [Fact]
    public void PersistsMeaningfulChangesImmediately()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mobile-receivers-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "order-receivers.json");
        try
        {
            DateTime now = new(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);
            var registry = new MobileOrderReceiverRegistry(path, () => now);

            registry.Register(IPAddress.Parse("192.168.31.201"), "device-a");
            Assert.True(File.Exists(path), "新设备必须立刻落盘");

            Assert.True(registry.TrySetCustomName("device-a", "打包台A", out string error), error);
            // 按语义断言而不是比字符串：登记表里的中文是 \uXXXX 转义存的。
            Assert.Contains(
                "打包台A",
                new MobileOrderReceiverRegistry(path, () => now)
                    .GetKnownRecordingDevices()
                    .Select(device => device.NodeName));

            // 新设备加入也算实质变化，哪怕上一次刚写过。
            now = now.AddSeconds(5);
            registry.Register(IPAddress.Parse("192.168.31.202"), "device-b");
            Assert.Contains("device-b", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// 只有活跃时间变化的心跳不该每次都全量重写整份表：手机每 15 秒一次，
    /// 512 台设备时就是持续的无谓写盘。改动内容不变时按时间节流。
    /// </summary>
    [Fact]
    public void ThrottlesWritesForHeartbeatsThatOnlyRefreshLastSeen()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mobile-receivers-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "order-receivers.json");
        try
        {
            DateTime now = new(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);
            var registry = new MobileOrderReceiverRegistry(path, () => now);
            registry.Register(IPAddress.Parse("192.168.31.201"), "device-a", "安卓1");
            // 用文件内容判断有没有落盘：文件系统时间戳精度在 CI 上会骗人（两次写入同一时刻），
            // 内容里的 LastSeenUtc 是随注入时钟变化的，能稳定反映"这一轮到底写没写"。
            string writtenContent = File.ReadAllText(path);

            // 连续几次"什么都没变"的心跳。
            for (int index = 0; index < 4; index++)
            {
                now = now.AddSeconds(15);
                registry.Register(IPAddress.Parse("192.168.31.201"), "device-a", "安卓1");
            }

            Assert.Equal(writtenContent, File.ReadAllText(path));

            // 过了节流窗口后要落一次，活跃时间不能长期只存在内存里。
            now = now.AddMinutes(3);
            registry.Register(IPAddress.Parse("192.168.31.201"), "device-a", "安卓1");
            Assert.NotEqual(writtenContent, File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>地址变化要立刻落盘：订单推送要按地址找设备，存慢了就推不到。</summary>
    [Fact]
    public void PersistsAddressChangeImmediately()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mobile-receivers-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "order-receivers.json");
        try
        {
            DateTime now = new(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);
            var registry = new MobileOrderReceiverRegistry(path, () => now);
            registry.Register(IPAddress.Parse("192.168.31.201"), "device-a", "安卓1");

            now = now.AddSeconds(15);
            registry.Register(IPAddress.Parse("192.168.31.209"), "device-a", "安卓1");

            Assert.Contains("192.168.31.209", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

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
            registry.Register(IPAddress.Parse("192.168.31.205"), "device-1");
            registry.Register(IPAddress.Parse("8.8.8.8"), "device-2");
            registry.Register(IPAddress.Loopback, "device-3");

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
                registry.Register(IPAddress.Parse($"192.168.31.{index}"), $"device-{index}");

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
    /// 只要这台设备在主机上留下过录像，就不能按活跃时间清理：昵称映射是录像显示名的唯一
    /// 来源，清掉它老录像就只剩设备号了。
    /// </summary>
    [Fact]
    public void DevicesWithRecordingsSurviveRetentionPruning()
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
                platform: "android",
                hasRecordings: true);
            registry.Register(
                IPAddress.Parse("192.168.31.202"),
                "android-device-0002",
                "设备 D4E5F6",
                deviceKind: "mobile",
                platform: "android");

            now = now.AddDays(120);
            registry.Register(
                IPAddress.Parse("192.168.31.203"),
                "android-device-0003",
                "设备 G7H8I9",
                deviceKind: "mobile",
                platform: "android");

            IReadOnlyList<MobileOrderReceiverInfo> known = registry.GetKnownRecordingDevices();
            Assert.Contains(
                known,
                item => item.NodeId == "android-device-0001" && item.NodeName == "安卓1");
            Assert.DoesNotContain(known, item => item.NodeId == "android-device-0002");

            // 重启后依然保留（设备表是持久化的映射）。
            var restarted = new MobileOrderReceiverRegistry(path, () => now);
            Assert.Contains(
                restarted.GetKnownRecordingDevices(),
                item => item.NodeId == "android-device-0001" && item.NodeName == "安卓1");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// 升级前的昵称只留在记录快照里，启动时按库里的录像来源补齐映射：
    /// 只补没登记过的设备，名字取它最近一条记录里的名字。
    /// </summary>
    [Fact]
    public void SeedRecordedDevicesRestoresMappingFromExistingRecords()
    {
        string directory = Path.Combine(Path.GetTempPath(), "packingproof-order-receivers-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "receivers.json");
        DateTime lastRecord = new(2026, 7, 1, 3, 0, 0, DateTimeKind.Utc);
        try
        {
            var registry = new MobileOrderReceiverRegistry(path);
            registry.SeedRecordedDevices(
            [
                new ExpressPackingMonitoring.Data.VideoSourceInfo("external", "phone-1", "手机5", 3, lastRecord),
                new ExpressPackingMonitoring.Data.VideoSourceInfo("pc", "host-1", "DESKTOP-ABC", 9, lastRecord),
                new ExpressPackingMonitoring.Data.VideoSourceInfo("external", "", "无设备号的老记录", 1, lastRecord)
            ]);

            MobileOrderReceiverInfo seeded = Assert.Single(
                registry.GetKnownRecordingDevices(),
                item => item.NodeId == "phone-1");
            Assert.Equal("手机5", seeded.NodeName);
            Assert.Equal(lastRecord, seeded.LastSeenUtc);
            // 本机来源（pc）和无设备号的老记录不该变成"设备"。
            Assert.Single(registry.GetKnownRecordingDevices());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void SeedRecordedDevicesKeepsOneNamePerDevice()
    {
        string directory = Path.Combine(Path.GetTempPath(), "packingproof-order-receivers-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new MobileOrderReceiverRegistry(Path.Combine(directory, "receivers.json"));
            registry.SeedRecordedDevices(
            [
                // 老版本给两台设备发过同一个名字：最近还有录像的那台保留原名，
                // 另一台带上设备号后缀区分开（它下次上线会换成主机新分配的名字）。
                new ExpressPackingMonitoring.Data.VideoSourceInfo(
                    "external", "phone-new", "手机1", 2, new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc)),
                new ExpressPackingMonitoring.Data.VideoSourceInfo(
                    "external", "phone-old", "手机1", 5, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)),
                new ExpressPackingMonitoring.Data.VideoSourceInfo(
                    "external", "phone-other", "手机2", 1, new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc))
            ]);

            IReadOnlyList<MobileOrderReceiverInfo> known = registry.GetKnownRecordingDevices();
            Assert.Equal("手机1", Assert.Single(known, item => item.NodeId == "phone-new").NodeName);
            Assert.Equal("手机2", Assert.Single(known, item => item.NodeId == "phone-other").NodeName);

            // 撞名的那台带上设备号后缀：一台设备一个名字，名字之间也不重复。
            string distinct = Assert.Single(known, item => item.NodeId == "phone-old").NodeName;
            Assert.StartsWith("手机1·", distinct);
            Assert.Contains("NEOLD", distinct);
            Assert.Equal(
                known.Count,
                known.Select(item => item.NodeName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void SeedRecordedDevicesDoesNotOverrideAssignedNames()
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

            registry.SeedRecordedDevices(
            [
                new ExpressPackingMonitoring.Data.VideoSourceInfo(
                    "external", "android-device-0001", "从机9", 2, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc))
            ]);

            Assert.Equal(
                "安卓1",
                Assert.Single(registry.GetKnownRecordingDevices(), item => item.NodeId == "android-device-0001").NodeName);
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

    /// <summary>
    /// 没有设备号的匿名上报（未配对的手机探测主机能力等）不能凭空造一台设备：
    /// 之前会按来源地址生成一个假设备号，筛选与设备列表里就多出一台并不存在的"从机"。
    /// </summary>
    [Fact]
    public void AnonymousRegistrationDoesNotInventADevice()
    {
        string directory = Path.Combine(Path.GetTempPath(), "packingproof-order-receivers-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new MobileOrderReceiverRegistry(Path.Combine(directory, "receivers.json"));

            Assert.Null(registry.Register(IPAddress.Parse("192.168.31.205"), "", "设备 A1B2C3"));
            Assert.Null(registry.Register(IPAddress.Parse("192.168.31.206"), "   ", "设备 D4E5F6"));
            Assert.Empty(registry.GetKnownRecordingDevices());
            Assert.Empty(registry.GetAuthorities());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// 备份上传、能力查询这些路径可能没带平台信息（旧版 App 的原生签名）。这时不能把设备
    /// 从"手机1"改成"从机1"——用户会看到一台设备不断改名，列表里冒出一堆从机。
    /// 平台明确时才做前缀迁移（手机1 -> 安卓1）。
    /// </summary>
    [Fact]
    public void ReregisteringWithoutPlatformKeepsExistingPrefix()
    {
        string directory = Path.Combine(Path.GetTempPath(), "packingproof-order-receivers-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new MobileOrderReceiverRegistry(Path.Combine(directory, "receivers.json"));
            // 老版本按"手机N"命名的设备（昵称表里已经是这个名字）。
            registry.Register(
                IPAddress.Parse("192.168.31.201"),
                "android-device-0001",
                "手机1",
                deviceKind: "mobile",
                trustProvidedName: true);
            Assert.Equal(
                "手机1",
                Assert.Single(registry.GetKnownRecordingDevices(), item => item.NodeId == "android-device-0001").NodeName);

            // 平台未知的上报：沿用已有前缀，不再是"从机1"。
            Assert.Equal(
                "手机1",
                registry.Register(
                    IPAddress.Parse("192.168.31.201"),
                    "android-device-0001",
                    "设备 A1B2C3",
                    deviceKind: "mobile")?.NodeName);

            // 平台明确：按平台前缀迁移一次。
            Assert.Equal(
                "安卓1",
                registry.Register(
                    IPAddress.Parse("192.168.31.201"),
                    "android-device-0001",
                    "设备 A1B2C3",
                    deviceKind: "mobile",
                    platform: "android")?.NodeName);

            // 迁移完成后，旧版 App 的匿名上报不会再把它改回去。
            Assert.Equal(
                "安卓1",
                registry.Register(
                    IPAddress.Parse("192.168.31.201"),
                    "android-device-0001",
                    "设备 A1B2C3",
                    deviceKind: "mobile")?.NodeName);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// 旧版 App 上报没带平台信息时，主机把已经有名字的设备改成了"从机N"。
    /// 打开软件时按这台设备自己录像里用过的名字改回来，用户不用等设备上线。
    /// </summary>
    [Fact]
    public void SeedRecordedDevicesRepairsSlaveFallbackNames()
    {
        string directory = Path.Combine(Path.GetTempPath(), "packingproof-order-receivers-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new MobileOrderReceiverRegistry(Path.Combine(directory, "receivers.json"));
            // 先造出被兜底命名改坏的状态：这台设备已经是"从机1"。
            registry.Register(
                IPAddress.Parse("192.168.31.201"),
                "android-device-0001",
                "从机1",
                deviceKind: "mobile",
                trustProvidedName: true);

            registry.SeedRecordedDevices(
            [
                new ExpressPackingMonitoring.Data.VideoSourceInfo(
                    "external",
                    "android-device-0001",
                    "从机2",
                    12,
                    new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc),
                    "手机1")
            ]);

            Assert.Equal(
                "手机1",
                Assert.Single(registry.GetKnownRecordingDevices(), item => item.NodeId == "android-device-0001").NodeName);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// 两个设备号都叫过"手机1"（老版本不管重名）：修回兜底名时谁最近还在用它谁留着，
    /// 另一台退回"名字·设备号"，不会又冒出两台同名设备。
    /// </summary>
    [Fact]
    public void SeedRecordedDevicesGivesContestedNameToTheMostRecentDevice()
    {
        string directory = Path.Combine(Path.GetTempPath(), "packingproof-order-receivers-" + Guid.NewGuid().ToString("N"));
        DateTime now = new(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc);
        try
        {
            var registry = new MobileOrderReceiverRegistry(
                Path.Combine(directory, "receivers.json"),
                () => now);
            now = new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc);
            registry.Register(
                IPAddress.Parse("192.168.31.201"),
                "android-device-old",
                "手机1",
                deviceKind: "mobile",
                trustProvidedName: true);
            now = new DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc);
            registry.Register(
                IPAddress.Parse("192.168.31.202"),
                "android-device-new",
                "从机1",
                deviceKind: "mobile",
                trustProvidedName: true);

            registry.SeedRecordedDevices(
            [
                // 旧的这台最后一次录像在 8/11，新的那台在 8/19：名字归新的那台。
                new ExpressPackingMonitoring.Data.VideoSourceInfo(
                    "external",
                    "android-device-old",
                    "手机1",
                    4,
                    new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc),
                    "手机1"),
                new ExpressPackingMonitoring.Data.VideoSourceInfo(
                    "external",
                    "android-device-new",
                    "手机1",
                    9,
                    new DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc),
                    "手机1")
            ]);

            IReadOnlyList<MobileOrderReceiverInfo> known = registry.GetKnownRecordingDevices();
            Assert.Equal("手机1", Assert.Single(known, item => item.NodeId == "android-device-new").NodeName);
            Assert.StartsWith("手机1·", Assert.Single(known, item => item.NodeId == "android-device-old").NodeName);
            Assert.Equal(
                known.Count,
                known.Select(item => item.NodeName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>登记表里名字为空的设备（之前补齐没补上的）也要按录像里的名字补上。</summary>
    [Fact]
    public void SeedRecordedDevicesFillsEntriesWithoutName()
    {
        string directory = Path.Combine(Path.GetTempPath(), "packingproof-order-receivers-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new MobileOrderReceiverRegistry(Path.Combine(directory, "receivers.json"));
            registry.Register(
                IPAddress.Parse("192.168.31.201"),
                "android-device-0001",
                "",
                deviceKind: "mobile",
                trustProvidedName: true);
            Assert.Equal(
                "",
                Assert.Single(registry.GetKnownRecordingDevices(), item => item.NodeId == "android-device-0001").NodeName);

            registry.SeedRecordedDevices(
            [
                new ExpressPackingMonitoring.Data.VideoSourceInfo(
                    "external",
                    "android-device-0001",
                    "手机3",
                    18,
                    new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc),
                    "手机3")
            ]);

            Assert.Equal(
                "手机3",
                Assert.Single(registry.GetKnownRecordingDevices(), item => item.NodeId == "android-device-0001").NodeName);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>用户手改过的名字不能被补齐流程改掉，哪怕它看起来像兜底名。</summary>
    [Fact]
    public void SeedRecordedDevicesNeverTouchesCustomizedNames()
    {
        string directory = Path.Combine(Path.GetTempPath(), "packingproof-order-receivers-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new MobileOrderReceiverRegistry(Path.Combine(directory, "receivers.json"));
            registry.Register(
                IPAddress.Parse("192.168.31.201"),
                "android-device-0001",
                "手机1",
                deviceKind: "mobile",
                platform: "android");
            Assert.True(registry.TrySetCustomName("android-device-0001", "从机台A", out _));

            registry.SeedRecordedDevices(
            [
                new ExpressPackingMonitoring.Data.VideoSourceInfo(
                    "external",
                    "android-device-0001",
                    "手机1",
                    12,
                    new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc),
                    "手机1")
            ]);

            Assert.Equal(
                "从机台A",
                Assert.Single(registry.GetKnownRecordingDevices(), item => item.NodeId == "android-device-0001").NodeName);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
    /// <summary>
    /// 条数上限是硬上限：淘汰跑在插入之前，不给新设备预留位置的话插入后会多出一条。
    /// 有录像的设备最后才被淘汰。
    /// </summary>
    [Fact]
    public void NeverExceedsTheDeviceCountLimit()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mobile-receivers-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "order-receivers.json");
        try
        {
            DateTime now = new(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);
            var registry = new MobileOrderReceiverRegistry(path, () => now);
            for (int index = 0; index < 520; index++)
            {
                now = now.AddSeconds(1);
                registry.Register(IPAddress.Parse("192.168.31.201"), $"device-{index:D4}");
            }

            Assert.True(
                registry.GetKnownRecordingDevices().Count <= 512,
                $"登记表里有 {registry.GetKnownRecordingDevices().Count} 台设备，超过硬上限 512");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
