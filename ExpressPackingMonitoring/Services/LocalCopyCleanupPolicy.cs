using ExpressPackingMonitoring.Data;
using System.IO;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 容量清理只允许删除“网络端已归档且远端存在”的本地副本，
/// 保留今天/昨天，未归档文件绝不因容量清理删除。
/// </summary>
internal static class LocalCopyCleanupPolicy
{
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
}
