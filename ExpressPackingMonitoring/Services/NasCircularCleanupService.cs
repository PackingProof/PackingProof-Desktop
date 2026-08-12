using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Logging;
using System.Collections.Concurrent;
using System.IO;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// NAS 容量循环清理与归档缺失对账服务。
/// 空间判断只读取 NAS 真实文件系统（StorageVolumeInfo.TryGet），不做文件探测；
/// 只有准备删除某条候选时才对该 ArchivePath 做一次三态探测；
/// 每删除一条后重新读取真实可用空间作为停止条件，回读失败立即停止本轮。
/// 同一网络根目录同一时刻只允许一个清理循环。
/// </summary>
internal sealed class NasCircularCleanupService
{
    /// <summary>单轮清理条数上限：达到上限仅结束本轮，下一次空间检查继续。</summary>
    internal const int BatchLimit = 200;

    private const string ReconcileReason =
        "NAS 归档文件不存在，检测到外部删除或缺失（非本程序删除）";
    private const string NasCleanupReason = "NAS 容量循环清理";

    private readonly VideoDatabase _database;
    private readonly Func<string, StorageVolumeInfo?> _volumeReader;
    private readonly Func<IArchiveProvider> _providerFactory;
    private readonly Func<string, long, TimeSpan, RemoteFileProbe.FileProbeState> _probe;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _rootLocks =
        new(StringComparer.OrdinalIgnoreCase);

    internal NasCircularCleanupService(
        VideoDatabase database,
        Func<string, StorageVolumeInfo?>? volumeReader = null,
        Func<IArchiveProvider>? providerFactory = null,
        Func<string, long, TimeSpan, RemoteFileProbe.FileProbeState>? probe = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _volumeReader = volumeReader
            ?? (path => StorageVolumeInfo.TryGet(path, out StorageVolumeInfo volume)
                ? volume
                : null);
        _providerFactory = providerFactory ?? (() => new NasArchiveProvider());
        _probe = probe ?? RemoteFileProbe.TryProbeFileState;
    }

