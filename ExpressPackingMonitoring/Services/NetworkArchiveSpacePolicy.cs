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
}
