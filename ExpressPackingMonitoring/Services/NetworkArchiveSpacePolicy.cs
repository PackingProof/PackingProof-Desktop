using System.IO;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// NAS 满提示策略：NAS 卷可用空间只影响归档任务，不影响本地录像、本地 GC 与硬循环保护机制。
/// </summary>
internal static class NetworkArchiveSpacePolicy
{
    /// <summary>同一 NAS 空间不足提示的最小间隔（固定内部常量，不提供 UI 配置）。</summary>
    public static readonly TimeSpan WarningCooldown = TimeSpan.FromMinutes(60);

    public static bool IsBelowReserve(long availableBytes, long reserveBytes) =>
        availableBytes <= reserveBytes;

    public static bool ShouldWarn(DateTime lastWarnedAt, DateTime now) =>
        now - lastWarnedAt >= WarningCooldown;

    /// <summary>
    /// 判断异常是否由目标磁盘/共享空间不足引起，用于进入 NASFull 状态而非普通失败重试。
    /// </summary>
    public static bool IsDiskFullException(Exception ex)
    {
        for (Exception? current = ex;
             current != null;
             current = current.InnerException)
        {
            if (current is IOException
                && (IsDiskFullHResult(current.HResult)))
            {
                return true;
            }

            string message = current.Message ?? "";
            if (message.Contains("磁盘空间不足", StringComparison.OrdinalIgnoreCase)
                || message.Contains("空间不足", StringComparison.OrdinalIgnoreCase)
                || message.Contains("no space left", StringComparison.OrdinalIgnoreCase)
                || message.Contains("disk full", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsDiskFullHResult(int hresult)
    {
        int code = hresult & 0xFFFF;
        return code is 112 or 39; // ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL
    }
}
