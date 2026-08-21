using ExpressPackingMonitoring.Data;
using System.IO;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 容量清理只允许删除“网络端已归档且远端存在”的本地副本，
/// 保留今天/昨天，未归档文件绝不因容量清理删除。
/// </summary>
internal static class LocalCopyCleanupPolicy
{
    /// <summary>硬循环触发的最低可用空间保护线（固定内部常量，不提供 UI 配置）。</summary>
    public const long EmergencyCleanupThresholdBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>硬循环删除未归档录像的最小保护期（固定内部常量）。</summary>
    public static readonly TimeSpan EmergencyDeleteGracePeriod = TimeSpan.FromMinutes(30);

    /// <summary>未归档录像被容量清理删除时，同一提示的最小间隔（UI 节流，日志不受限）。</summary>
    public static readonly TimeSpan UnarchivedCleanupWarningCooldown = TimeSpan.FromHours(6);

    /// <summary>
    /// 未归档录像删除分档顺序：先删已尝试失败（占用空间且短期无意义），
    /// 再删等待归档，最后删从未备份过的 LocalOnly；档内由查询按结束时间最旧优先。
    /// NasDeleted 排最前：它不会重新进入归档队列，本地空间紧张时应优先于仍可能恢复归档的
    /// Pending/Failed 清理。
    /// </summary>
    public static readonly string[] UnarchivedCleanupTiers =
    [
        VideoArchiveStatus.NasDeleted,
        VideoArchiveStatus.Failed,
        VideoArchiveStatus.Pending,
        VideoArchiveStatus.LocalOnly
    ];

    /// <summary>GC 远端探测缓存窗口：24 小时内已成功探测则不重复探测。</summary>
    public static readonly TimeSpan ProbeCacheWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// 远端确认过期窗口：超过该时长没有成功探测时，NAS 不可达不再阻塞本地容量清理，
    /// Verified 本地副本可按本地策略删除并打未确认原因码；与探测缓存窗口一致。
    /// </summary>
    public static readonly TimeSpan UnconfirmedRemoteCleanupGrace = TimeSpan.FromHours(24);

    public static bool IsEligibleForCapacityCleanup(
        VideoRecord record,
        DateTime now,
        out string reason)
    {
        reason = "";
        if (record == null || record.IsDeleted)
        {
            reason = "记录已删除";
            return false;
        }
        if (string.IsNullOrWhiteSpace(record.FilePath) || !File.Exists(record.FilePath))
        {
            reason = "本地文件不存在";
            return false;
        }
        if (record.ArchiveStatus is not (
                VideoArchiveStatus.Verified
                or VideoArchiveStatus.NasDeleted))
        {
            reason = $"归档状态 {record.ArchiveStatus} 未验证";
            return false;
        }
        if (record.ArchiveStatus == VideoArchiveStatus.Verified
            && record.ArchiveCompletedAt == null)
        {
            reason = "缺少归档完成时间";
            return false;
        }
        if (record.ArchiveStatus == VideoArchiveStatus.Verified
            && string.IsNullOrWhiteSpace(record.ArchivePath))
        {
            reason = "缺少归档路径";
            return false;
        }
        if (record.StartTime >= now.Date.AddDays(-1))
        {
            reason = "今天/昨天本地副本保留";
            return false;
        }
        return true;
    }

    /// <summary>
    /// 硬循环候选：仅本地/等待归档/失败，已结束且超过保护期；Conflict 永不进入硬循环。
    /// </summary>
    public static bool IsEligibleForEmergencyCleanup(
        VideoRecord record,
        DateTime now,
        out string reason)
    {
        reason = "";
        if (record == null || record.IsDeleted)
        {
            reason = "记录已删除";
            return false;
        }
        if (string.IsNullOrWhiteSpace(record.FilePath) || !File.Exists(record.FilePath))
        {
            reason = "本地文件不存在";
            return false;
        }
        if (record.EndTime == DateTime.MinValue
            || record.EndTime > now - EmergencyDeleteGracePeriod)
        {
            reason = "未结束或处于保护期";
            return false;
        }
        if (record.ArchiveStatus is not (
                VideoArchiveStatus.LocalOnly
                or VideoArchiveStatus.Pending
                or VideoArchiveStatus.Failed
                or VideoArchiveStatus.NasDeleted))
        {
            reason = $"状态 {record.ArchiveStatus} 不参与硬循环";
            return false;
        }
        return true;
    }

