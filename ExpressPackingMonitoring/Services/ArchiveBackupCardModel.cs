using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using System;

namespace ExpressPackingMonitoring.Services;

/// <summary>主界面/备份主机“录像备份”卡片状态：短状态文字与详情。</summary>
internal readonly record struct ArchiveBackupCardState(
    string ShortStatusText,
    string DetailText);

/// <summary>
/// “录像备份”卡片的共享状态模型：主界面与录像文件备份主机窗口共用，
/// 避免两处各自实现一套状态文案与判重逻辑。
/// </summary>
internal static class ArchiveBackupCardModel
{
    /// <summary>录像备份卡片可见条件：非录像工作站角色且配置了至少一个网络备份位置。</summary>
    internal static bool ShouldShowArchiveBackupCard(
        AppConfig config,
        bool isRecordingWorkstation) =>
        !isRecordingWorkstation
        && StorageLocationResolver.GetOrderedNetworkLocations(config).Count > 0;

    internal static string ResolveCurrentArchiveTarget(AppConfig config) =>
        StorageLocationResolver.GetOrderedNetworkLocations(config)
            .Select(location => location.Path)
            .FirstOrDefault()
        ?? "";

    /// <summary>
    /// 构建“录像备份”卡片状态，优先级：
    /// 备份丢失 > 上传中 > 备份失败 > 备份异常(Conflict) > 备份暂停 > 待核实 > 待备份
    /// > 已清理(无待备份) > 已同步。
    /// </summary>
    internal static ArchiveBackupCardState BuildArchiveBackupCardState(
        ArchiveQueueSummary summary,
        string currentTarget,
        bool targetUnavailable = false,
        string unavailableRoot = "")
    {
        int remaining = summary.RemainingCount;

        if (targetUnavailable)
        {
            return new ArchiveBackupCardState(
                "备份位置不可用",
                string.IsNullOrWhiteSpace(unavailableRoot)
                    ? $"{remaining} 个录像等待备份，录像仍保存在本地，恢复后自动重试"
                    : $"无法访问备份位置 {CompactArchiveTarget(unavailableRoot)}，录像仍保存在本地，恢复后自动重试");
        }

        if (summary.LostCount > 0)
        {
            return new ArchiveBackupCardState(
                "备份丢失",
                $"{summary.LostCount} 个录像本地与 NAS 均无可信副本，请检查");
        }
        if (summary.UploadingCount > 0)
        {
            return new ArchiveBackupCardState(
                "备份中",
                $"正在备份 · 共 {remaining} 个待备份");
        }
        if (summary.FailedCount > 0)
        {
            return new ArchiveBackupCardState(
                "备份失败",
                $"{remaining} 个录像等待重试，录像仍保存在本地");
        }
        if (summary.ConflictCount > 0)
        {
            return new ArchiveBackupCardState(
                "备份异常",
                $"{remaining} 个录像归档冲突，请检查 NAS 同名文件");
        }
        if (summary.NasFullCount > 0)
        {
            return new ArchiveBackupCardState(
                "备份暂停",
                $"备份位置空间不足，{remaining} 个录像等待空间恢复");
        }
        if (summary.PendingVerificationCount > 0)
        {
            return new ArchiveBackupCardState(
                "待核实",
                $"{remaining} 个录像本地副本缺失，正在确认 NAS 归档");
        }
        if (summary.PendingCount > 0 || summary.LocalOnlyCount > 0)
        {
            return new ArchiveBackupCardState(
                "待备份",
                $"{remaining} 个录像等待备份到 NAS");
        }
        if (summary.CleanedUnbackedCount > 0)
        {
            return new ArchiveBackupCardState(
                "已清理",
                $"{summary.CleanedUnbackedCount} 个录像已清理且未备份到 NAS");
        }
        return new ArchiveBackupCardState(
            "已同步",
            string.IsNullOrWhiteSpace(currentTarget)
                ? "全部已备份"
                : $"全部已备份 · {CompactArchiveTarget(currentTarget)}");
    }

    /// <summary>把 UNC 备份目标压缩为服务器主机部分（\\IP），避免卡片详情被截断。</summary>
    internal static string CompactArchiveTarget(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                string trimmed = path.TrimStart('\\');
                int end = trimmed.IndexOf('\\');
                string server = end < 0 ? trimmed : trimmed[..end];
                if (!string.IsNullOrWhiteSpace(server))
                    return @"\\" + server;
            }
        }
        catch
        {
        }
        return path;
    }
}
