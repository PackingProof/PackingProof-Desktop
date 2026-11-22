using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;

namespace ExpressPackingMonitoring.Services;

internal sealed class VideoClipCompressionRunner
{
    private readonly VideoCompressionService _compressionService;
    private readonly Func<IReadOnlyList<string>> _validatedEncodersProvider;

    internal VideoClipCompressionRunner(
        FfmpegWorkLimiter ffmpegWorkLimiter,
        Func<IReadOnlyList<string>> validatedEncodersProvider)
    {
        _compressionService = new VideoCompressionService(
            AppPaths.FindFFmpeg,
            ffmpegWorkLimiter);
        _validatedEncodersProvider = validatedEncodersProvider ?? (() => []);
    }

    internal ClipExportInfo GetExportInfo(
        VideoRecord record,
        string sourcePath,
        double durationSeconds)
    {
        return new ClipExportInfo
        {
            DurationSeconds = durationSeconds,
            FileSizeBytes = File.Exists(sourcePath)
                ? new FileInfo(sourcePath).Length
                : Math.Max(0, record.FileSizeBytes),
            VideoCodec = record.VideoCodec ?? "",
            Av1Available = ResolveAv1Encoder().Length > 0
        };
    }

    internal string ResolveAv1Encoder()
    {
        IReadOnlyList<string> validatedEncoders;
        try
        {
            validatedEncoders = _validatedEncodersProvider() ?? [];
        }
        catch
        {
            validatedEncoders = [];
        }
        return VideoCompressionPolicy.ResolveValidatedAv1Encoder(validatedEncoders);
    }

    internal static void ValidateTarget(
        VideoRecord record,
        string sourcePath,
        double sourceDurationSeconds,
        double startSeconds,
        double endSeconds,
        ClipOutputOptions options)
    {
        if (options.Mode != ClipOutputMode.Compressed)
            return;

        long sourceBytes = File.Exists(sourcePath)
            ? new FileInfo(sourcePath).Length
            : Math.Max(0, record.FileSizeBytes);
        double rangeDurationSeconds = endSeconds - startSeconds;
        long rangeSourceBytes = CalculateRangeSourceBytes(
            sourceBytes,
            sourceDurationSeconds,
            rangeDurationSeconds);
        if (VideoCompressionPolicy.TryValidateTarget(
                rangeSourceBytes,
                options.TargetBytes,
                rangeDurationSeconds,
                out VideoCompressionFailureKind failure))
        {
            return;
        }

        throw new ArgumentException(GetFailureMessage(failure));
    }

    internal Task<VideoCompressionResult> CompressAsync(
        VideoRecord record,
        string inputPath,
        string outputPath,
        double startSeconds,
        double durationSeconds,
        ClipOutputOptions options,
        IProgress<VideoCompressionProgress> progress,
        CancellationToken cancellationToken)
    {
        long sourceBytes = File.Exists(inputPath)
            ? new FileInfo(inputPath).Length
            : Math.Max(0, record.FileSizeBytes);
        long rangeSourceBytes = CalculateRangeSourceBytes(
            sourceBytes,
            record.DurationSeconds > 0 ? record.DurationSeconds : durationSeconds,
            durationSeconds);
        string av1Encoder = options.CodecMode == VideoCompressionCodecMode.Av1
            ? ResolveAv1Encoder()
            : "";
        var source = new VideoCompressionSource(
            record.OrderId ?? "",
            Path.GetFileName(inputPath),
            inputPath,
            record.VideoCodec ?? "",
            durationSeconds,
            sourceBytes,
            startSeconds,
            rangeSourceBytes);
        return _compressionService.CompressAsync(
            new VideoCompressionRequest(
                source,
                outputPath,
                options.TargetBytes,
                options.CodecMode,
                av1Encoder),
            progress,
            cancellationToken);
    }

    internal static long CalculateRangeSourceBytes(
        long sourceBytes,
        double sourceDurationSeconds,
        double rangeDurationSeconds)
    {
        if (sourceBytes <= 0
            || sourceDurationSeconds <= 0
            || rangeDurationSeconds <= 0)
        {
            return 0;
        }

        return Math.Max(
            0,
            (long)Math.Floor(
                sourceBytes
                * Math.Min(rangeDurationSeconds, sourceDurationSeconds)
                / sourceDurationSeconds));
    }

    private static string GetFailureMessage(VideoCompressionFailureKind failure) =>
        failure switch
        {
            VideoCompressionFailureKind.SourceTooSmall =>
                "当前片段已很小，无需压缩",
            VideoCompressionFailureKind.TargetTooLarge =>
                "目标大小不能超过当前片段",
            VideoCompressionFailureKind.TargetTooSmall =>
                "目标大小太小，无法生成可播放的视频",
            _ => "目标大小无效"
        };
}
