#nullable disable
using ExpressPackingMonitoring.Config;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace ExpressPackingMonitoring.Services
{
    public sealed class ClipRangeRequest
    {
        public double StartSeconds { get; set; }
        public double EndSeconds { get; set; }
        public string Mode { get; set; } = "original";
        public long TargetBytes { get; set; }
        public string Codec { get; set; } = "";
        public double Seconds { get; set; }
        public string PreviewSide { get; set; } = "";
        public int FrameCount { get; set; }
        public int FrameIndex { get; set; } = -1;
    }

    internal enum ClipOutputMode
    {
        Original,
        Compressed
    }

    internal sealed record ClipOutputOptions(
        ClipOutputMode Mode,
        long TargetBytes = 0,
        VideoCompressionCodecMode CodecMode = VideoCompressionCodecMode.H264);

    internal sealed class ClipExportInfo
    {
        public double DurationSeconds { get; set; }
        public long FileSizeBytes { get; set; }
        public string VideoCodec { get; set; } = "";
        public bool Av1Available { get; set; }
    }

    internal sealed class ClipTaskState
    {
        public string TaskId { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
        public string OutputPath { get; set; }
        public string DownloadUrl { get; set; }
        public string PlayUrl { get; set; }
        public ClipOutputOptions Options { get; set; }
        public string Stage { get; set; } = "queued";
        public int ProgressPercent { get; set; }
        public bool CancelRequested { get; set; }
        public Process Process { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public object Sync { get; } = new();
    }

    internal readonly record struct ClipFFmpegResult(
        bool Success,
        int ExitCode,
        string Stderr);

    internal static class VideoClipFilePolicy
    {
        internal static string SanitizeFileName(string fileName)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(c, '_');
            return fileName;
        }

        internal static string GetTempClipPath(string outputPath)
        {
            return Path.Combine(
                Path.GetDirectoryName(outputPath) ?? AppPaths.ClipsDir,
                Path.GetFileNameWithoutExtension(outputPath) + ".tmp" + Path.GetExtension(outputPath));
        }

        internal static string BuildOutputFileName(
            string sourceName,
            string taskId,
            DateTime createdAt,
            ClipOutputOptions options)
        {
            string codecSuffix = options.Mode == ClipOutputMode.Compressed
                ? $"_{options.CodecMode switch
                {
                    VideoCompressionCodecMode.H265 => "H265",
                    VideoCompressionCodecMode.Av1 => "AV1",
                    _ => "H264"
                }}_compressed"
                : "";
            string taskToken = (taskId ?? "").Trim();
            if (taskToken.Length > 8)
                taskToken = taskToken[..8];
            return SanitizeFileName(
                $"{sourceName}_clip_{createdAt:yyyyMMdd_HHmmss}_{taskToken}{codecSuffix}.mp4");
        }
    }

    public sealed class ClipPreviewResult
    {
        public bool Success { get; set; }
        public double VideoDuration { get; set; }
        public double StartSeconds { get; set; }
        public double EndSeconds { get; set; }
        public double DurationSeconds { get; set; }
        public string StartPreviewUrl { get; set; }
        public string EndPreviewUrl { get; set; }
    }

    public sealed class ClipTaskSnapshot
    {
        public bool Success { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
        public string Stage { get; set; } = "";
        public int Progress { get; set; }
        public string DownloadUrl { get; set; } = "";
        public string PlayUrl { get; set; } = "";
    }

    public sealed class ClipTimelineResult
    {
        public bool Success { get; set; }
        public double VideoDuration { get; set; }
        public List<ClipTimelineFrame> Frames { get; set; } = new();
    }

    public sealed class ClipTimelineFrame
    {
        public int Index { get; set; }
        public double Seconds { get; set; }
        public string Url { get; set; } = "";
    }

    public sealed class ClipPreviewFrameResult
    {
        public bool Success { get; set; }
        public double VideoDuration { get; set; }
        public double Seconds { get; set; }
        public string Url { get; set; } = "";
    }
}