    /// <summary>
    /// 对某个网络根目录执行一轮循环清理：真实卷空间 ≤ 预留值时，按最旧优先删除已确认归档的
    /// Verified/LocalDeleted 候选，直到空间恢复到预留值以上、候选耗尽、达到单轮上限或空间读取失败。
    /// 返回本轮是否执行过删除。
    /// </summary>
    internal bool RunForRoot(string root, long reserveBytes)
    {
        if (string.IsNullOrWhiteSpace(root) || reserveBytes < 0)
            return false;

        SemaphoreSlim gate = _rootLocks.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0))
            return false; // 同一根已有清理循环在运行
        bool deletedAny = false;
        try
        {
            int deletedCount = 0;
            while (deletedCount < BatchLimit)
            {
                StorageVolumeInfo? volume = _volumeReader(root);
                if (volume == null)
                    return deletedAny; // 空间无法读取 → 停止本轮，绝不猜测
                if (volume.Value.AvailableFreeSpace > reserveBytes)
                    return deletedAny; // 空间已恢复

                IReadOnlyList<VideoRecord> candidates =
                    _database.GetNasCleanupCandidates(root, BatchLimit);
                if (candidates.Count == 0)
                    return deletedAny;

                bool progressed = false;
                foreach (VideoRecord record in candidates)
                {
                    if (deletedCount >= BatchLimit)
                        return deletedAny;
                    if (!TryCleanOneRecord(record, root, out bool deleted))
                        continue;
                    progressed = true;
                    if (deleted)
                    {
                        deletedAny = true;
                        deletedCount++;
                        StorageVolumeInfo? afterDelete = _volumeReader(root);
                        if (afterDelete == null)
                            return deletedAny; // 回读失败 → 停止本轮
                        if (afterDelete.Value.AvailableFreeSpace > reserveBytes)
                            return deletedAny;
                    }
                }

                if (!progressed)
                    return deletedAny; // 本轮候选全部跳过（探测不可达/不可删）→ 结束本轮
            }
            return deletedAny;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(
                "Cleanup",
                $"NAS circular cleanup failed root={root}, error={ex.Message}");
            return deletedAny;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 处理单条 NAS 清理候选：在所有权锁内复查状态与路径，三态探测；
    /// Exists → 删除远端副本并标记；ConfirmedMissing → 对账修复；Unavailable → 跳过。
    /// </summary>
    private bool TryCleanOneRecord(
        VideoRecord snapshot,
        string root,
        out bool deleted)
    {
        deleted = false;
        try
        {
            using (VideoLifecycleCoordinator.EnterAsync(
                       snapshot.Id,
                       CancellationToken.None).GetAwaiter().GetResult())
            {
                VideoRecord? record = _database.GetVideoById(snapshot.Id);
                if (record == null
                    || record.IsDeleted
                    || record.ArchiveCompletedAt == null
                    || string.IsNullOrWhiteSpace(record.ArchivePath)
                    || record.ArchiveStatus is not (
                        VideoArchiveStatus.Verified
                        or VideoArchiveStatus.LocalDeleted))
                {
                    return false;
                }
                if (!IsPathUnderRoot(record.ArchivePath, root))
                    return false;

                switch (_probe(
                            record.ArchivePath,
                            record.FileSizeBytes,
                            TimeSpan.FromSeconds(3)))
                {
                    case RemoteFileProbe.FileProbeState.Unavailable:
                        return false; // 不可判断：保持原状态，下轮再试
                    case RemoteFileProbe.FileProbeState.ConfirmedMissing:
                        ReconcileRecordCore(record);
                        return true; // 已修复状态（NasDeleted 或 IsDeleted）
                }

                IArchiveProvider provider = _providerFactory();
                IArchiveProvider.DeleteOutcome outcome = provider.DeleteAsync(
                        record.ArchivePath,
                        new[] { root },
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                if (outcome == IArchiveProvider.DeleteOutcome.NotFound)
                {
                    // 删除时明确不存在：按已删除处理并写库
                    _database.MarkNasCopyDeleted(
                        record.Id,
                        record.ArchivePath,
                        NasCleanupReason,
                        RecordingDeletionReasonCode.NasCapacityCleanup);
                }
                else
                {
                    _database.MarkNasCopyDeleted(
                        record.Id,
                        record.ArchivePath,
                        NasCleanupReason,
                        RecordingDeletionReasonCode.NasCapacityCleanup);
                }
                deleted = true;
                return true;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(
                "Cleanup",
                $"NAS cleanup candidate skipped id={snapshot.Id}, error={ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 低频对账扫描：只处理明确不存在的归档（ConfirmedMissing）；
    /// 不可达保持原状态，存在则更新探测缓存。调用方负责频率控制（启动首次 + 每日一次）。
    /// </summary>
    internal int RunReconcileBatch(int limit = 200)
    {
        DateTime staleBefore = DateTime.Now
            - LocalCopyCleanupPolicy.UnconfirmedRemoteCleanupGrace;
        int repaired = 0;
        foreach (VideoRecord record in _database.GetReconcileCandidates(
                     staleBefore,
                     limit))
        {
            try
            {
                switch (_probe(
                            record.ArchivePath,
                            record.FileSizeBytes,
                            TimeSpan.FromSeconds(3)))
                {
                    case RemoteFileProbe.FileProbeState.Exists:
                        _database.UpdateLastArchiveProbeAt(record.Id, DateTime.Now);
                        break;
                    case RemoteFileProbe.FileProbeState.ConfirmedMissing:
                        if (ReconcileRecordWithLock(record.Id))
                            repaired++;
                        break;
                    case RemoteFileProbe.FileProbeState.Unavailable:
                        break; // 不可判断：不改状态、不更新缓存
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn(
                    "Cleanup",
                    $"NAS reconcile candidate skipped id={record.Id}, error={ex.Message}");
            }
        }
        return repaired;
    }

    /// <summary>带所有权锁的缺失对账入口（供低频扫描等外部触发使用）。</summary>
    internal bool ReconcileRecordWithLock(long recordId)
    {
        using (VideoLifecycleCoordinator.EnterAsync(
                   recordId,
                   CancellationToken.None).GetAwaiter().GetResult())
        {
            VideoRecord? record = _database.GetVideoById(recordId);
            return record != null && ReconcileRecordCore(record);
        }
    }

    /// <summary>
    /// 对账核心：仅在记录仍为 Verified/LocalDeleted、归档确认成功时，
    /// 按本地文件是否存在修复为 NasDeleted 或 IsDeleted=1；调用方必须已持有所有权锁。
    /// </summary>
    internal bool ReconcileRecordCore(VideoRecord record)
    {
        if (record == null
            || record.IsDeleted
            || record.ArchiveCompletedAt == null
            || string.IsNullOrWhiteSpace(record.ArchivePath)
            || record.ArchiveStatus is not (
                VideoArchiveStatus.Verified
                or VideoArchiveStatus.LocalDeleted))
        {
            return false;
        }

        _database.MarkNasCopyDeleted(
            record.Id,
            record.ArchivePath,
            ReconcileReason,
            RecordingDeletionReasonCode.NasCopyMissingReconcile);
        return true;
    }

    /// <summary>判断归档路径是否属于指定网络根目录（规范化、忽略大小写）。</summary>
    internal static bool IsPathUnderRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            return false;
        try
        {
            string fullPath = Path.GetFullPath(path);
            string fullRoot = Path.GetFullPath(root).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            return fullPath.StartsWith(
                fullRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
