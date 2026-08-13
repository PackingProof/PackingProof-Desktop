using System;
using System.IO;

namespace ExpressPackingMonitoring.Config
{
    /// <summary>
    /// 维护 StorageLocation 的卷标识元数据，为未来盘符变化自动重定位预留数据。
    /// 本版本只保存信息，不实现重映射逻辑。
    /// </summary>
    internal static class StorageLocationMetadata
    {
        /// <summary>
        /// 在 VolumeId 为空或卷已变化时刷新 VolumeId 与 LastVerifiedAt。
        /// 卷未变化时保持 LastVerifiedAt 不变，避免每次启动改写配置。
        /// </summary>
        public static bool RefreshVolumeId(StorageLocation location)
        {
            if (location == null || string.IsNullOrWhiteSpace(location.Path)) return false;
            if (location.IsBackupTarget
                || StorageVolumeInfo.IsBackupTargetPath(location.Path))
            {
                return false;
            }

            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(location.Path)) ?? "";
                if (string.IsNullOrWhiteSpace(root)) return false;

                string volumeId = StorageVolumeInfo.GetVolumeIdForRoot(root);
                if (string.IsNullOrWhiteSpace(volumeId)) return false;
                if (string.Equals(
                        location.VolumeId,
                        volumeId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                location.VolumeId = volumeId;
                location.LastVerifiedAt = DateTime.Now;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
