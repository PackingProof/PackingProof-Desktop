using System.IO;
using System.Security.Cryptography;
using ExpressPackingMonitoring.Config;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// Chooses and tracks the physical storage placement for each incoming recording.
/// A recording never moves after its first chunk is staged.
/// </summary>
internal sealed class MobileBackupStoragePlacement
{
    private readonly string _stateDirectory;
    private readonly Func<IReadOnlyList<string>> _recordingRootsResolver;
    private readonly HostUploadStorageAdmissionController _storageAdmission;
    private readonly Func<string, StorageVolumeInfo?> _storageVolumeProbe;

    internal MobileBackupStoragePlacement(
        string stateDirectory,
        Func<IReadOnlyList<string>> recordingRootsResolver,
        HostUploadStorageAdmissionController storageAdmission,
        Func<string, StorageVolumeInfo?> storageVolumeProbe)
    {
        _stateDirectory = stateDirectory;
        _recordingRootsResolver = recordingRootsResolver;
        _storageAdmission = storageAdmission;
        _storageVolumeProbe = storageVolumeProbe;
    }

    internal IReadOnlyList<string> ResolveRoots()
    {
        try
        {
            return _recordingRootsResolver()
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(root => Path.GetFullPath(root.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            throw new MobileBackupStorageUnavailableException(
                $"录像保存位置不可用：{ex.Message}");
        }
    }

    internal string ResolveStoredUploadRoot(MobileBackupUploadState state)
    {
        if (string.IsNullOrWhiteSpace(state.UploadRoot))
            throw new MobileBackupStorageUnavailableException(
                "上传任务尚未绑定录像保存磁盘");
        string root = Path.GetFullPath(state.UploadRoot);
        BindUploadRoot(state, root);
        return root;
    }

    internal void BindUploadRoot(
        MobileBackupUploadState state,
        string uploadRoot)
    {
        if (string.IsNullOrWhiteSpace(uploadRoot))
            throw new MobileBackupStorageUnavailableException(
                "录像保存位置为空，无法绑定上传任务");
        if (_storageVolumeProbe(uploadRoot) is not StorageVolumeInfo volume)
        {
            throw new MobileBackupStorageUnavailableException(
                $"无法确认上传任务所在磁盘：{uploadRoot}");
        }

        string volumeKey = VolumeKey(volume);
        string physicalDiskKey = volume.PhysicalDiskKey;
        if ((!string.IsNullOrWhiteSpace(state.UploadVolumeId)
             && !string.Equals(
                 state.UploadVolumeId,
                 volumeKey,
                 StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(state.UploadPhysicalDiskId)
                && !string.Equals(
                    state.UploadPhysicalDiskId,
                    physicalDiskKey,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new MobileBackupStorageUnavailableException(
                $"上传任务原保存磁盘已变化：{uploadRoot}");
        }

        state.UploadRoot = Path.GetFullPath(uploadRoot);
        state.UploadVolumeId = volumeKey;
        state.UploadPhysicalDiskId = physicalDiskKey;
    }

    internal string ResolveUploadRootForExistingState(
        string uploadId,
        MobileBackupUploadState state)
    {
        if (!string.IsNullOrWhiteSpace(state.UploadRoot))
        {
            string storedRoot = ResolveStoredUploadRoot(state);
            if (ResolveRoots().Contains(
                    storedRoot,
                    StringComparer.OrdinalIgnoreCase))
            {
                return storedRoot;
            }
            throw new MobileBackupStorageUnavailableException(
                $"上传任务原保存磁盘不可用：{storedRoot}");
        }

        foreach (string root in ResolveRoots())
        {
            if (File.Exists(PartPath(uploadId, root)))
                return root;
        }
        return "";
    }

    internal string EnsureStagingDirectory(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new MobileBackupStorageUnavailableException(
                "录像保存位置为空，无法接收备份");

        string directory = Path.Combine(
            Path.GetFullPath(root),
            MobileBackupService.StagingDirectoryName);
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            throw new MobileBackupStorageUnavailableException(
                $"无法在 {root} 下创建暂存目录：{ex.Message}");
        }
        return directory;
    }

    internal string PartPath(
        string uploadId,
        MobileBackupUploadState? state = null)
    {
        string legacy = Path.Combine(_stateDirectory, $"{uploadId}.part");
        if (state?.UsesLegacyPart == true && File.Exists(legacy))
            return legacy;
        if (!string.IsNullOrWhiteSpace(state?.UploadRoot))
            return PartPath(uploadId, state.UploadRoot);

        foreach (string root in ResolveRoots())
        {
            string candidate = PartPath(uploadId, root);
            if (File.Exists(candidate))
                return candidate;
        }
        return legacy;
    }

    internal bool MigrateLegacyPart(
        string uploadId,
        MobileBackupUploadState state,
        string uploadRoot)
    {
        string legacy = Path.Combine(_stateDirectory, $"{uploadId}.part");
        if (!File.Exists(legacy))
        {
            state.UsesLegacyPart = false;
            return false;
        }

        string target = PartPath(uploadId, uploadRoot);
        _ = EnsureStagingDirectory(uploadRoot);
        if (File.Exists(target))
        {
            if (new FileInfo(target).Length != new FileInfo(legacy).Length
                || !string.Equals(
                    ComputeFileSha256(target),
                    ComputeFileSha256(legacy),
                    StringComparison.Ordinal))
            {
                throw new MobileBackupStorageUnavailableException(
                    "旧版暂存分片与目标磁盘上的分片不一致，已停止续传");
            }
            File.Delete(legacy);
            state.UsesLegacyPart = false;
            return true;
        }

        string temporary = $"{target}.{Guid.NewGuid():N}.legacy-migrating";
        try
        {
            File.Copy(legacy, temporary, overwrite: false);
            using (var stream = new FileStream(
                       temporary,
                       FileMode.Open,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target);
            File.Delete(legacy);
            state.UsesLegacyPart = false;
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            TryDelete(temporary);
            throw new MobileBackupStorageUnavailableException(
                $"旧版暂存分片迁移失败，已保留原文件：{ex.Message}");
        }
    }

    internal IEnumerable<string> EnumeratePartDirectories()
    {
        IReadOnlyList<string> roots;
        try
        {
            roots = ResolveRoots();
        }
        catch (MobileBackupStorageUnavailableException)
        {
            roots = [];
        }
        foreach (string root in roots)
        {
            yield return Path.Combine(
                root,
                MobileBackupService.StagingDirectoryName);
        }
        yield return _stateDirectory;
    }

    internal string ReserveNewUploadOnBestDisk(
        string uploadId,
        string? deviceId,
        long totalBytes,
        long receivedBytes)
    {
        var candidates = new List<UploadRootCandidate>();
        foreach (string root in ResolveRoots())
        {
            if (_storageVolumeProbe(root) is not StorageVolumeInfo volume)
                continue;
            string volumeKey = VolumeKey(volume);
            string physicalDiskKey = volume.PhysicalDiskKey;
            long reservedBytes = _storageAdmission.GetReservedBytes(volumeKey);
            long reserveBytes = StorageSpacePolicy.CalculateMinimumReserveBytes(volume);
            long rawUsableBytes = Math.Max(
                0,
                volume.AvailableFreeSpace - reserveBytes);
            long usableBytes = Math.Max(0, rawUsableBytes - reservedBytes);
            HostUploadStorageWriteLoad load =
                _storageAdmission.GetWriteLoad(physicalDiskKey, root);
            double pressure = (load.ActiveWrites + load.WaitingWriters + 1d)
                / Math.Max(1, load.ConcurrencyLimit);
            candidates.Add(new UploadRootCandidate(
                root,
                volumeKey,
                physicalDiskKey,
                usableBytes,
                rawUsableBytes,
                pressure));
        }

        Exception? lastUnavailable = null;
        HostUploadStorageBusyException? lastBusy = null;
        if (candidates.Count == 0
            || candidates.All(item => item.RawUsableBytes <= 0))
        {
            throw new MobileBackupStorageUnavailableException(
                "录像保存磁盘空间不足，无法接收新的上传任务");
        }
        if (candidates.All(item => item.UsableBytes <= 0))
            throw new HostUploadStorageBusyException(2);
        var orderedGroups = candidates
            .GroupBy(item => item.PhysicalDiskKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                long reservedOnDisk = group
                    .Sum(item => _storageAdmission.GetReservedBytes(item.VolumeKey));
                long rawCapacity = group.Sum(item => item.RawUsableBytes);
                return new
                {
                    Candidates = group
                        .OrderByDescending(item => item.UsableBytes)
                        .ThenBy(item => item.Root, StringComparer.OrdinalIgnoreCase),
                    Pressure = group.Min(item => item.Pressure),
                    RemainingBytes = Math.Max(0, rawCapacity - reservedOnDisk),
                    ReservedRatio = rawCapacity <= 0
                        ? 1
                        : reservedOnDisk / (double)rawCapacity,
                    PhysicalDiskKey = group.Key
                };
            })
            .OrderBy(group => group.Pressure)
            .ThenBy(group => group.ReservedRatio)
            .ThenByDescending(group => group.RemainingBytes)
            .ThenBy(group => group.PhysicalDiskKey, StringComparer.OrdinalIgnoreCase);

        IEnumerable<UploadRootCandidate> candidateOrder = orderedGroups
            .Select(group => group.Candidates.FirstOrDefault(
                item => item.UsableBytes > 0))
            .Where(item => item != null)
            .Cast<UploadRootCandidate>()
            .Concat(orderedGroups
                .SelectMany(group => group.Candidates.Skip(1))
                .OrderByDescending(item => item.UsableBytes)
                .ThenBy(item => item.Root, StringComparer.OrdinalIgnoreCase));
        foreach (UploadRootCandidate candidate in candidateOrder)
        {
            if (candidate.UsableBytes <= 0)
                continue;
            try
            {
                ReserveAdmissionSpace(
                    uploadId,
                    deviceId,
                    candidate.Root,
                    totalBytes,
                    receivedBytes);
                try
                {
                    _ = EnsureStagingDirectory(candidate.Root);
                }
                catch
                {
                    _storageAdmission.ReleaseUpload(uploadId);
                    throw;
                }
                return candidate.Root;
            }
            catch (HostUploadStorageBusyException busy)
            {
                lastBusy = busy;
            }
            catch (MobileBackupStorageUnavailableException unavailable)
            {
                lastUnavailable = unavailable;
            }
        }

        if (lastBusy != null)
            throw lastBusy;
        throw lastUnavailable as MobileBackupStorageUnavailableException
            ?? new MobileBackupStorageUnavailableException(
                "没有可用的录像保存磁盘，无法接收新的上传任务");
    }

    internal string ReserveAdmissionSpace(
        string uploadId,
        string? deviceId,
        string path,
        long totalBytes,
        long receivedBytes)
    {
        if (_storageVolumeProbe(path) is not StorageVolumeInfo volume)
        {
            throw new MobileBackupStorageUnavailableException(
                "无法读取录像存储卷的可用空间");
        }

        string volumeKey = VolumeKey(volume);
        long reserveBytes = StorageSpacePolicy.CalculateMinimumReserveBytes(volume);
        _storageAdmission.ReserveUpload(
            uploadId,
            string.IsNullOrWhiteSpace(deviceId) ? uploadId : deviceId,
            volumeKey,
            totalBytes,
            receivedBytes,
            volume.AvailableFreeSpace,
            reserveBytes,
            volume.RootPath,
            volume.PhysicalDiskKey);
        return volumeKey;
    }

    internal HostUploadStorageAvailability GetStorageAvailability()
    {
        try
        {
            long reservedBytes = 0;
            bool hasCapacity = false;
            bool hasBusyReservations = false;
            foreach (string root in ResolveRoots())
            {
                if (_storageVolumeProbe(root) is not StorageVolumeInfo volume)
                    continue;
                string volumeKey = VolumeKey(volume);
                long volumeReserved = _storageAdmission.GetReservedBytes(volumeKey);
                reservedBytes += volumeReserved;
                long reserveBytes = StorageSpacePolicy.CalculateMinimumReserveBytes(volume);
                if (volume.AvailableFreeSpace <= reserveBytes)
                    continue;
                if (volume.AvailableFreeSpace - reserveBytes - volumeReserved > 0)
                    hasCapacity = true;
                else
                    hasBusyReservations = true;
            }

            if (hasCapacity)
            {
                return new HostUploadStorageAvailability(
                    true,
                    "available",
                    0,
                    reservedBytes);
            }
            if (hasBusyReservations)
            {
                return new HostUploadStorageAvailability(
                    false,
                    "write_busy",
                    2,
                    reservedBytes);
            }
            return new HostUploadStorageAvailability(
                false,
                "storage_unavailable",
                15,
                reservedBytes);
        }
        catch
        {
            return new HostUploadStorageAvailability(
                false,
                "storage_unavailable",
                15,
                0);
        }
    }

    internal void ReleaseUpload(string uploadId) =>
        _storageAdmission.ReleaseUpload(uploadId);

    internal HostUploadWriteLease AcquireWrite(
        string uploadId,
        string deviceId,
        string volumeKey) =>
        _storageAdmission.AcquireWrite(uploadId, deviceId, volumeKey);

    private static string PartPath(string uploadId, string root) =>
        Path.Combine(
            root,
            MobileBackupService.StagingDirectoryName,
            $"{uploadId}.part");

    private static string VolumeKey(StorageVolumeInfo volume) =>
        string.IsNullOrWhiteSpace(volume.VolumeId)
            ? volume.RootPath
            : volume.VolumeId;

    private static string ComputeFileSha256(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}

internal sealed record UploadRootCandidate(
    string Root,
    string VolumeKey,
    string PhysicalDiskKey,
    long UsableBytes,
    long RawUsableBytes,
    double Pressure);
