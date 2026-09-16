using Microsoft.Data.Sqlite;

namespace ExpressPackingMonitoring.Data;

/// <summary>
/// 设备录像目录迁移用到的查询与回写。
///
/// 目录名从"设备-&lt;后六位&gt;"（更老的还带昵称前缀）改成完整设备号之后，磁盘上的目录要搬，
/// 而记录里的 <c>FilePath</c> 与 <c>ArchivePath</c> 存的是绝对路径 —— 回放、导出、归档校验、
/// 删除、NAS 清理全按这两个字符串定位文件，只搬目录不回写就等于让所有老录像"文件丢失"。
///
/// 单独一个分部文件：VideoDatabase.cs 是规模冻结的历史例外，不再往里堆查询。
/// </summary>
public partial class VideoDatabase
{
    /// <summary>
    /// 外部上传记录的路径清单（含已删除记录：它们的文件可能还在，路径同样要跟着搬）。
    /// </summary>
    public IReadOnlyList<DeviceFolderPathRecord> GetDeviceFolderPathRecords()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT Id,
                       COALESCE(SourceDeviceId, ''),
                       COALESCE(FilePath, ''),
                       COALESCE(ArchivePath, '')
                FROM VideoRecords
                WHERE LOWER(COALESCE(SourceType, '')) = 'external'
                  AND TRIM(COALESCE(SourceDeviceId, '')) <> ''
                  AND (TRIM(COALESCE(FilePath, '')) <> '' OR TRIM(COALESCE(ArchivePath, '')) <> '');";
            var records = new List<DeviceFolderPathRecord>();
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                records.Add(new DeviceFolderPathRecord(
                    reader.GetInt64(0),
                    reader.GetString(1).Trim(),
                    reader.GetString(2),
                    reader.GetString(3)));
            }
            return records;
        }
    }

    /// <summary>
    /// 把一条记录的路径改到新目录下。
    ///
    /// 带乐观条件：只有当库里的值仍然是我们读到的那个旧值时才改。期间被别的任务
    /// （MKV 转封装、归档发布、共享文件迁移）改过的话这次就不改，返回 false 留给下一轮，
    /// 绝不用旧快照覆盖别人的新结果。
    /// </summary>
    public bool TryMoveDeviceFolderPaths(
        long recordId,
        string expectedFilePath,
        string? newFilePath,
        string expectedArchivePath,
        string? newArchivePath)
    {
        if (newFilePath == null && newArchivePath == null)
            return false;

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE VideoRecords
                SET FilePath = @newFilePath,
                    ArchivePath = @newArchivePath
                WHERE Id = @id
                  AND COALESCE(FilePath, '') = @expectedFilePath
                  AND COALESCE(ArchivePath, '') = @expectedArchivePath;";
            cmd.Parameters.AddWithValue("@id", recordId);
            cmd.Parameters.AddWithValue("@expectedFilePath", expectedFilePath ?? "");
            cmd.Parameters.AddWithValue("@expectedArchivePath", expectedArchivePath ?? "");
            // 只搬其中一侧时，另一侧原样写回，避免把没迁移的那列清空。
            cmd.Parameters.AddWithValue("@newFilePath", newFilePath ?? expectedFilePath ?? "");
            cmd.Parameters.AddWithValue("@newArchivePath", newArchivePath ?? expectedArchivePath ?? "");
            bool updated = cmd.ExecuteNonQuery() == 1;
            if (!updated)
                return false;

            // 本地文件索引按路径记账，路径变了要跟着搬，否则容量统计会重复计入。
            if (newFilePath != null
                && !string.IsNullOrWhiteSpace(expectedFilePath)
                && !string.Equals(expectedFilePath, newFilePath, StringComparison.OrdinalIgnoreCase))
            {
                long size = TryReadLocalVideoFileSize(expectedFilePath);
                RemoveLocalVideoFileCore(expectedFilePath);
                if (size > 0)
                    UpsertLocalVideoFileCore(newFilePath, size);
            }
            return true;
        }
    }

    private long TryReadLocalVideoFileSize(string filePath)
    {
        string normalizedPath;
        try { normalizedPath = System.IO.Path.GetFullPath(filePath); }
        catch { return 0; }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT FileSizeBytes FROM LocalVideoFileInventory WHERE FilePath = @path;";
        cmd.Parameters.AddWithValue("@path", normalizedPath);
        object? value = cmd.ExecuteScalar();
        return value == null || value is DBNull ? 0 : Convert.ToInt64(value);
    }
}

/// <summary>一条外部上传记录的两个绝对路径：本地文件与 NAS 归档。</summary>
public sealed record DeviceFolderPathRecord(
    long Id,
    string SourceDeviceId,
    string FilePath,
    string ArchivePath);
