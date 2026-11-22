using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services;

internal enum TargetSizeTranscodeCodec
{
    Source,
    H264,
    H265,
    Av1
}

internal enum TargetSizeTranscodeFailure
{
    None,
    Failed,
    Stalled,
    Timeout
}

internal sealed record TargetSizeTranscodeBitrate(
    int VideoBitsPerSecond,
    int AudioBitsPerSecond)
{
    private const int MinimumVideoBitsPerSecond = 32_000;

    internal static bool TryCalculate(
        double durationSeconds,
        long targetBytes,
        out TargetSizeTranscodeBitrate? bitrate)
    {
        bitrate = null;
        if (durationSeconds <= 0
            || double.IsNaN(durationSeconds)
            || double.IsInfinity(durationSeconds)
            || targetBytes <= 0)
        {
            return false;
        }

        // 为 MP4 封装、时长偏差和编码器 ABR 波动保留 4% 安全余量。
        double totalBitsPerSecond = Math.Floor(targetBytes * 8d / durationSeconds * 0.96d);
        if (totalBitsPerSecond > int.MaxValue)
            return false;

        int total = (int)totalBitsPerSecond;
        int audio = Math.Clamp(total / 10, 32_000, 128_000);
        int video = total - audio;
        if (video < MinimumVideoBitsPerSecond)
            return false;

        bitrate = new TargetSizeTranscodeBitrate(video, audio);
        return true;
    }
}

/// <summary>
/// 把录像压到指定大小的通用两遍转码引擎。
/// 扩展交付副本和桌面“压缩副本”共用同一套码率、参数、看门狗和临时文件规则。
/// </summary>
internal static class TargetSizeTranscodeEngine
{
    private const string X265Encoder = "libx265";
    private const string X265StatsFileName = "x265_2pass.log";
    // 本机实测软编两遍约 2.0 × 录像时长（1080p58），门店机器 CPU 更弱，宁可估长。
    private const double X265SecondsPerSourceSecond = 2.4;
    private const double X264SecondsPerSourceSecond = 0.8;
    private static readonly TimeSpan ProgressStallTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinimumTranscodeTimeout = TimeSpan.FromMinutes(30);
    private const int TranscodeTimeoutFactor = 3;

    internal static string ResolveEncoder(
        TargetSizeTranscodeCodec codec,
        string sourceCodec,
        string av1Encoder = "")
    {
        if (codec == TargetSizeTranscodeCodec.H264)
            return "libx264";
        if (codec == TargetSizeTranscodeCodec.H265)
            return X265Encoder;
        if (codec == TargetSizeTranscodeCodec.Av1)
            return (av1Encoder ?? "").Trim().ToLowerInvariant();

        return (sourceCodec ?? "").Trim().ToLowerInvariant() switch
        {
            "h264" => "libx264",
            "h265" => X265Encoder,
            _ => ""
        };
    }

    internal static bool RunTwoPass(
        string ffmpegPath,
        string sourcePath,
        string outputPath,
        string passLogPath,
        string encoder,
        TargetSizeTranscodeBitrate bitrate,
        double sourceSeconds,
        Action<double>? reportProgress,
        CancellationToken cancellationToken,
        out TargetSizeTranscodeFailure failure) =>
        RunTwoPass(
            ffmpegPath,
            sourcePath,
            outputPath,
            passLogPath,
            encoder,
            bitrate,
            sourceSeconds,
            0d,
            reportProgress,
            cancellationToken,
            out failure);

    internal static bool RunTwoPass(
        string ffmpegPath,
        string sourcePath,
        string outputPath,
        string passLogPath,
        string encoder,
        TargetSizeTranscodeBitrate bitrate,
        double sourceSeconds,
        double startSeconds,
        Action<double>? reportProgress,
        CancellationToken cancellationToken,
        out TargetSizeTranscodeFailure failure)
    {
        failure = TargetSizeTranscodeFailure.Failed;
        string? workingDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(workingDirectory))
            return false;

