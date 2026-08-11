using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Logging;
using System.IO;

namespace ExpressPackingMonitoring.Services;

/// <summary>手动清理模式：按时间清理或按空间释放。</summary>
public enum ManualCleanupKind
{
    ByTime,
    BySpace
}

/// <summary>
/// 手动清理参数：ByTime 使用 Cutoff（结束时间早于该时间）；BySpace 使用 TargetBytes。
/// </summary>
public sealed record ManualCleanupOptions(
    ManualCleanupKind Kind,
    DateTime Cutoff,
    long TargetBytes);

/// <summary>未备份录像询问信息：条数与按数据库记录估算的字节数。</summary>
public sealed record ManualCleanupPrompt(
    int UnarchivedCount,
    long UnarchivedBytes);

/// <summary>手动清理结果汇总。</summary>
public sealed record ManualCleanupResult(
    int CleanedCount,
    long CleanedBytes,
    int RepairedCount,
    int SkippedCount,
    int UnarchivedRemainingCount,
    bool TargetReached,
    bool HasUnarchivedOlderThanCutoff);

/// <summary>
/// 设置页“录像清理”的后台服务：两阶段执行。
/// 第一阶段只清理已确认备份（Verified）的本地副本；
/// 空间模式未达到释放目标、或时间模式存在超过截止日期的未备份录像时，
/// 通过 unarchivedDecider 询问用户，确认后按 Failed → Pending → LocalOnly 清理未备份录像。
/// 所有清理都保留数据库记录并标记 LocalDeleted，NAS 文件永不删除。
/// </summary>
internal sealed class ManualCleanupService
{
    private const string CleanupReason = "手动清理本地录像";
    private const string MissingFileRepairReason = "本地文件已缺失，状态自动修复";
    private static readonly string[] UnarchivedTiers =
    [
        VideoArchiveStatus.Failed,
        VideoArchiveStatus.Pending,
        VideoArchiveStatus.LocalOnly
    ];

    private readonly VideoDatabase _database;

