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
}
