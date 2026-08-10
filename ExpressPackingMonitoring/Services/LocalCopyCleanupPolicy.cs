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
        if (record.ArchiveStatus != VideoArchiveStatus.Verified)
        {
            reason = $"归档状态 {record.ArchiveStatus} 未验证";
            return false;
        }
        if (record.ArchiveCompletedAt == null)
        {
            reason = "缺少归档完成时间";
            return false;
        }
        if (string.IsNullOrWhiteSpace(record.ArchivePath))
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
                or VideoArchiveStatus.Failed))
        {
            reason = $"状态 {record.ArchiveStatus} 不参与硬循环";
            return false;
        }
        return true;
    }
}
