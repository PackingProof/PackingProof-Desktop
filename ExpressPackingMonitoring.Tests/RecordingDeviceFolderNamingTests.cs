using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 设备录像目录命名：目录名就是完整设备号。
///
/// 唯一不变量：**目录名是设备号的纯函数** —— 同一个设备号永远得到同一个目录名，
/// 不同的设备号永远得到不同的目录名。没有分配、没有映射表、没有状态，
/// 所以不存在"表丢了就重新编号、同一台设备的录像散进两个目录"这类风险。
/// 目录名难看由录像根目录下的快捷方式解决，不靠截短设备号。
/// </summary>
public sealed class RecordingDeviceFolderNamingTests
{
    /// <summary>GUID 设备号原样成为目录名：一个字符都不该被改。</summary>
    [Fact]
    public void UsesFullDeviceIdAsDirectoryName()
    {
        const string deviceId = "12345678-1234-1234-1234-1234569abcdef";

        Assert.Equal(deviceId, RecordingDeviceFolderNaming.BuildDirectoryName(deviceId));
    }

    /// <summary>纯函数：同一个设备号问多少次都是同一个名字。</summary>
    [Fact]
    public void IsStableForTheSameDeviceId()
    {
        const string deviceId = "8f14e45f-ea8d-4c2b-9f3a-1b2c3d4e5f60";

        Assert.Equal(
            RecordingDeviceFolderNaming.BuildDirectoryName(deviceId),
            RecordingDeviceFolderNaming.BuildDirectoryName(deviceId));
        Assert.Equal(
            RecordingDeviceFolderNaming.BuildDirectoryName(deviceId),
            RecordingDeviceFolderNaming.BuildDirectoryName($"  {deviceId}  "));
    }

    /// <summary>
    /// 不同设备号绝不撞成同一个目录。这是放弃"设备号后六位"的根本原因：
    /// 六位十六进制会撞，撞上就是两台设备的录像混进同一个目录。
    /// </summary>
    [Fact]
    public void DifferentDeviceIdsNeverShareADirectory()
    {
        string[] deviceIds =
        [
            // 只有前缀不同、后六位完全相同：老的"后六位"方案在这里必然撞号。
            "11111111-1111-1111-1111-111111abcdef",
            "22222222-2222-2222-2222-222222abcdef",
            "33333333-3333-3333-3333-333333abcdef",
            // 只有非法字符位置不同：替换后可能压成同一个名字，必须靠哈希后缀区分开。
            "device:001",
            "device*001",
            "device 001",
            // 超长设备号，前缀相同、尾部不同：截断后同样要能区分。
            new string('a', 80) + "-first",
            new string('a', 80) + "-second",
        ];

        string[] directories = deviceIds
            .Select(RecordingDeviceFolderNaming.BuildDirectoryName)
            .ToArray();

        Assert.Equal(deviceIds.Length, directories.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>目录名不能超长，否则深一点的 NAS 路径会逼近 Windows 的 260 上限。</summary>
    [Fact]
    public void LimitsDirectoryNameLength()
    {
        string directory = RecordingDeviceFolderNaming.BuildDirectoryName(new string('a', 500));

        Assert.True(
            directory.Length <= RecordingDeviceFolderNaming.MaximumDirectoryNameLength,
            $"目录名长度 {directory.Length} 超过上限");
    }

    /// <summary>目录名里不能出现文件系统非法字符，也不能以点或空格结尾。</summary>
    [Theory]
    [InlineData("device:001")]
    [InlineData("device/001")]
    [InlineData(@"device\001")]
    [InlineData("device*?001")]
    [InlineData("device001.")]
    [InlineData(" device001 ")]
    public void ProducesFileSystemSafeNames(string deviceId)
    {
        string directory = RecordingDeviceFolderNaming.BuildDirectoryName(deviceId);

        Assert.DoesNotContain(directory, Path.GetInvalidFileNameChars().Select(c => c.ToString()));
        Assert.Equal(directory.Trim().TrimEnd('.', ' '), directory);
        Assert.NotEmpty(directory);
    }

    /// <summary>设备号为空时用占位，不能拼出空目录名。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyDeviceIdUsesPlaceholder(string? deviceId)
    {
        Assert.Equal(
            RecordingDeviceFolderNaming.UnknownDeviceDirectoryName,
            RecordingDeviceFolderNaming.BuildDirectoryName(deviceId));
    }

    /// <summary>能认出自己写出来的目录名，迁移时据此跳过已经迁好的目录。</summary>
    [Fact]
    public void RecognizesItsOwnDirectoryName()
    {
        const string deviceId = "12345678-1234-1234-1234-1234569abcdef";
        string directory = RecordingDeviceFolderNaming.BuildDirectoryName(deviceId);

        Assert.True(RecordingDeviceFolderNaming.IsDirectoryNameFor(directory, deviceId));
        Assert.True(RecordingDeviceFolderNaming.IsDirectoryNameFor(directory.ToUpperInvariant(), deviceId));
        Assert.False(RecordingDeviceFolderNaming.IsDirectoryNameFor("设备-ABCDEF", deviceId));
        Assert.False(RecordingDeviceFolderNaming.IsDirectoryNameFor("一号打包手机-ABCDEF", deviceId));
        Assert.False(RecordingDeviceFolderNaming.IsDirectoryNameFor("", deviceId));
    }
}
