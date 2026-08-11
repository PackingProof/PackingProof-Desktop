using System.IO;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 对网络路径做带超时的轻量探测，避免 NAS 离线时 GC/回放被 SMB 超时卡住。
/// 文件 API 无法真正取消，这里用线程池 + 超时等待，超时后调用方直接跳过本轮。
/// </summary>
internal static class RemoteFileProbe
{
    public static bool TryProbeFile(string path, TimeSpan timeout)
    {
        Task<bool> probe = Task.Run(() => SafeFileExists(path));
        return probe.Wait(timeout) && probe.Result;
    }

    public static bool TryProbeFileWithSize(string path, long expectedSize, TimeSpan timeout)
    {
        Task<bool> probe = Task.Run(() => SafeFileExistsWithSize(path, expectedSize));
        return probe.Wait(timeout) && probe.Result;
    }

    /// <summary>
    /// 对网络目录根做带超时的可达性探测（Directory.Exists，File.Exists 对目录恒返回 false）。
    /// </summary>
    public static bool TryProbeDirectory(string path, TimeSpan timeout)
    {
        Task<bool> probe = Task.Run(() => SafeDirectoryExists(path));
        return probe.Wait(timeout) && probe.Result;
    }

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    private static bool SafeFileExistsWithSize(string path, long expectedSize)
    {
        try
        {
            if (!File.Exists(path)) return false;
            return new FileInfo(path).Length == expectedSize;
        }
        catch
        {
            return false;
        }
    }
}
