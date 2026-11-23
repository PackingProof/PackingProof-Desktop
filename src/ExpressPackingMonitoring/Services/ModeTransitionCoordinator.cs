namespace ExpressPackingMonitoring.Services;

internal sealed record ModeTransitionActivity(
    bool Recording,
    bool ActiveMobileBackups,
    int PendingRecordingUploads,
    bool ActiveClipTasks,
    bool ActiveMediaFinalization,
    bool PhotoCaptureInProgress,
    bool PhotoArchiveInProgress,
    bool RecordingStartInProgress,
    bool RecordingStopInProgress);

internal sealed record ModeTransitionAudit(
    string FromPreset,
    string ToPreset,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<string> Blockers,
    string Outcome)
{
    internal TimeSpan Duration => CompletedAt - StartedAt;
}

internal static class ModeTransitionPolicy
{
    internal static IReadOnlyList<string> FindBlockers(ModeTransitionActivity activity)
    {
        var blockers = new List<string>();
        if (activity.Recording)
            blockers.Add("recording");
        if (activity.ActiveMobileBackups)
            blockers.Add("mobile-backup");
        if (activity.PendingRecordingUploads > 0)
            blockers.Add("recording-upload");
        if (activity.ActiveClipTasks)
            blockers.Add("clip-task");
        if (activity.ActiveMediaFinalization)
            blockers.Add("media-finalization");
        if (activity.PhotoCaptureInProgress)
            blockers.Add("photo-capture");
        if (activity.PhotoArchiveInProgress)
            blockers.Add("photo-archive");
        if (activity.RecordingStartInProgress)
            blockers.Add("recording-start");
        if (activity.RecordingStopInProgress)
            blockers.Add("recording-stop");
        return blockers;
    }

    internal static string StatusText(IReadOnlyList<string> blockers) =>
        blockers.FirstOrDefault() switch
        {
            "recording" => "等待录像完成",
            "mobile-backup" => "等待备份完成",
            "recording-upload" => "等待上传完成",
            "clip-task" => "等待剪辑完成",
            "media-finalization" => "等待文件收尾",
            "photo-capture" => "等待拍照保存完成",
            "photo-archive" => "等待照片归档完成",
            "recording-start" => "等待录像启动完成",
            "recording-stop" => "等待录像收尾完成",
            _ => "正在切换"
        };
}

internal sealed class ModeTransitionGate
{
    private int _active;

    internal bool TryEnter() =>
        Interlocked.CompareExchange(ref _active, 1, 0) == 0;

    internal void Exit() =>
        Interlocked.Exchange(ref _active, 0);
}
