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

            return message.Contains("未找到 FFmpeg", StringComparison.OrdinalIgnoreCase)
                || message.Contains("转换超时", StringComparison.OrdinalIgnoreCase)
                || message.Contains("文件句柄", StringComparison.OrdinalIgnoreCase)
                || message.Contains("同一录像已有转换", StringComparison.OrdinalIgnoreCase)
                || message.Contains("正在删除、备份或清理", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Unable to flush", StringComparison.OrdinalIgnoreCase)
                || message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase);
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
