using System.IO;
using System.Threading;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 对网络路径做带超时的轻量探测，避免 NAS 离线时 GC/回放被 SMB 超时卡住。
/// 文件 API 无法真正取消，这里用线程池 + 超时等待，超时后调用方直接跳过本轮。
/// </summary>
internal static class RemoteFileProbe
{
    /// <summary>探测默认超时（也用于 ProbeAsync 等待门禁）。</summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 全局探测门禁：同一时间最多一个 SMB 探测在运行，
    /// 避免 NAS 挂死时线程池中被放弃的探测不断堆积。
    /// </summary>
    internal static readonly SemaphoreSlim ProbeGate = new(1, 1);

    public static bool TryProbeFile(string path, TimeSpan timeout)
    {
        if (!ProbeGate.Wait(timeout))
            return false;
        Task<bool> probe = Task.Run(() => SafeFileExists(path));
        if (probe.Wait(timeout))
        {
            ProbeGate.Release();
            return probe.Result;
        }
        _ = probe.ContinueWith(
            _ => ProbeGate.Release(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return false;
    }

    public static bool TryProbeFileWithSize(string path, long expectedSize, TimeSpan timeout)
    {
        if (!ProbeGate.Wait(timeout))
            return false;
        Task<bool> probe = Task.Run(() => SafeFileExistsWithSize(path, expectedSize));
        if (probe.Wait(timeout))
        {
            ProbeGate.Release();
            return probe.Result;
        }
        _ = probe.ContinueWith(
            _ => ProbeGate.Release(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return false;
    }

    /// <summary>
    /// 对网络目录根做带超时的可达性探测（Directory.Exists，File.Exists 对目录恒返回 false）。
    /// </summary>
    public static bool TryProbeDirectory(string path, TimeSpan timeout)
    {
        if (!ProbeGate.Wait(timeout))
            return false;
        Task<bool> probe = Task.Run(() => SafeDirectoryExists(path));
        if (probe.Wait(timeout))
        {
            ProbeGate.Release();
            return probe.Result;
        }
        _ = probe.ContinueWith(
            _ => ProbeGate.Release(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return false;
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
