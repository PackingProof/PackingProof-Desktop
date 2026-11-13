namespace ExpressPackingMonitoring.Data
{
    public sealed class MkvConversionFailureState
    {
        public string FilePath { get; init; } = "";
        public DateTime? FirstFailedAt { get; init; }
        public DateTime? LastAttemptAt { get; init; }
        public int FailureCount { get; init; }
        public string LastError { get; init; } = "";
        public DateTime? LastNotifiedAt { get; init; }
    }

    public enum MkvAutomaticRetryDecision
    {
        Retry,
        Deferred,
        Suppressed
    }

    public static class MkvConversionRetryPolicy
    {
        private static readonly TimeSpan RecoverableRetryInterval = TimeSpan.FromMinutes(15);

        public static MkvAutomaticRetryDecision GetAutomaticRetryDecision(
            MkvConversionFailureState? state,
            DateTime now)
        {
            if (state?.FirstFailedAt == null)
                return MkvAutomaticRetryDecision.Retry;

            if (IsRecoverableFailure(state.LastError))
            {
                return state.LastAttemptAt != null
                    && now - state.LastAttemptAt.Value < RecoverableRetryInterval
                        ? MkvAutomaticRetryDecision.Deferred
                        : MkvAutomaticRetryDecision.Retry;
            }

            // 转换失败是确定性的文件/编码问题，后台不再按时间反复重试。
            // 维护工具仍可通过 forceRetry 明确发起人工重试。
            return MkvAutomaticRetryDecision.Suppressed;
        }

        private static bool IsRecoverableFailure(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            string normalized = message.Trim();
            return normalized is
                    "未找到 FFmpeg，无法转换"
                    or "MP4 转换超时"
                    or "同一录像已有转换在进行"
                    or "等待写入方释放文件句柄超时"
                    or "录像正在删除、备份或清理，请稍后重试"
                    or "现有 MP4 暂时无法刷盘，稍后重试"
                    or "MP4 暂时无法刷盘，稍后重试"
                || normalized.StartsWith(
                    "Unable to flush converted MP4 before deleting source",
                    StringComparison.OrdinalIgnoreCase);
        }

        public static bool ShouldNotify(MkvConversionFailureState? state, DateTime now)
        {
            if (state?.FirstFailedAt == null || state.LastNotifiedAt != null)
            {
                return false;
            }

            return true;
        }
    }

    public sealed class MkvBatchConversionResult
    {
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public int SkippedCount { get; set; }
        public int DeferredCount { get; set; }
        public int SuppressedCount { get; set; }
        public int NotificationCount { get; set; }
        public bool ShouldNotify => NotificationCount > 0;
        public List<string> ProcessedSources { get; } = [];
        public List<MkvFinalizedFile> FinalFiles { get; } = [];

        public void MarkProcessedSource(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)
                || ProcessedSources.Any(item =>
                    string.Equals(item, sourcePath, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            ProcessedSources.Add(sourcePath);
        }

        public void AddFinalFile(string sourcePath, string finalPath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)
                || string.IsNullOrWhiteSpace(finalPath)
                || FinalFiles.Any(item =>
                    string.Equals(item.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            FinalFiles.Add(new MkvFinalizedFile(sourcePath, finalPath));
        }
    }

    public sealed record MkvFinalizedFile(string SourcePath, string FinalPath);
}
