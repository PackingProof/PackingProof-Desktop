using System.IO;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services;

internal enum VideoCompressionCodecMode
{
    H264,
    H265,
    Av1
}

internal enum VideoCompressionStage
{
    Preparing,
    Transcoding,
    VerifyingSize,
    Retrying,
    Publishing
}

internal enum VideoCompressionFailureKind
{
    None,
    SourceMissing,
    SourceChanged,
    Av1Unavailable,
    SourceTooSmall,
    TargetTooLarge,
    TargetTooSmall,
    FfmpegUnavailable,
    SizeUnreachable,
    TranscodeFailed,
    TranscodeStalled,
    TranscodeTimeout,
    InvalidOutput
}

internal sealed record VideoCompressionSource(
    string OrderId,
    string FileName,
    string FilePath,
    string VideoCodec,
    double DurationSeconds,
    long FileSizeBytes,
    double StartSeconds = 0d,
    long RangeSourceBytes = 0);

internal sealed record VideoCompressionRequest(
    VideoCompressionSource Source,
    string OutputPath,
    long TargetBytes,
    VideoCompressionCodecMode CodecMode,
    string Av1Encoder = "");

internal sealed record VideoCompressionProgress(
    int Percent,
    VideoCompressionStage Stage);

internal sealed record VideoCompressionResult(
    string OutputPath,
    long FileSizeBytes);

internal sealed class VideoCompressionException : Exception
{
    internal VideoCompressionException(
        VideoCompressionFailureKind kind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    internal VideoCompressionFailureKind Kind { get; }
}

internal static class VideoCompressionPolicy
{
    internal const int MinimumTargetSizeMb = 1;
    internal const int MaximumTargetSizeMb = 4096;
    internal const long Mebibyte = 1024L * 1024L;
    private const int MaximumOrderTokenLength = 48;
    private static readonly int[] PresetPercentOptions = [30, 50, 70];

    internal static string NormalizeCodec(string? codec) =>
        (codec ?? "").Trim().ToLowerInvariant() switch
        {
            "avc" or "h.264" => "h264",
            "hevc" or "h.265" => "h265",
            "av01" or "av-1" => "av1",
            string value => value
        };

    internal static VideoCompressionCodecMode ResolveDefaultCodecMode(
        string? sourceCodec,
        bool av1Available) =>
        NormalizeCodec(sourceCodec) switch
        {
            "h264" => VideoCompressionCodecMode.H264,
            "h265" => VideoCompressionCodecMode.H265,
            "av1" when av1Available => VideoCompressionCodecMode.Av1,
            "av1" => VideoCompressionCodecMode.H265,
            _ => VideoCompressionCodecMode.H264
        };

    internal static long GetDefaultTargetBytes(
        long sourceBytes,
        VideoCompressionCodecMode codecMode)
    {
        if (sourceBytes <= 0)
            return MinimumTargetSizeMb * Mebibyte;

        double ratio = codecMode switch
        {
            VideoCompressionCodecMode.Av1 => 0.32d,
            VideoCompressionCodecMode.H265 => 0.40d,
            VideoCompressionCodecMode.H264 => 0.60d,
            _ => throw new ArgumentOutOfRangeException(nameof(codecMode))
        };
        long rounded = (long)Math.Round(
            sourceBytes * ratio / Mebibyte,
            MidpointRounding.AwayFromZero) * Mebibyte;
        rounded = Math.Max(MinimumTargetSizeMb * Mebibyte, rounded);
        rounded = Math.Min(MaximumTargetSizeMb * Mebibyte, rounded);
        return rounded;
    }

    internal static int? ResolvePresetPercent(
        long sourceBytes,
        long targetBytes)
    {
        if (sourceBytes <= 0 || targetBytes <= 0)
            return null;

        double targetPercent = targetBytes * 100d / sourceBytes;
        foreach (int preset in PresetPercentOptions)
        {
            if (Math.Abs(targetPercent - preset) <= 2d)
                return preset;
        }

        return null;
    }

    internal static bool IsTargetWithinSupportedRange(double targetMb) =>
        !double.IsNaN(targetMb)
        && !double.IsInfinity(targetMb)
        && targetMb >= MinimumTargetSizeMb
        && targetMb <= MaximumTargetSizeMb;

    internal static long MbToBytes(double targetMb) =>
        (long)Math.Round(targetMb * Mebibyte, MidpointRounding.AwayFromZero);

