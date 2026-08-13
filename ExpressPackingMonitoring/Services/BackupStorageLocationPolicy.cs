using ExpressPackingMonitoring.Config;

namespace ExpressPackingMonitoring.Services;

internal enum BackupStorageDecision
{
    Accept,
    ConfirmVirtualDisk,
    ConfirmUnknown,
    RejectPhysicalLocal
}

/// <summary>
/// “添加备份位置”的路径决策：网络共享直接允许；网盘挂载成的虚拟磁盘与
/// 无法确认类型的路径需用户确认后允许；明确物理本地磁盘拒绝。
/// </summary>
internal static class BackupStorageLocationPolicy
{
    public static BackupStorageDecision Evaluate(
        string path,
        Func<string, StorageVolumeInfo.StorageLocationKind>? classifier = null)
    {
        StorageVolumeInfo.StorageLocationKind kind = classifier != null
            ? classifier(path)
            : StorageVolumeInfo.ClassifyStorageLocation(path);

        return kind switch
        {
            StorageVolumeInfo.StorageLocationKind.Network =>
                BackupStorageDecision.Accept,
            StorageVolumeInfo.StorageLocationKind.VirtualDisk =>
                BackupStorageDecision.ConfirmVirtualDisk,
            StorageVolumeInfo.StorageLocationKind.Unknown =>
                BackupStorageDecision.ConfirmUnknown,
            _ => BackupStorageDecision.RejectPhysicalLocal
        };
    }
}
