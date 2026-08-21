using System.Security.Cryptography;
using System.Text.Json;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;

namespace ExpressPackingMonitoring.Services;

internal sealed class MobileBackupSharedFileMigrator
{
    private readonly VideoDatabase _database;
    private readonly Func<string, StorageVolumeInfo?> _volumeResolver;
    private readonly Action? _beforeDatabaseCommit;

    internal MobileBackupSharedFileMigrator(
        VideoDatabase database,
        Func<string, StorageVolumeInfo?>? volumeResolver = null,
        Action? beforeDatabaseCommit = null)
    {
        _database = database;
        _volumeResolver = volumeResolver ?? ResolveVolume;
        _beforeDatabaseCommit = beforeDatabaseCommit;
    }

    internal SharedFileMigrationSummary Run()
    {
        int completedGroups = 0;
        int pendingGroups = 0;
        List<IGrouping<string, VideoRecord>> groups = _database.QueryVideos(null, null)
            .Where(record => !record.IsDeleted
                && string.Equals(record.SourceType, "external", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(record.FilePath))
            .GroupBy(record => record.FilePath.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToList();

        foreach (IGrouping<string, VideoRecord> group in groups)
        {
            List<VideoRecord> records = group.OrderBy(record => record.Id).ToList();
            List<MigrationState> states;
            try
            {
                states = records.Select(ReadState).ToList();
            }
            catch (SharedFileMigrationException)
            {
                pendingGroups++;
                continue;
            }
            _database.MarkSharedFileMigrationPending(records
                .Zip(states)
                .Select(pair => new SharedFileMigrationQuarantine(
                    pair.First.Id,
                    JsonSerializer.Serialize(pair.Second with { LastError = "" })))
                .ToList());
            try
            {
                MaterializeGroup(records, states);
                completedGroups++;
            }
            catch (Exception ex)
            {
                pendingGroups++;
                try
                {
                    _database.MarkSharedFileMigrationPending(records
                        .Zip(states)
                        .Select(pair => new SharedFileMigrationQuarantine(
                            pair.First.Id,
                            JsonSerializer.Serialize(pair.Second with { LastError = ex.Message })))
                        .ToList());
                }
                catch
                {
                    // 首次隔离事务已经成功；诊断更新失败不能解除清理门禁。
                }
            }
        }
        return new SharedFileMigrationSummary(completedGroups, pendingGroups);
    }

    private void MaterializeGroup(IReadOnlyList<VideoRecord> records, IReadOnlyList<MigrationState> states)
    {
        VideoRecord sourceRecord = records[0];
        string sourcePath = Path.GetFullPath(sourceRecord.FilePath);
        ValidateSource(sourcePath, sourceRecord.ContentSha256);
        long fileSize = new FileInfo(sourcePath).Length;
        var copies = new List<PlannedCopy>();
        var updates = new List<SharedFileMigrationUpdate>(records.Count);

        for (int index = 0; index < records.Count; index++)
        {
            VideoRecord record = records[index];
            MigrationState state = states[index];
            string newFilePath = index == 0
                ? sourcePath
                : BuildIndependentPath(sourcePath, record.Id, record.SourceDeviceId, record.SourceSessionId);
            if (index > 0)
                copies.Add(new PlannedCopy(sourcePath, newFilePath, record.ContentSha256));

            string expectedArchivePath = record.ArchivePath?.Trim() ?? "";
            string newArchivePath = expectedArchivePath;
            if (!string.IsNullOrWhiteSpace(expectedArchivePath))
            {
                ValidateSource(expectedArchivePath, record.ContentSha256);
                if (index > 0)
                {
                    newArchivePath = BuildIndependentPath(
                        expectedArchivePath, record.Id, record.SourceDeviceId, record.SourceSessionId);
                    copies.Add(new PlannedCopy(expectedArchivePath, newArchivePath, record.ContentSha256));
                }
            }
            else if (state.OriginalArchiveStatus is VideoArchiveStatus.Verified or VideoArchiveStatus.LocalDeleted)
            {
                throw new SharedFileMigrationException("共享录像缺少可验证的归档副本");
            }

            updates.Add(new SharedFileMigrationUpdate(
                record.Id,
                record.FilePath,
                newFilePath,
                fileSize,
                expectedArchivePath,
                newArchivePath,
                state.OriginalArchiveStatus,
                state.OriginalArchiveError));
        }

        EnsureSpace(copies);
        var createdPaths = new List<string>();
        try
        {
            foreach (PlannedCopy copy in copies)
            {
                if (File.Exists(copy.DestinationPath))
                {
                    ValidateSource(copy.DestinationPath, copy.ExpectedSha256);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(copy.DestinationPath)!);
                CopyAtomically(copy.SourcePath, copy.DestinationPath);
                createdPaths.Add(copy.DestinationPath);
                ValidateSource(copy.DestinationPath, copy.ExpectedSha256);
            }
            _beforeDatabaseCommit?.Invoke();
            _database.ApplySharedFileMigration(updates);
        }
        catch
        {
            foreach (string path in createdPaths)
            {
                try { File.Delete(path); } catch { }
            }
            throw;
        }
    }

    private void EnsureSpace(IReadOnlyList<PlannedCopy> copies)
    {
        var requiredByVolume = new Dictionary<string, (StorageVolumeInfo Volume, long Required)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (PlannedCopy copy in copies)
        {
            if (File.Exists(copy.DestinationPath)) continue;
            StorageVolumeInfo volume = _volumeResolver(copy.DestinationPath)
                ?? throw new SharedFileMigrationException("无法读取共享录像迁移目标卷空间");
            long length = new FileInfo(copy.SourcePath).Length;
            if (requiredByVolume.TryGetValue(volume.RootPath, out var current))
                requiredByVolume[volume.RootPath] = (volume, checked(current.Required + length));
            else
                requiredByVolume[volume.RootPath] = (volume, length);
        }

        foreach ((StorageVolumeInfo volume, long required) in requiredByVolume.Values)
        {
            long reserve = StorageSpacePolicy.CalculateMinimumReserveBytes(volume);
            if (volume.AvailableFreeSpace < checked(required + reserve))
                throw new SharedFileMigrationException("共享录像迁移空间不足，已保留原始共享文件");
        }
    }

    private static MigrationState ReadState(VideoRecord record)
    {
        if (record.ArchiveStatus == VideoArchiveStatus.SharedFileMigrationPending)
        {
            try
            {
                MigrationState? state = JsonSerializer.Deserialize<MigrationState>(record.ArchiveError);
                if (state != null && !string.IsNullOrWhiteSpace(state.OriginalArchiveStatus))
                    return state;
            }
            catch (JsonException)
            {
            }
            throw new SharedFileMigrationException("共享录像迁移状态损坏，已保持隔离");
        }
        return new MigrationState(record.ArchiveStatus, record.ArchiveError, "");
    }

    private static string BuildIndependentPath(
        string sourcePath,
        long recordId,
        string sourceDeviceId,
        string sourceSessionId)
    {
        string fingerprint = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{sourceDeviceId}\n{sourceSessionId}\n{recordId}")))[..16]
            .ToLowerInvariant();
        string directory = Path.GetDirectoryName(sourcePath)!;
        string baseName = Path.GetFileNameWithoutExtension(sourcePath);
        string extension = Path.GetExtension(sourcePath);
        return Path.Combine(directory, $"{baseName}_record-{recordId}-{fingerprint}{extension}");
    }

    private static void ValidateSource(string path, string expectedSha256)
    {
        if (!File.Exists(path))
            throw new SharedFileMigrationException("共享录像迁移源文件不可读");
        if (string.IsNullOrWhiteSpace(expectedSha256))
            throw new SharedFileMigrationException("共享录像缺少完整文件校验值");
        using FileStream stream = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new SharedFileMigrationException("共享录像迁移源文件校验失败");
    }

    private static void CopyAtomically(string sourcePath, string destinationPath)
    {
        string tempPath = $"{destinationPath}.{Guid.NewGuid():N}.migration";
        try
        {
            File.Copy(sourcePath, tempPath);
            File.Move(tempPath, destinationPath);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private static StorageVolumeInfo? ResolveVolume(string path) =>
        StorageVolumeInfo.TryGet(path, out StorageVolumeInfo volume) ? volume : null;

    private sealed record MigrationState(
        string OriginalArchiveStatus,
        string OriginalArchiveError,
        string LastError);

    private sealed record PlannedCopy(
        string SourcePath,
        string DestinationPath,
        string ExpectedSha256);
}

internal sealed record SharedFileMigrationSummary(int CompletedGroups, int PendingGroups);

internal sealed class SharedFileMigrationException(string message) : Exception(message);
