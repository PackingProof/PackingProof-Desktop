using System.Security.Cryptography;
using System.Text;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 设备录像目录的命名规则：**目录名就是完整设备号**。
///
/// 这里刻意不用"主机分配的序号"之类的方案。序号要靠一份额外的映射表持久化，
/// 那份表一旦丢失或损坏，所有设备都会重新编号，同一台设备的录像就散进两个目录
/// —— 分配、回收、落盘失败、清理规则每一处都是分裂风险。
/// 目录名取成设备号的**纯函数**之后，这一整类问题都不存在：没有分配、没有状态、
/// 没有文件可丢，同一台设备永远落同一个目录，是由构造保证的。
///
/// 也不再截短设备号。之前取后六位是为了目录名好看，但六位十六进制会撞号，
/// 撞上就是两台设备的录像混进同一个目录，比目录名难看严重得多。
///
/// 昵称不进目录名（昵称随时可改，写进去同一台设备的录像会散成多个目录），
/// 目录与昵称的对应关系由录像根目录下的快捷方式给出。
/// </summary>
internal static class RecordingDeviceFolderNaming
{
    /// <summary>设备号为空时的目录名占位。</summary>
    internal const string UnknownDeviceDirectoryName = "未识别设备";

    /// <summary>
    /// 目录名长度上限。设备号是 36 字符的 GUID，留出余量应对自报超长 ID 的客户端，
    /// 同时保证"根目录 + 分类 + 设备 + 日期 + 文件名"不逼近 Windows 的 260 上限。
    /// </summary>
    internal const int MaximumDirectoryNameLength = 64;

    /// <summary>区分后缀的长度：截断或含非法字符时补上，保证不同设备号不会撞成同一个目录。</summary>
    private const int DistinctSuffixLength = 8;

    /// <summary>
    /// 目录名。设备号原样使用；含文件系统非法字符或超长时才做处理，
    /// 并补一段设备号哈希后缀 —— 替换和截断都可能把两个不同的设备号压成同一个名字，
    /// 补后缀是为了让"不同设备号 → 不同目录"这条在任何输入下都成立。
    /// </summary>
    internal static string BuildDirectoryName(string? deviceId)
    {
        string raw = deviceId?.Trim() ?? "";
        if (raw.Length == 0)
            return UnknownDeviceDirectoryName;

        string safe = MakeFileSystemSafe(raw, out bool altered);
        if (safe.Length == 0)
            return $"{UnknownDeviceDirectoryName}-{BuildDistinctSuffix(raw)}";

        if (safe.Length > MaximumDirectoryNameLength)
        {
            safe = safe[..(MaximumDirectoryNameLength - DistinctSuffixLength - 1)];
            altered = true;
        }

        return altered ? $"{safe}-{BuildDistinctSuffix(raw)}" : safe;
    }

    /// <summary>
    /// 判断一个目录名是否就是这台设备的目录。迁移时据此跳过已经迁好的目录，
    /// 重复执行不会做多余的事。
    /// </summary>
    internal static bool IsDirectoryNameFor(string? directoryName, string? deviceId) =>
        !string.IsNullOrWhiteSpace(directoryName)
        && string.Equals(
            directoryName.Trim(),
            BuildDirectoryName(deviceId),
            StringComparison.OrdinalIgnoreCase);

    private static string MakeFileSystemSafe(string value, out bool altered)
    {
        altered = false;
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            // 只放行确定安全的字符集：GUID 设备号完整落在这里，一个字符都不会被改。
            if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            {
                builder.Append(character);
                continue;
            }

            altered = true;
        }

        string safe = builder.ToString().Trim('.', ' ', '-');
        if (safe.Length != builder.Length)
            altered = true;

        return safe;
    }

    /// <summary>设备号哈希的前若干位十六进制：同一个设备号永远得到同一段后缀。</summary>
    private static string BuildDistinctSuffix(string deviceId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId)))
            [..DistinctSuffixLength]
            .ToUpperInvariant();
}
