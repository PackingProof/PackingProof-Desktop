using ExpressPackingMonitoring.Data;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// “回放里能不能删掉这条录像”的唯一判定口：界面用它决定右键菜单是否可用，
/// <see cref="RecordingDeleteService"/> 用它做锁内复查，两边规则必须一致。
/// 允许删除的前提是本机拿得到这条录像的实体：本地文件在，或本地副本早已按容量策略
/// 清理过、记录上还有完成归档的证据。
/// </summary>
internal static class RecordingDeletePolicy
{
    /// <summary>删除原因：写进录像记录与删除日志，售后按原因码定位。</summary>
    internal const string Reason = "用户删除";

    /// <summary>录像实体保存在录像主机上，本机只有记录（见 VideoDatabase.MarkVideoCacheDeleted）。</summary>
    private const string RemoteStorageState = "Remote";

    /// <summary>能删除返回 null，不能删除时返回给用户看的一句说明。</summary>
    internal static string? DescribeRefusal(VideoRecord? record, bool localFileExists)
    {
        if (record == null)
            return "该录像已删除或不存在";
        if (record.IsDeleted)
            return "该录像已经删除";
        // EndTime 为空表示还在录，文件正在写，这时候删会把录制链路一起弄乱。
        if (record.EndTime == default)
            return "录像正在录制，停止录制后才能删除";
        if (record.ArchiveStatus is VideoArchiveStatus.Copying or VideoArchiveStatus.Verifying)
            return "录像正在备份，请稍后再试";
        if (string.Equals(record.StorageState, RemoteStorageState, StringComparison.OrdinalIgnoreCase))
            return "录像保存在录像主机上，请在录像主机上删除";
        if (!localFileExists && !HasCompletedArchiveEvidence(record))
            return "本机找不到录像文件，无法删除";
        return null;
    }

    internal static bool CanDelete(VideoRecord? record, bool localFileExists) =>
        DescribeRefusal(record, localFileExists) == null;

    /// <summary>
    /// 本机找不到文件但已确认归档的记录仍可删除：本地副本是容量清理主动收掉的，
    /// 这里删的是“记录本身”，备份位置上的归档副本按既有约定保留。
    /// </summary>
    private static bool HasCompletedArchiveEvidence(VideoRecord record) =>
        record.ArchiveCompletedAt != null
        && !string.IsNullOrWhiteSpace(record.ArchivePath)
        && record.ArchiveStatus is VideoArchiveStatus.Verified or VideoArchiveStatus.LocalDeleted;
}
