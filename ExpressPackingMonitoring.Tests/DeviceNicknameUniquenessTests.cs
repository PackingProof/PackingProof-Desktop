using System.Net;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 昵称的全局唯一性。
///
/// 昵称分存两张表：手机登记表（order-receivers.json）覆盖所有连过的设备，
/// 电脑昵称表（computer-nicknames.json）是电脑工位名字的权威来源。
/// 但只发心跳、没上传过录像的工位不在手机登记表里，所以两张表各查各的时候，
/// 一台手机和一台电脑可以叫同一个名字——用户眼里就是两台设备同名。
/// 这里守的就是"跨表也不许重名"。
/// </summary>
public sealed class DeviceNicknameUniquenessTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"nickname-uniqueness-{Guid.NewGuid():N}");

    /// <summary>电脑工位改名撞上手机的名字时要让开，不能两台设备同名。</summary>
    [Fact]
    public void ComputerRenameCannotTakeAPhoneName()
    {
        (MobileOrderReceiverRegistry phones, RecordingComputerNicknameRegistry computers) = CreatePair();
        phones.Register(IPAddress.Parse("192.168.31.201"), "phone-1");
        Assert.True(phones.TrySetCustomName("phone-1", "打包台A", out string error), error);

        string assigned = computers.Assign("pc-1", "打包台A", customized: true);

        Assert.NotEqual("打包台A", assigned);
        Assert.StartsWith("打包台A", assigned, StringComparison.Ordinal);
    }

    /// <summary>反过来也一样：手机改名撞上工位的名字时直接拒绝，由用户换一个。</summary>
    [Fact]
    public void PhoneRenameCannotTakeAComputerName()
    {
        (MobileOrderReceiverRegistry phones, RecordingComputerNicknameRegistry computers) = CreatePair();
        // 只发过心跳、没上传过录像的工位：它只在电脑昵称表里。
        computers.Assign("pc-1", "打包台A", customized: true);
        phones.Register(IPAddress.Parse("192.168.31.201"), "phone-1");

        bool renamed = phones.TrySetCustomName("phone-1", "打包台A", out string error);

        Assert.False(renamed, "手机不该能占用工位已经用了的名字");
        Assert.Contains("打包台A", error);
    }

    /// <summary>
    /// 自动编号也要避开对方表里的名字：用户把一台手机手工命名成"电脑3"之后，
    /// 新上线的工位不能再拿到"电脑3"。
    /// </summary>
    [Fact]
    public void AutomaticComputerNumberSkipsNamesUsedByPhones()
    {
        (MobileOrderReceiverRegistry phones, RecordingComputerNicknameRegistry computers) = CreatePair();
        phones.Register(IPAddress.Parse("192.168.31.201"), "phone-1");
        Assert.True(phones.TrySetCustomName("phone-1", "电脑1", out string error), error);

        string assigned = computers.Assign("pc-1", "", customized: false);

        Assert.NotEqual("电脑1", assigned);
    }

    /// <summary>同一台设备重复改成自己已有的名字不算撞名，否则改名会莫名失败。</summary>
    [Fact]
    public void RenamingToItsOwnNameIsNotACollision()
    {
        (MobileOrderReceiverRegistry phones, RecordingComputerNicknameRegistry computers) = CreatePair();
        computers.Assign("pc-1", "打包台A", customized: true);

        Assert.Equal("打包台A", computers.Assign("pc-1", "打包台A", customized: true));

        phones.Register(IPAddress.Parse("192.168.31.201"), "phone-1");
        Assert.True(phones.TrySetCustomName("phone-1", "安卓A", out _));
        Assert.True(phones.TrySetCustomName("phone-1", "安卓A", out string error), error);
    }

    /// <summary>
    /// 上传落库的那条路径（工位上传时把电脑昵称表的结果交给手机登记表）也不能撞名：
    /// 结果必须是同一个名字，否则两张表会给同一台工位存两个名字。
    /// </summary>
    [Fact]
    public void UploadRegistrationKeepsBothTablesAgreeingOnTheName()
    {
        (MobileOrderReceiverRegistry phones, RecordingComputerNicknameRegistry computers) = CreatePair();
        phones.Register(IPAddress.Parse("192.168.31.201"), "phone-1");
        Assert.True(phones.TrySetCustomName("phone-1", "打包台A", out string error), error);
        computers.Assign("pc-1", "打包台A", customized: true);

        string resolved = BackupUploadDeviceRegistration.RegisterAndResolveSourceName(
            phones,
            computers,
            IPAddress.Parse("192.168.31.202"),
            "pc-1",
            "打包台A",
            "pc",
            "windows");

        Assert.NotEqual("打包台A", resolved);
        Assert.Equal(
            resolved,
            phones.GetKnownRecordingDevices()
                .First(device => string.Equals(device.NodeId, "pc-1", StringComparison.OrdinalIgnoreCase))
                .NodeName);
    }

    /// <summary>
    /// 昵称表读不出来（文件损坏）时按空表继续，不能抛异常挡住启动：
    /// 昵称丢失只是显示名退化，而启动失败是整个软件不可用。
    /// </summary>
    [Fact]
    public void CorruptedNicknameFilesDoNotBlockStartup()
    {
        Directory.CreateDirectory(_root);
        string phonesPath = Path.Combine(_root, "order-receivers.json");
        string computersPath = Path.Combine(_root, "computer-nicknames.json");
        File.WriteAllText(phonesPath, "{ 这不是合法的 JSON");
        File.WriteAllText(computersPath, "[[[");

        var phones = new MobileOrderReceiverRegistry(phonesPath);
        var computers = new RecordingComputerNicknameRegistry(computersPath);

        Assert.Empty(phones.GetKnownRecordingDevices());
        Assert.Equal("电脑1", computers.Assign("pc-1", "", customized: false));
    }

    /// <summary>
    /// 崩溃留下的临时文件要在启动时清掉，不能在状态目录里越积越多。
    /// 只清这个确定由自己写出的命名形状。
    /// </summary>
    [Fact]
    public void CleansUpTemporaryFilesLeftByACrash()
    {
        Directory.CreateDirectory(_root);
        string phonesPath = Path.Combine(_root, "order-receivers.json");
        // 已退出进程留下的残留（进程号 1 在这里只是一个不会命中当前进程的值）。
        string abandoned = $"{phonesPath}.1.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(abandoned, "[]");
        // 用户自己的文件和别的状态文件都不该被碰。
        string unrelated = Path.Combine(_root, "computer-nicknames.json");
        File.WriteAllText(unrelated, "[]");

        _ = new MobileOrderReceiverRegistry(phonesPath);

        Assert.False(File.Exists(abandoned), "崩溃留下的临时文件应该被清掉");
        Assert.True(File.Exists(unrelated), "别的状态文件不能被碰");
    }

    private (MobileOrderReceiverRegistry Phones, RecordingComputerNicknameRegistry Computers) CreatePair()
    {
        Directory.CreateDirectory(_root);
        RecordingComputerNicknameRegistry? computers = null;
        var phones = new MobileOrderReceiverRegistry(
            Path.Combine(_root, "order-receivers.json"),
            externalNicknames: () => computers?.NicknameSnapshot
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        computers = new RecordingComputerNicknameRegistry(
            Path.Combine(_root, "computer-nicknames.json"),
            externalNicknames: () => phones.NicknameSnapshot);
        return (phones, computers);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch
        {
            // 临时目录清理失败不影响断言结果。
        }
    }
}
