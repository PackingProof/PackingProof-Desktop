using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Logging;
using System.IO;

namespace ExpressPackingMonitoring.Services;

internal sealed record VideoDurationBackfillResult(
    int Candidates,
    int Updated,
    int Missing,
    int Failed);

/// <summary>
/// 修复“视频文件已经落盘，但数据库没来得及写停止时间和时长”的历史记录。
/// 常见触发是录制期间进程被强制关闭、崩溃或断电；文件本身仍然可读。
/// </summary>
internal sealed class VideoDurationBackfillService
{
    internal const int MaxRecordsPerRun = 100;
    internal static readonly TimeSpan MissingRetryInterval = TimeSpan.FromDays(1);

    private readonly VideoDatabase _database;
    private readonly Func<string, CompletedVideoMetadata?> _probe;

    internal VideoDurationBackfillService(
        VideoDatabase database,
        Func<string, CompletedVideoMetadata?>? probe = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _probe = probe ?? ProbeWithFfmpeg;
    }

    internal async Task<VideoDurationBackfillResult> RunOnceAsync(
        CancellationToken cancellationToken = default)
    {
        DateTime nowUtc = DateTime.UtcNow;
        IReadOnlyList<VideoDurationProbeCandidate> candidates = await Task.Run(
            () => _database.GetVideosNeedingDurationProbe(
                nowUtc - MissingRetryInterval,
                MaxRecordsPerRun),
            cancellationToken).ConfigureAwait(false);

        int updated = 0;
        int missing = 0;
        int failed = 0;
        foreach (VideoDurationProbeCandidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(
                () => _database.MarkVideoDurationProbeAttempt(candidate.RecordId, nowUtc),
                cancellationToken).ConfigureAwait(false);

            if (!File.Exists(candidate.FilePath))
            {
                missing++;
                continue;
            }

            CompletedVideoMetadata? metadata = await Task.Run(
                () => _probe(candidate.FilePath),
                cancellationToken).ConfigureAwait(false);
            if (metadata is not CompletedVideoMetadata value || value.DurationSeconds <= 0)
            {
                failed++;
                continue;
            }

            DateTime endTime = candidate.StartTime.AddSeconds(value.DurationSeconds);
            bool saved = await Task.Run(
                () => _database.UpdateVideoDurationAndEndTime(
                    candidate.RecordId,
                    value.DurationSeconds,
                    endTime),
                cancellationToken).ConfigureAwait(false);
            if (saved)
                updated++;
            else
                failed++;
        }

        if (updated > 0 || missing > 0 || failed > 0)
        {
            RuntimeLog.Info(
                "DurationBackfill",
                $"已完成历史录像时长修复：候选={candidates.Count}, 更新={updated}, 文件缺失={missing}, 失败={failed}");
        }
        return new VideoDurationBackfillResult(candidates.Count, updated, missing, failed);
    }

    private static CompletedVideoMetadata? ProbeWithFfmpeg(string filePath)
    {
        string ffmpegPath = AppPaths.FindFFmpeg();
        return CompletedVideoSpecificationProbe.TryRead(
            ffmpegPath,
            filePath,
            out CompletedVideoMetadata metadata)
            ? metadata
            : null;
    }
}
