using System.IO;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 录像目录树的递归遍历选项：跳过符号链接与目录联接。
///
/// macOS 保存主机在分类目录里放"昵称 → 设备目录"的符号链接替身；默认枚举会把指向目录的
/// 链接当成普通子目录、还会跟着递归进去，同一份录像就会被算两遍（容量、清理、归档都会受影响）。
/// Windows 上 .lnk 是文件不受影响，但用户自己建的目录联接同样不该被跟着走，所以两边统一跳过。
///
/// 默认的 AttributesToSkip 是 Hidden|System，覆盖时必须把它们带回来，否则隐藏文件会被算进去。
/// </summary>
internal static class RecordingTreeEnumeration
{
    internal static EnumerationOptions RecursiveSkipLinks { get; } = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        IgnoreInaccessible = true
    };
}