        // 单遍的预估耗时：两遍的估算除以 2，用作硬超时基准。
        double passSeconds = EstimateTranscodeSeconds(sourceSeconds, encoder, 1) / 2d;
        string common =
            $"-loglevel warning -progress pipe:1 -nostats -y " +
            BuildRangeInputArguments(sourcePath, sourceSeconds, startSeconds) +
            " " +
            $"-map 0:v:0 -map 0:a:0? -c:v {encoder} -b:v {bitrate.VideoBitsPerSecond} " +
            $"-maxrate {bitrate.VideoBitsPerSecond} -bufsize {bitrate.VideoBitsPerSecond * 2L}";
        string nullOutput = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

        failure = RunFfmpeg(
            ffmpegPath,
            common + BuildTwoPassArguments(encoder, passLogPath, 1) + $" -an -f null {nullOutput}",
            workingDirectory,
            passSeconds,
            sourceSeconds,
            0d,
            0.5d,
            reportProgress,
            cancellationToken);
        if (failure != TargetSizeTranscodeFailure.None)
            return false;

        failure = RunFfmpeg(
            ffmpegPath,
            common + BuildTwoPassArguments(encoder, passLogPath, 2)
                + $" -c:a aac -b:a {bitrate.AudioBitsPerSecond} -movflags +faststart -f mp4 \"{outputPath}\"",
            workingDirectory,
            passSeconds,
            sourceSeconds,
            0.5d,
            0.5d,
            reportProgress,
            cancellationToken);
        return failure == TargetSizeTranscodeFailure.None;
    }

    internal static bool RunSinglePass(
        string ffmpegPath,
        string sourcePath,
        string outputPath,
        string encoder,
        TargetSizeTranscodeBitrate bitrate,
        double sourceSeconds,
        double startSeconds,
        Action<double>? reportProgress,
        CancellationToken cancellationToken,
        out TargetSizeTranscodeFailure failure)
    {
        failure = TargetSizeTranscodeFailure.Failed;
        string? workingDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(workingDirectory))
            return false;

        string videoArguments = BuildAv1VideoArguments(encoder);
        if (videoArguments.Length == 0)
            return false;

        string arguments =
            $"-loglevel warning -progress pipe:1 -nostats -y " +
            BuildRangeInputArguments(sourcePath, sourceSeconds, startSeconds) +
            " " +
            $"-map 0:v:0 -map 0:a:0? {videoArguments} " +
            $"-b:v {bitrate.VideoBitsPerSecond} -maxrate {bitrate.VideoBitsPerSecond} " +
            $"-bufsize {bitrate.VideoBitsPerSecond * 2L} " +
            $"-c:a aac -b:a {bitrate.AudioBitsPerSecond} -movflags +faststart -f mp4 \"{outputPath}\"";
        failure = RunFfmpeg(
            ffmpegPath,
            arguments,
            workingDirectory,
            EstimateTranscodeSeconds(sourceSeconds, encoder, 1),
            sourceSeconds,
            0d,
            1d,
            reportProgress,
            cancellationToken);
        return failure == TargetSizeTranscodeFailure.None;
    }

    internal static string BuildRangeInputArguments(
        string sourcePath,
        double sourceSeconds,
        double startSeconds)
    {
        double safeDuration = Math.Max(0d, sourceSeconds);
        double safeStart = Math.Max(0d, startSeconds);
        string duration = safeDuration.ToString("0.###", CultureInfo.InvariantCulture);
        string output = $"-i \"{sourcePath}\" -t {duration}";
        // 分段录像的 HEVC 参数集可能不在每个关键帧前；输入侧跳转会丢失解码上下文。
        return safeStart <= 0
            ? output
            : $"-i \"{sourcePath}\" -ss {safeStart.ToString("0.###", CultureInfo.InvariantCulture)} -t {duration}";
    }

    internal static string BuildAv1VideoArguments(string encoder) =>
        (encoder ?? "").Trim().ToLowerInvariant() switch
        {
            "av1_nvenc" =>
                "-c:v av1_nvenc -pix_fmt yuv420p -preset p4 -rc vbr",
            "av1_qsv" =>
                "-c:v av1_qsv -pix_fmt nv12 -preset medium",
            "av1_amf" =>
                "-c:v av1_amf -pix_fmt yuv420p -quality balanced -rc cbr",
            "libsvtav1" =>
                "-c:v libsvtav1 -pix_fmt yuv420p -preset 8 -svtav1-params tune=0",
            _ => ""
        };

    internal static bool IsEncoderAvailable(
        string ffmpegPath,
        string encoder,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(encoder))
            return false;

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-encoders");

        using Process? process = Process.Start(startInfo);
        if (process == null)
            return false;
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
        }

        using CancellationTokenRegistration cancellationRegistration =
            cancellationToken.Register(
                () => _ = Task.Run(() => TryKill(process)));
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        while (!process.WaitForExit(200))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                WaitForExitAfterKill(process);
                throw new OperationCanceledException(cancellationToken);
            }
            if (DateTimeOffset.UtcNow - startedAt > TimeSpan.FromSeconds(10))
            {
                TryKill(process);
                WaitForExitAfterKill(process);
                return false;
            }
        }

        string output = outputTask.GetAwaiter().GetResult()
            + errorTask.GetAwaiter().GetResult();
        return ContainsEncoder(output, encoder);
    }

    internal static bool ContainsEncoder(string output, string encoder)
    {
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(encoder))
            return false;

        return Regex.IsMatch(
            output,
            $@"(^|\s){Regex.Escape(encoder.Trim())}(\s|$)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
    }

    /// <summary>
    /// 按编码器和轮次估算转码耗时。轮次 2 表示第一轮没压够、要按更低码率再压一遍。
    /// </summary>
    internal static double EstimateTranscodeSeconds(
        double durationSeconds,
        string encoder,
        int attempt)
    {
        if (durationSeconds <= 0
            || double.IsNaN(durationSeconds)
            || double.IsInfinity(durationSeconds))
        {
            return 0;
        }

        string normalizedEncoder = (encoder ?? "").Trim().ToLowerInvariant();
        double perSourceSecond;
        if (normalizedEncoder == "libsvtav1")
            perSourceSecond = 2.8;
        else if (normalizedEncoder.StartsWith("av1_", StringComparison.Ordinal))
            perSourceSecond = 1.2;
        else if (normalizedEncoder == X265Encoder)
            perSourceSecond = X265SecondsPerSourceSecond;
        else
            perSourceSecond = X264SecondsPerSourceSecond;
        return durationSeconds * perSourceSecond * Math.Max(1, attempt);
    }

    /// <summary>
    /// 两遍编码各自的 pass 参数。libx265 不识别 ffmpeg 的 <c>-pass</c>，必须使用 x265 自己的统计文件。
    /// </summary>
    internal static string BuildTwoPassArguments(
        string encoder,
        string passLogPath,
        int pass)
    {
        if (encoder == X265Encoder)
        {
            // x265 统计文件只使用相对文件名，避免 Windows 盘符中的冒号被参数解析拆坏。
            return $" -x265-params \"pass={pass}:stats={X265StatsFileName}:cutree=0\"";
        }

        return $" -passlogfile \"{passLogPath}\" -pass {pass}";
    }

    internal static TimeSpan GetTranscodeTimeout(double expectedSeconds)
    {
        TimeSpan estimated = TimeSpan.FromSeconds(
            Math.Max(0d, expectedSeconds) * TranscodeTimeoutFactor);
        return estimated > MinimumTranscodeTimeout
            ? estimated
            : MinimumTranscodeTimeout;
    }

    internal static TargetSizeTranscodeFailure GetTranscodeWatchdogFailure(
        TimeSpan elapsed,
        TimeSpan sinceProgress,
        TimeSpan timeout)
    {
        if (sinceProgress > ProgressStallTimeout)
            return TargetSizeTranscodeFailure.Stalled;
        if (elapsed > timeout)
            return TargetSizeTranscodeFailure.Timeout;
        return TargetSizeTranscodeFailure.None;
    }

    internal static void DeletePassArtifacts(string outputPath)
    {
        string directory = Path.GetDirectoryName(outputPath) ?? "";
        TryDelete(outputPath + ".pass-0.log");
        TryDelete(outputPath + ".pass-0.log.mbtree");
        if (directory.Length > 0)
        {
            TryDelete(Path.Combine(directory, X265StatsFileName));
            TryDelete(Path.Combine(directory, X265StatsFileName + ".cutree"));
        }
    }

    private static TargetSizeTranscodeFailure RunFfmpeg(
        string ffmpegPath,
        string arguments,
        string workingDirectory,
        double expectedSeconds,
        double mediaSeconds,
        double progressBase,
        double progressSpan,
        Action<double>? reportProgress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using Process? process = Process.Start(startInfo);
        if (process == null)
            return TargetSizeTranscodeFailure.Failed;
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
        }

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        long[] lastProgressTicks = [startedAt.UtcTicks];
        TimeSpan timeout = GetTranscodeTimeout(expectedSeconds);
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        Task progressTask = Task.Run(
            () => ReadFfmpegProgress(
                process.StandardOutput,
                progressBase,
                progressSpan,
                reportProgress,
                lastProgressTicks,
                mediaSeconds),
            CancellationToken.None);
        using CancellationTokenRegistration cancellationRegistration =
            cancellationToken.Register(
                () => _ = Task.Run(() => TryKill(process)));

        while (!process.WaitForExit(1000))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                WaitForExitAfterKill(process);
                DrainReaderTasks(progressTask, errorTask);
                cancellationToken.ThrowIfCancellationRequested();
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            TargetSizeTranscodeFailure watchdogFailure = GetTranscodeWatchdogFailure(
                elapsed: now - startedAt,
                sinceProgress: TimeSpan.FromTicks(
                    now.UtcTicks - Interlocked.Read(ref lastProgressTicks[0])),
                timeout);
            if (watchdogFailure != TargetSizeTranscodeFailure.None)
            {
                TryKill(process);
                WaitForExitAfterKill(process);
                DrainReaderTasks(progressTask, errorTask);
                return watchdogFailure;
            }
        }

        DrainReaderTasks(progressTask, errorTask);

        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode == 0)
            return TargetSizeTranscodeFailure.None;

        RuntimeLog.Warn(
            "VideoCompression",
            $"FFmpeg 转码失败 exit={process.ExitCode} stderr={TrimForLog(errorTask.Result)}");
        return TargetSizeTranscodeFailure.Failed;
    }

    private static void ReadFfmpegProgress(
        StreamReader reader,
        double progressBase,
        double progressSpan,
        Action<double>? reportProgress,
        long[] lastProgressTicks,
        double mediaSeconds)
    {
        while (true)
        {
            string? line;
            try
            {
                line = reader.ReadLine();
            }
            catch
            {
                return;
            }

            if (line == null)
                return;
            if (!line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && !line.StartsWith("out_time_ms=", StringComparison.Ordinal))
            {
                continue;
            }

            string raw = line[(line.IndexOf('=') + 1)..];
            if (!double.TryParse(
                    raw,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double microseconds))
            {
                continue;
            }

            Interlocked.Exchange(ref lastProgressTicks[0], DateTimeOffset.UtcNow.UtcTicks);
            if (reportProgress == null || progressSpan <= 0 || mediaSeconds <= 0)
                continue;

            double fraction = Math.Clamp(
                microseconds / 1_000_000d / mediaSeconds,
                0d,
                1d);
            reportProgress(progressBase + fraction * progressSpan);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static void WaitForExitAfterKill(Process process)
    {
        try
        {
            process.WaitForExit(5000);
        }
        catch
        {
        }
    }

    private static void DrainReaderTasks(Task progressTask, Task<string> errorTask)
    {
        try
        {
            progressTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        try
        {
            errorTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
    }

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

    private static string TrimForLog(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        return value.Length <= 4000 ? value : value[..4000] + "...";
    }
}