    internal static bool TryValidateTarget(
        long sourceBytes,
        long targetBytes,
        double durationSeconds,
        out VideoCompressionFailureKind failure)
    {
        if (sourceBytes <= MinimumTargetSizeMb * Mebibyte)
        {
            failure = VideoCompressionFailureKind.SourceTooSmall;
            return false;
        }

        if (targetBytes > sourceBytes)
        {
            failure = VideoCompressionFailureKind.TargetTooLarge;
            return false;
        }

        if (durationSeconds <= 0
            || !TargetSizeTranscodeBitrate.TryCalculate(
                durationSeconds,
                targetBytes,
                out _))
        {
            failure = VideoCompressionFailureKind.TargetTooSmall;
            return false;
        }

        failure = VideoCompressionFailureKind.None;
        return true;
    }

    internal static double EstimateSeconds(
        double durationSeconds,
        VideoCompressionCodecMode codecMode,
        string av1Encoder = "")
    {
        string encoder = TargetSizeTranscodeEngine.ResolveEncoder(
            codecMode switch
            {
                VideoCompressionCodecMode.H264 => TargetSizeTranscodeCodec.H264,
                VideoCompressionCodecMode.Av1 => TargetSizeTranscodeCodec.Av1,
                VideoCompressionCodecMode.H265 => TargetSizeTranscodeCodec.H265,
                _ => throw new ArgumentOutOfRangeException(nameof(codecMode))
            },
            "",
            av1Encoder);
        return TargetSizeTranscodeEngine.EstimateTranscodeSeconds(
            durationSeconds,
            encoder,
            1);
    }

    internal static string ResolveValidatedAv1Encoder(
        IEnumerable<string>? validatedEncoders)
    {
        var validated = new HashSet<string>(
            validatedEncoders ?? [],
            StringComparer.OrdinalIgnoreCase);
        string[] priority =
        [
            "av1_nvenc",
            "av1_qsv",
            "av1_amf",
            "libsvtav1"
        ];
        return priority.FirstOrDefault(validated.Contains) ?? "";
    }

    internal static int CalculateReductionPercent(long sourceBytes, long targetBytes)
    {
        if (sourceBytes <= 0 || targetBytes >= sourceBytes)
            return 0;

        return (int)Math.Round(
            (sourceBytes - targetBytes) * 100d / sourceBytes,
            MidpointRounding.AwayFromZero);
    }

    internal static long CalculateRetryTargetBytes(long targetBytes, long actualBytes)
    {
        if (targetBytes <= 0 || actualBytes <= 0)
            return 0;

        double retry = (double)targetBytes * targetBytes / actualBytes * 0.96d;
        if (double.IsNaN(retry) || double.IsInfinity(retry) || retry <= 0)
            return 0;
        return (long)Math.Floor(Math.Min(retry, targetBytes));
    }

    internal static string BuildDefaultFileName(
        string? orderId,
        string? sourceFileName,
        VideoCompressionCodecMode codecMode,
        DateTime createdAt)
    {
        string baseToken = ToAsciiToken(
            Path.GetFileNameWithoutExtension(sourceFileName ?? ""));
        if (baseToken.Length == 0)
        {
            baseToken = ToAsciiToken(orderId);
            if (baseToken.Length == 0)
                baseToken = "Recording";
            baseToken += "_" + createdAt.ToString("yyyyMMdd_HHmmss");
        }

        string codecToken = codecMode switch
        {
            VideoCompressionCodecMode.Av1 => "AV1",
            VideoCompressionCodecMode.H265 => "H265",
            VideoCompressionCodecMode.H264 => "H264",
            _ => throw new ArgumentOutOfRangeException(nameof(codecMode))
        };
        return $"{baseToken}_{codecToken}_compressed.mp4";
    }

    internal static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes}B";
        if (bytes < Mebibyte)
            return $"{bytes / 1024d:F0}KB";
        if (bytes < 1024L * 1024L * 1024L)
            return $"{bytes / (double)Mebibyte:F1}MB";
        return $"{bytes / (1024d * Mebibyte):F2}GB";
    }

    private static string ToAsciiToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var builder = new System.Text.StringBuilder(MaximumOrderTokenLength);
        foreach (char character in value.Trim())
        {
            if (builder.Length >= MaximumOrderTokenLength)
                break;
            if (character <= 0x7f
                && (char.IsAsciiLetterOrDigit(character)
                    || character is '-' or '_'))
            {
                builder.Append(character);
            }
            else if (character is ' ' or '.')
            {
                builder.Append('_');
            }
        }

        return builder.ToString().Trim('_', '.', '-');
    }
}

