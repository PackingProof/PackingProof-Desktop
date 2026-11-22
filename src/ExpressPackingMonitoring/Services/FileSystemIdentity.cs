using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Runtime.InteropServices;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 判断两个路径是否指向同一个文件。Windows 上使用卷序列号和文件索引，
/// 覆盖硬链接、符号链接、映射盘和短路径等仅靠字符串无法识别的别名。
/// </summary>
internal static class FileSystemIdentity
{
    internal static bool AreSameFile(string firstPath, string secondPath)
    {
        if (string.IsNullOrWhiteSpace(firstPath)
            || string.IsNullOrWhiteSpace(secondPath))
        {
            return false;
        }

        string firstFull = Path.GetFullPath(firstPath);
        string secondFull = Path.GetFullPath(secondPath);
        if (string.Equals(firstFull, secondFull, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!File.Exists(firstFull) || !File.Exists(secondFull))
            return false;

        if (OperatingSystem.IsWindows()
            && TryReadWindowsIdentity(firstFull, out WindowsFileIdentity first)
            && TryReadWindowsIdentity(secondFull, out WindowsFileIdentity second))
        {
            return first == second;
        }

        try
        {
            string? firstResolved = File.ResolveLinkTarget(firstFull, returnFinalTarget: true)
                ?.FullName;
            string? secondResolved = File.ResolveLinkTarget(secondFull, returnFinalTarget: true)
                ?.FullName;
            return string.Equals(
                Path.GetFullPath(firstResolved ?? firstFull),
                Path.GetFullPath(secondResolved ?? secondFull),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadWindowsIdentity(
        string path,
        out WindowsFileIdentity identity)
    {
        identity = default;
        try
        {
            using SafeFileHandle handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info))
                return false;

            identity = new WindowsFileIdentity(
                info.VolumeSerialNumber,
                info.FileIndexHigh,
                info.FileIndexLow);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct WindowsFileIdentity(
        uint VolumeSerialNumber,
        uint FileIndexHigh,
        uint FileIndexLow);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal NativeFileTime CreationTime;
        internal NativeFileTime LastAccessTime;
        internal NativeFileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);
}
