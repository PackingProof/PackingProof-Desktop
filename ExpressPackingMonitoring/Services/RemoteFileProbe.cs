using System.IO;
using System.Threading;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 对网络路径做带超时的轻量探测，避免 NAS 离线时 GC/回放被 SMB 超时卡住。
/// 文件 API 无法真正取消，这里用线程池 + 超时等待，超时后调用方直接跳过本轮。
/// </summary>
internal static class RemoteFileProbe
{
    /// <summary>单文件三态探测结果：明确存在（且大小一致）／明确不存在／不可判断。</summary>
    internal enum FileProbeState
    {
        Exists,
        ConfirmedMissing,
        Unavailable
    }

    /// <summary>目录根探测结果：可达 / 不可达 / 门禁忙（本轮无法确认）。</summary>
    internal enum DirectoryProbeState
    {
        Reachable,
        Unreachable,
        Busy
    }

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

    /// <summary>
    /// 目录根可达性三态探测：门禁忙或探测超时返回 Busy，
    /// 调用方按需决定是跳过本轮还是按不可达处理。
    /// </summary>
    public static DirectoryProbeState TryProbeDirectoryState(string path, TimeSpan timeout)
    {
        if (!ProbeGate.Wait(timeout))
            return DirectoryProbeState.Busy;
        Task<bool> probe = Task.Run(() => SafeDirectoryExists(path));
        if (probe.Wait(timeout))
        {
            ProbeGate.Release();
            return probe.Result
                ? DirectoryProbeState.Reachable
                : DirectoryProbeState.Unreachable;
        }
        _ = probe.ContinueWith(
            _ => ProbeGate.Release(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return DirectoryProbeState.Busy;
    }

    /// <summary>
    /// 单文件三态探测（带大小确认）：文件明确不存在返回 ConfirmedMissing，
    /// 网络断开/超时/权限错误/门禁忙返回 Unavailable，只有可抛错 API 明确报告“不存在”才算缺失。
    /// 用于 NAS 对账与删除前确认，禁止用 File.Exists 的 bool 直接判断缺失。
    /// </summary>
    public static FileProbeState TryProbeFileState(
        string path,
        long expectedSize,
        TimeSpan timeout)
    {
        if (!ProbeGate.Wait(timeout))
            return FileProbeState.Unavailable;
        Task<FileProbeState> probe = Task.Run(() => ProbeFileStateCore(path, expectedSize));
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
        return FileProbeState.Unavailable;
    }

    private static FileProbeState ProbeFileStateCore(string path, long expectedSize)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
                return FileProbeState.ConfirmedMissing;
            return new FileInfo(path).Length == expectedSize
                ? FileProbeState.Exists
                : FileProbeState.Unavailable;
        }
        catch (FileNotFoundException)
        {
            return FileProbeState.ConfirmedMissing;
        }
        catch (DirectoryNotFoundException)
        {
            return FileProbeState.ConfirmedMissing;
        }
        catch
        {
            return FileProbeState.Unavailable;
        }
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