internal sealed class VideoCompressionService
{
    private readonly FfmpegWorkLimiter _ffmpegWorkLimiter;
    private readonly Func<string> _ffmpegPathResolver;

    internal VideoCompressionService(
        Func<string>? ffmpegPathResolver = null,
        FfmpegWorkLimiter? ffmpegWorkLimiter = null)
    {
        _ffmpegPathResolver = ffmpegPathResolver ?? AppPaths.FindFFmpeg;
        _ffmpegWorkLimiter = ffmpegWorkLimiter ?? new FfmpegWorkLimiter(1);
    }

    internal Task<VideoCompressionResult> CompressAsync(
        VideoCompressionRequest request,
        IProgress<VideoCompressionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(
            () => Compress(request, progress, cancellationToken),
            cancellationToken);
    }

    internal Task<bool> IsEncoderAvailableAsync(
        string encoder,
        CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                string ffmpegPath = _ffmpegPathResolver();
                return !string.IsNullOrWhiteSpace(ffmpegPath)
                    && File.Exists(ffmpegPath)
                    && TargetSizeTranscodeEngine.IsEncoderAvailable(
                        ffmpegPath,
                        encoder,
                        cancellationToken);
            },
            cancellationToken);

    private VideoCompressionResult Compress(
        VideoCompressionRequest request,
        IProgress<VideoCompressionProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new VideoCompressionProgress(2, VideoCompressionStage.Preparing));

        string sourcePath = Path.GetFullPath(request.Source.FilePath);
        string outputPath = Path.GetFullPath(request.OutputPath);
        if (!File.Exists(sourcePath))
        {
            throw new VideoCompressionException(
                VideoCompressionFailureKind.SourceMissing,
                "录像文件不存在或当前不可访问");
        }

        if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(outputPath), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new VideoCompressionException(
                VideoCompressionFailureKind.InvalidOutput,
                "压缩副本必须保存为另一个 MP4 文件");
        }
        if (File.Exists(outputPath)
            && FileSystemIdentity.AreSameFile(sourcePath, outputPath))
        {
            throw new VideoCompressionException(
                VideoCompressionFailureKind.InvalidOutput,
                "导出路径指向原录像文件");
        }

        bool usesAv1Encoder = request.CodecMode == VideoCompressionCodecMode.Av1;

        var sourceInfo = new FileInfo(sourcePath);
        long sourceBytes = sourceInfo.Length;
        long sourceLastWriteTicks = sourceInfo.LastWriteTimeUtc.Ticks;
        long effectiveSourceBytes = request.Source.RangeSourceBytes > 0
            ? request.Source.RangeSourceBytes
            : sourceBytes;
        if (!VideoCompressionPolicy.TryValidateTarget(
                effectiveSourceBytes,
                request.TargetBytes,
                request.Source.DurationSeconds,
                out VideoCompressionFailureKind validationFailure))
        {
            throw CreateFailure(validationFailure);
        }

        string ffmpegPath = _ffmpegPathResolver();
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            throw new VideoCompressionException(
                VideoCompressionFailureKind.FfmpegUnavailable,
                "未找到视频处理组件 ffmpeg.exe");
        }

        string av1Encoder = (request.Av1Encoder ?? "").Trim().ToLowerInvariant();
        if (usesAv1Encoder
            && TargetSizeTranscodeEngine.BuildAv1VideoArguments(av1Encoder).Length == 0)
        {
            throw new VideoCompressionException(
                VideoCompressionFailureKind.Av1Unavailable,
                "没有已验证可用的 AV1 编码器");
        }
        if (usesAv1Encoder
            && !TargetSizeTranscodeEngine.IsEncoderAvailable(
                ffmpegPath,
                av1Encoder,
                cancellationToken))
        {
            throw new VideoCompressionException(
                VideoCompressionFailureKind.Av1Unavailable,
                "当前 FFmpeg 不提供已验证的 AV1 编码器");
        }

        string encoder = usesAv1Encoder
            ? av1Encoder
            : TargetSizeTranscodeEngine.ResolveEncoder(
                request.CodecMode == VideoCompressionCodecMode.H265
                    ? TargetSizeTranscodeCodec.H265
                    : TargetSizeTranscodeCodec.H264,
                "");

        if (!TargetSizeTranscodeBitrate.TryCalculate(
                request.Source.DurationSeconds,
                request.TargetBytes,
                out TargetSizeTranscodeBitrate? initialBitrate))
        {
            throw new VideoCompressionException(
                VideoCompressionFailureKind.TargetTooSmall,
                "目标大小太小，无法生成可播放的视频");
        }

        string outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new VideoCompressionException(
                VideoCompressionFailureKind.InvalidOutput,
                "压缩副本保存路径无效");
        Directory.CreateDirectory(outputDirectory);
        string workDirectory = Path.Combine(
            outputDirectory,
            $".{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}.compressing");
        string partialPath = Path.Combine(workDirectory, "output.mp4");
        string passLogPath = Path.Combine(workDirectory, "output.mp4.pass");
        TargetSizeTranscodeBitrate bitrate = initialBitrate!;
        DateTime startedAt = DateTime.UtcNow;

        try
        {
            CleanupStaleWorkDirectories(outputDirectory);
            Directory.CreateDirectory(workDirectory);
            MarkDirectoryHidden(workDirectory);
            using IDisposable slot = _ffmpegWorkLimiter.Enter(cancellationToken);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryDelete(partialPath);
                TargetSizeTranscodeEngine.DeletePassArtifacts(partialPath);
                int progressBase = attempt == 0 ? 5 : 55;
                int progressSpan = attempt == 0 ? 45 : 40;
                progress?.Report(new VideoCompressionProgress(
                    progressBase,
                    attempt == 0
                        ? VideoCompressionStage.Transcoding
                        : VideoCompressionStage.Retrying));

                Action<double> reportTranscodeProgress = fraction =>
                    progress?.Report(new VideoCompressionProgress(
                        Math.Clamp(
                            (int)Math.Round(progressBase + fraction * progressSpan),
                            0,
                            99),
                        attempt == 0
                            ? VideoCompressionStage.Transcoding
                            : VideoCompressionStage.Retrying));
                bool completed = usesAv1Encoder
                    ? TargetSizeTranscodeEngine.RunSinglePass(
                        ffmpegPath,
                        sourcePath,
                        partialPath,
                        encoder,
                        bitrate,
                        request.Source.DurationSeconds,
                        request.Source.StartSeconds,
                        reportTranscodeProgress,
                        cancellationToken,
                        out TargetSizeTranscodeFailure transcodeFailure)
                    : TargetSizeTranscodeEngine.RunTwoPass(
                        ffmpegPath,
                        sourcePath,
                        partialPath,
                        passLogPath,
                        encoder,
                        bitrate,
                        request.Source.DurationSeconds,
                        request.Source.StartSeconds,
                        reportTranscodeProgress,
                        cancellationToken,
                        out transcodeFailure);
                if (!completed)
                {
                    if (usesAv1Encoder
                        && transcodeFailure == TargetSizeTranscodeFailure.Failed)
                    {
                        throw new VideoCompressionException(
                            VideoCompressionFailureKind.Av1Unavailable,
                            "当前显卡或驱动无法使用 AV1 编码");
                    }
                    throw CreateFailure(transcodeFailure);
                }

                var partialInfo = new FileInfo(partialPath);
                if (!partialInfo.Exists || partialInfo.Length <= 0)
                {
                    throw new VideoCompressionException(
                        VideoCompressionFailureKind.TranscodeFailed,
                        "视频处理组件没有生成有效文件");
                }

                cancellationToken.ThrowIfCancellationRequested();
                var currentSourceInfo = new FileInfo(sourcePath);
                if (!currentSourceInfo.Exists
                    || currentSourceInfo.Length != sourceBytes
                    || currentSourceInfo.LastWriteTimeUtc.Ticks != sourceLastWriteTicks)
                {
                    throw new VideoCompressionException(
                        VideoCompressionFailureKind.SourceChanged,
                        "录像仍在写入或文件中途发生变化，请等录像完成后重试");
                }

                if (partialInfo.Length <= request.TargetBytes)
                {
                    progress?.Report(new VideoCompressionProgress(
                        98,
                        VideoCompressionStage.VerifyingSize));
                    progress?.Report(new VideoCompressionProgress(99, VideoCompressionStage.Publishing));
                    File.Move(partialPath, outputPath, overwrite: true);
                    long finalBytes = new FileInfo(outputPath).Length;
                    RuntimeLog.Info(
                        "VideoCompression",
                        $"压缩完成 output={Path.GetFileName(outputPath)} size={finalBytes} " +
                        $"target={request.TargetBytes} elapsedMs={(DateTime.UtcNow - startedAt).TotalMilliseconds:F0}");
                    return new VideoCompressionResult(
                        outputPath,
                        finalBytes);
                }

                long retryTarget = VideoCompressionPolicy.CalculateRetryTargetBytes(
                    request.TargetBytes,
                    partialInfo.Length);
                if (!TargetSizeTranscodeBitrate.TryCalculate(
                        request.Source.DurationSeconds,
                        retryTarget,
                        out TargetSizeTranscodeBitrate? retryBitrate))
                {
                    break;
                }

                bitrate = retryBitrate!;
            }

            throw new VideoCompressionException(
                VideoCompressionFailureKind.SizeUnreachable,
                "目标大小太小，已停止压缩");
        }
        catch (OperationCanceledException)
        {
            RuntimeLog.Info(
                "VideoCompression",
                $"压缩已取消 elapsedMs={(DateTime.UtcNow - startedAt).TotalMilliseconds:F0}");
            throw;
        }
        catch (VideoCompressionException ex)
        {
            RuntimeLog.Warn(
                "VideoCompression",
                $"压缩失败 kind={ex.Kind} message={ex.Message} target={request.TargetBytes} " +
                $"elapsedMs={(DateTime.UtcNow - startedAt).TotalMilliseconds:F0}");
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("VideoCompression", "压缩副本失败", ex);
            throw new VideoCompressionException(
                VideoCompressionFailureKind.TranscodeFailed,
                "视频处理失败",
                ex);
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDirectory))
                    Directory.Delete(workDirectory, recursive: true);
            }
            catch
            {
                RuntimeLog.Warn(
                    "VideoCompression",
                    $"压缩临时目录清理失败 path={workDirectory}");
            }
        }
    }

    private static VideoCompressionException CreateFailure(
        VideoCompressionFailureKind kind) =>
        kind switch
        {
            VideoCompressionFailureKind.SourceTooSmall => new(
                kind,
                "当前文件已很小，无需压缩"),
            VideoCompressionFailureKind.TargetTooLarge => new(
                kind,
                "目标大小不能超过原文件"),
            VideoCompressionFailureKind.TargetTooSmall => new(
                kind,
                "目标大小太小，无法生成可播放的视频"),
            VideoCompressionFailureKind.FfmpegUnavailable => new(
                kind,
                "未找到视频处理组件 ffmpeg.exe"),
            VideoCompressionFailureKind.SizeUnreachable => new(
                kind,
                "目标大小太小，已停止压缩"),
            VideoCompressionFailureKind.TranscodeStalled => new(
                kind,
                "视频处理长时间没有进展，已停止压缩"),
            VideoCompressionFailureKind.TranscodeTimeout => new(
                kind,
                "视频处理超时，已停止压缩"),
            _ => new VideoCompressionException(
                VideoCompressionFailureKind.TranscodeFailed,
                "视频处理失败")
        };

    private static VideoCompressionException CreateFailure(
        TargetSizeTranscodeFailure failure) =>
        CreateFailure(failure switch
        {
            TargetSizeTranscodeFailure.Stalled =>
                VideoCompressionFailureKind.TranscodeStalled,
            TargetSizeTranscodeFailure.Timeout =>
                VideoCompressionFailureKind.TranscodeTimeout,
            _ => VideoCompressionFailureKind.TranscodeFailed
        });

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void MarkDirectoryHidden(string directory)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetAttributes(
                directory,
                File.GetAttributes(directory) | FileAttributes.Hidden);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(
                "VideoCompression",
                $"无法隐藏压缩临时目录 path={directory}, error={ex.Message}");
        }
    }

    private static void CleanupStaleWorkDirectories(string outputDirectory)
    {
        DateTime cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(
                         outputDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(directory);
                if (!name.StartsWith('.')
                    || !name.EndsWith(".compressing", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (Directory.GetLastWriteTimeUtc(directory) >= cutoff)
                    continue;
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(
                "VideoCompression",
                $"清理旧压缩临时目录失败 path={outputDirectory}, error={ex.Message}");
        }
    }
}