    public ManualCleanupService(VideoDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public ManualCleanupPreview Preview(
        ManualCleanupOptions options,
        IReadOnlyList<string> rootPaths) =>
        _database.GetManualCleanupPreview(
            GetEffectiveCutoff(options),
            NormalizeRoots(rootPaths));

    public ManualCleanupResult Run(
        ManualCleanupOptions options,
        IReadOnlyList<string> rootPaths,
        Func<ManualCleanupPrompt, bool>? unarchivedDecider)
    {
        DateTime cutoff = GetEffectiveCutoff(options);
        IReadOnlyList<string> roots = NormalizeRoots(rootPaths);
        int cleanedCount = 0;
        long cleanedBytes = 0;
        int repairedCount = 0;
        int skippedCount = 0;

        // 第一阶段：只清理已确认备份的本地副本。
        foreach (VideoRecord record in _database.GetManualCleanupCandidates(
                     cutoff,
                     roots,
                     500,
                     VideoArchiveStatus.Verified))
        {
            if (TryCleanLocalCopy(record, out long bytes, out bool skipped, out bool repaired))
            {
                cleanedCount++;
                cleanedBytes += bytes;
            }
            else if (repaired)
            {
                repairedCount++;
            }
            else if (skipped)
            {
                skippedCount++;
            }
        }

        IReadOnlyList<VideoRecord> unarchived = GetUnarchivedCandidates(
            cutoff,
            roots);
        bool targetReached = options.Kind == ManualCleanupKind.BySpace
            && cleanedBytes >= options.TargetBytes;
        bool needDecide = options.Kind switch
        {
            ManualCleanupKind.BySpace => !targetReached && unarchived.Count > 0,
            ManualCleanupKind.ByTime => unarchived.Count > 0,
            _ => false
        };

        if (needDecide
            && (unarchivedDecider?.Invoke(new ManualCleanupPrompt(
                    unarchived.Count,
                    unarchived.Sum(record => Math.Max(0, record.FileSizeBytes))))
                ?? false))
        {
            foreach (string tier in UnarchivedTiers)
            {
                foreach (VideoRecord record in _database.GetManualCleanupCandidates(
                             cutoff,
                             roots,
                             200,
                             tier))
                {
                    if (TryCleanLocalCopy(
                            record,
                            out long bytes,
                            out bool skipped,
                            out bool repaired))
                    {
                        cleanedCount++;
                        cleanedBytes += bytes;
                    }
                    else if (repaired)
                    {
                        repairedCount++;
                    }
                    else if (skipped)
                    {
                        skippedCount++;
                    }
                }
            }
        }

        int unarchivedRemaining = needDecide
            ? GetUnarchivedCandidates(cutoff, roots).Count
            : unarchived.Count;
        return new ManualCleanupResult(
            cleanedCount,
            cleanedBytes,
            repairedCount,
            skippedCount,
            unarchivedRemaining,
            targetReached,
            unarchived.Count > 0);
    }

    private IReadOnlyList<VideoRecord> GetUnarchivedCandidates(
        DateTime cutoff,
        IReadOnlyList<string> roots)
    {
        var result = new List<VideoRecord>();
        foreach (string tier in UnarchivedTiers)
            result.AddRange(_database.GetManualCleanupCandidates(cutoff, roots, 200, tier));
        return result;
    }

    private static DateTime GetEffectiveCutoff(ManualCleanupOptions options) =>
        options.Kind == ManualCleanupKind.BySpace
            ? DateTime.MaxValue
            : options.Cutoff;

    /// <summary>
    /// 清理单条本地录像：删除文件并标记 LocalDeleted（记录保留）。
    /// 已备份记录先做远端确认；本地文件缺失时改为状态修复。
    /// </summary>
    private bool TryCleanLocalCopy(
        VideoRecord record,
        out long bytes,
        out bool skipped,
        out bool repaired)
    {
        bytes = 0;
        skipped = false;
        repaired = false;
        try
        {
            if (string.IsNullOrWhiteSpace(record.FilePath))
            {
                skipped = true;
                return false;
            }
            if (!File.Exists(record.FilePath))
            {
                _database.ReconcileMissingLocalFile(
                    record.Id,
                    MissingFileRepairReason,
                    RecordingDeletionReasonCode.ManualCleanup);
                repaired = true;
                return false;
            }
            if (record.ArchiveStatus == VideoArchiveStatus.Verified
                && !IsRemoteConfirmed(record))
            {
                skipped = true;
                return false;
            }

            long size = new FileInfo(record.FilePath).Length;
            using (VideoLifecycleCoordinator.EnterAsync(
                       record.Id,
                       CancellationToken.None).GetAwaiter().GetResult())
            {
                if (!File.Exists(record.FilePath))
                {
                    _database.ReconcileMissingLocalFile(
                        record.Id,
                        MissingFileRepairReason,
                        RecordingDeletionReasonCode.ManualCleanup);
                    repaired = true;
                    return false;
                }
                File.Delete(record.FilePath);
                _database.MarkLocalCopyDeleted(
                    record.Id,
                    CleanupReason,
                    RecordingDeletionReasonCode.ManualCleanup);
            }
            bytes = size;
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(
                "ManualCleanup",
                $"Manual cleanup skipped id={record.Id}, error={ex.Message}");
            skipped = true;
            return false;
        }
    }

    private bool IsRemoteConfirmed(VideoRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.ArchivePath))
            return false;
        if (!LocalCopyCleanupPolicy.ShouldProbeBeforeLocalCleanup(
                record,
                DateTime.Now))
        {
            return true;
        }
        if (!RemoteFileProbe.TryProbeFileWithSize(
                record.ArchivePath,
                record.FileSizeBytes,
                TimeSpan.FromSeconds(3)))
        {
            return false;
        }
        _database.UpdateLastArchiveProbeAt(record.Id, DateTime.Now);
        return true;
    }

    private static IReadOnlyList<string> NormalizeRoots(IReadOnlyList<string> rootPaths)
    {
        var result = new List<string>();
        foreach (string path in rootPaths)
        {
            try
            {
                string full = Path.GetFullPath(path);
                if (!full.EndsWith(Path.DirectorySeparatorChar))
                    full += Path.DirectorySeparatorChar;
                result.Add(full);
            }
            catch
            {
            }
        }
        return result;
    }
}
