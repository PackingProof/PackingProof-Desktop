namespace ExpressPackingMonitoring.Services;

internal sealed record RecordingSampleSource(
    long Id,
    string FilePath,
    DateTime StartTime,
    double DurationSeconds,
    string Mode = "",
    string StopReason = "")
{
    internal bool UsesSameCodeCapture =>
        RecordingSampleSelectionPolicy.IsSameCodeStopReason(StopReason);
}

internal sealed record RecordingSamplePick(
    RecordingSampleSource Source,
    RecordingSampleCapturePhase CapturePhase,
    double PositionRatio);

internal enum RecordingSampleCapturePhase
{
    Start,
    End
}

/// <summary>
/// 从历史录像中选择用于训练反馈的抽帧位置。
/// 优先按日期均匀覆盖；连续打包只取开头，同码停录交替取开头和结尾。
/// </summary>
internal static class RecordingSampleSelectionPolicy
{
    internal const int DefaultTargetCount = 200;
    internal const double CaptureWindowRatio = 0.25;
    internal const double MaxCaptureWindowSeconds = 15;

    internal static bool IsSameCodeStopReason(string? stopReason) =>
        stopReason?.StartsWith("同码停录", StringComparison.Ordinal) == true;

    internal static double GetCaptureWindowSeconds(double durationSeconds) =>
        Math.Min(
            Math.Max(0, durationSeconds) * CaptureWindowRatio,
            MaxCaptureWindowSeconds);

    internal static IReadOnlyList<RecordingSamplePick> Select(
        IReadOnlyList<RecordingSampleSource> sources,
        int targetCount,
        Random random,
        int maxCandidateCount = 0)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(random);
        if (targetCount <= 0)
            return [];

        List<RecordingSampleSource> eligible = sources
            .Where(source =>
                source.Id > 0
                && !string.IsNullOrWhiteSpace(source.FilePath)
                && source.DurationSeconds > 0)
            .OrderBy(source => source.StartTime)
            .ThenBy(source => source.Id)
            .ToList();
        if (eligible.Count == 0)
            return [];

        var byDate = eligible
            .GroupBy(source => source.StartTime.Date)
            .OrderBy(group => group.Key)
            .ToList();
        int candidateLimit = maxCandidateCount <= 0
            ? targetCount
            : Math.Max(targetCount, maxCandidateCount);
        candidateLimit = Math.Min(candidateLimit, eligible.Count);
        var selected = new List<RecordingSamplePick>(candidateLimit);
        var selectedIds = new HashSet<long>();
        int sameCodePickCount = 0;

        int datesToUse = Math.Min(targetCount, byDate.Count);
        var queuesByDate = new List<Queue<RecordingSampleSource>>(datesToUse);
        for (int index = 0; index < datesToUse; index++)
        {
            int dateIndex = datesToUse <= 1
                ? 0
                : (int)Math.Round(index * (byDate.Count - 1d) / (datesToUse - 1d));
            IGrouping<DateTime, RecordingSampleSource> day = byDate[dateIndex];
            var queue = new Queue<RecordingSampleSource>(
                day.OrderBy(_ => random.Next()));
            queuesByDate.Add(queue);
        }

        // 第一轮铺满全部选中日期，后续轮次用于某个录像抽帧失败时补位。
        while (selected.Count < candidateLimit)
        {
            bool added = false;
            foreach (Queue<RecordingSampleSource> queue in queuesByDate)
            {
                if (queue.Count == 0)
                    continue;
                RecordingSampleSource source = queue.Dequeue();
                Add(source, random);
                added = true;
                if (selected.Count >= candidateLimit)
                    break;
            }
            if (!added)
                break;
        }

        if (selected.Count < candidateLimit)
        {
            foreach (RecordingSampleSource source in eligible
                         .Where(source => !selectedIds.Contains(source.Id))
                         .OrderBy(_ => random.Next()))
            {
                Add(source, random);
                if (selected.Count >= candidateLimit)
                    break;
            }
        }

        return selected;

        void Add(RecordingSampleSource source, Random sourceRandom)
        {
            if (!selectedIds.Add(source.Id))
                return;
            RecordingSampleCapturePhase phase = RecordingSampleCapturePhase.Start;
            if (source.UsesSameCodeCapture)
            {
                phase = sameCodePickCount % 2 == 0
                    ? RecordingSampleCapturePhase.Start
                    : RecordingSampleCapturePhase.End;
                sameCodePickCount++;
            }
            double windowSeconds = GetCaptureWindowSeconds(source.DurationSeconds);
            double windowStart = phase == RecordingSampleCapturePhase.Start
                ? 0
                : Math.Max(0, source.DurationSeconds - windowSeconds);
            double positionSeconds = windowStart + sourceRandom.NextDouble() * windowSeconds;
            positionSeconds = Math.Clamp(
                positionSeconds,
                0,
                Math.Max(0, source.DurationSeconds - 0.05));
            selected.Add(new RecordingSamplePick(
                source,
                phase,
                positionSeconds / Math.Max(0.001, source.DurationSeconds)));
        }
    }
}
