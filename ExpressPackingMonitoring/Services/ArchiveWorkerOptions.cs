namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 归档 Worker 配置；BatchSize 可注入以便测试覆盖小批次行为。
/// </summary>
internal sealed class ArchiveWorkerOptions
{
    public int BatchSize { get; init; } = 20;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RemoteTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public bool AutomaticWorkerEnabled { get; init; } = true;

    /// <summary>同一备份根连续出现“网络不可达”错误达到该次数后触发熔断。</summary>
    public int UnreachableFailureThreshold { get; init; } = 3;

    /// <summary>熔断后的首次冷却时间，冷却结束后自动探测一次并决定继续或再次熔断。</summary>
    public TimeSpan UnreachableCooldown { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>连续熔断时冷却时间的上限（指数增长封顶）。</summary>
    public TimeSpan MaxUnreachableCooldown { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>NAS 恢复后第一轮允许处理的记录数，随后按倍数渐进放量。</summary>
    public int RecoveryInitialBatchSize { get; init; } = 1;

    /// <summary>NAS 恢复后单轮最大记录数，避免积压一次性冲击实时录像。</summary>
    public int RecoveryMaxBatchSize { get; init; } = 8;

    /// <summary>恢复放量各轮之间的最短间隔。</summary>
    public TimeSpan RecoveryInterBatchDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>恢复阶段 NAS 文件读写上限；0 表示不限制（单位：字节/秒）。</summary>
    public long RecoveryMaxBytesPerSecond { get; init; } = 8 * 1024 * 1024;
}