    /// <summary>
    /// 在记录所有权锁内确认候选快照仍指向同一条可清理记录。
    /// </summary>
    internal static bool IsEmergencyCleanupSnapshotCurrent(
        VideoRecord snapshot,
        VideoRecord? current,
        DateTime now)
    {
        if (snapshot == null
            || current == null
            || current.IsDeleted
            || string.IsNullOrWhiteSpace(current.FilePath)
            || !string.Equals(
                current.FilePath,
                snapshot.FilePath,
                StringComparison.OrdinalIgnoreCase)
            || ((!string.IsNullOrWhiteSpace(snapshot.ContentSha256)
                 || !string.IsNullOrWhiteSpace(current.ContentSha256))
                && !string.Equals(
                    current.ContentSha256,
                    snapshot.ContentSha256,
                    StringComparison.OrdinalIgnoreCase))
            || !IsEligibleForEmergencyCleanup(current, now, out _)
            || !File.Exists(current.FilePath))
        {
            return false;
        }

        if (current.FileSizeBytes <= 0)
            return true;

        try
        {
            return new FileInfo(current.FilePath).Length == current.FileSizeBytes;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>LastArchiveProbeAt 是否在探测缓存窗口内（24 小时内免重复探测）。</summary>
    public static bool IsProbeFresh(VideoRecord record, DateTime now) =>
        record?.LastArchiveProbeAt != null
        && now - record.LastArchiveProbeAt.Value < ProbeCacheWindow;

    /// <summary>远端确认是否已过期（超过 24 小时或从未成功探测）。</summary>
    public static bool IsRemoteConfirmationStale(VideoRecord record, DateTime now) =>
        !IsProbeFresh(record, now);

    /// <summary>GC 删除本地录像文件前是否需要实时探测归档目标。</summary>
    public static bool ShouldProbeBeforeLocalCleanup(VideoRecord record, DateTime now) =>
        !IsProbeFresh(record, now);

    /// <summary>
    /// 硬循环是否应删除未归档录像：没有归档目标或归档目标不可达时执行删除；
    /// 归档目标可达时优先唤醒归档，不删除。
    /// </summary>
    public static bool ShouldTriggerEmergencyCleanup(
        string? archiveRoot,
        bool archiveRootReachable) =>
        string.IsNullOrWhiteSpace(archiveRoot) || !archiveRootReachable;

    /// <summary>
    /// 正常容量 GC 是否允许回退删除未归档录像：没有归档目标或确认探测不可达时允许；
    /// 门禁忙（无法确认）时跳过本轮，避免把探测拥挤误判为 NAS 不可用。
    /// </summary>
    public static bool ShouldFallbackToUnarchivedCleanup(
        string? archiveRoot,
        RemoteFileProbe.DirectoryProbeState archiveState) =>
        string.IsNullOrWhiteSpace(archiveRoot)
        || archiveState == RemoteFileProbe.DirectoryProbeState.Unreachable;

    /// <summary>正常 GC 已释放部分空间后，紧急清理仍需释放的目标字节数。</summary>
    public static long ComputeEmergencyReleaseTarget(
        long totalCurrentBytes,
        long totalCapacityBytes,
        long releasedByNormalCleanup)
    {
        long remainingCurrentBytes = Math.Max(
            0,
            totalCurrentBytes - Math.Max(0, releasedByNormalCleanup));
        return Math.Max(
            0,
            remainingCurrentBytes - (long)(Math.Max(0, totalCapacityBytes) * 0.9));
    }
}
