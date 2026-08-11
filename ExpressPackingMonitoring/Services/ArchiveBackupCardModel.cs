using ExpressPackingMonitoring.Config;
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
    /// 构建“录像备份”卡片状态，优先级与从机上传卡片一致：
    /// 备份中 → 备份失败 → 备份暂停 → 待备份 → 已同步。
    /// </summary>
    internal static ArchiveBackupCardState BuildArchiveBackupCardState(
        int pendingCount,
        int uploadingCount,
        int failedCount,
        int pausedCount,
        string currentTarget)
    {
        int remaining = Math.Max(0, pendingCount)
            + Math.Max(0, uploadingCount)
            + Math.Max(0, failedCount)
            + Math.Max(0, pausedCount);

        if (uploadingCount > 0)
        {
            return new ArchiveBackupCardState(
                "备份中",
                $"正在备份 · 共 {remaining} 个待备份");
        }
        if (failedCount > 0)
        {
            return new ArchiveBackupCardState(
                "备份失败",
                $"{remaining} 个录像等待重试，录像仍保存在本地");
        }
        if (pausedCount > 0)
        {
            return new ArchiveBackupCardState(
                "备份暂停",
                $"备份位置空间不足，{remaining} 个录像等待空间恢复");
        }
        if (pendingCount > 0)
        {
            return new ArchiveBackupCardState(
                "待备份",
                $"{remaining} 个录像等待备份到 NAS");
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
