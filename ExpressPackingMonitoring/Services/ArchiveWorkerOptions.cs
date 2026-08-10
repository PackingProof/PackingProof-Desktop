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
}
