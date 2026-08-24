using AForge.Video.DirectShow;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.ViewModels;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace ExpressPackingMonitoring.Services;

internal readonly record struct NativeCameraMode(int Width, int Height, int Fps)
{
    public long PixelCount => (long)Width * Height;
}

internal sealed record RecordingProfileRecommendation(
    bool Success,
    NativeCameraMode? Mode,
    string Message,
    IReadOnlyList<RealtimeEncodingBenchmarkResult> Benchmarks);

internal sealed record RealtimeEncodingBenchmarkResult(
    NativeCameraMode Mode,
    bool CompletedSuccessfully,
    double ElapsedSeconds,
    int EncodedFrames,
    double MeasuredEncodingFps,
    double RequiredHeadroom,
    string Detail)
{
    internal bool SupportsFrameRate(int fps) =>
        CompletedSuccessfully
        && fps > 0
        && MeasuredEncodingFps >= fps * RequiredHeadroom;

    internal bool Stable => SupportsFrameRate(Mode.Fps);
}

internal static class RecordingProfileDetector
{
    internal const int TargetFps = 15;
    internal const double RequiredEncodingSpeed = 1.20;
    internal const int BenchmarkCacheSchemaVersion = AppConfig.CurrentEncoderBenchmarkCacheSchemaVersion;
    internal const int BenchmarkFrameCount = 180;
    internal const int BenchmarkInputFps = 60;
    private const int BenchmarkTimeoutMs = 12_000;
    private static readonly (int Width, int Height)[] StandardBenchmarkResolutions =
    [
        (3840, 2160),
        (2560, 1440),
        (1920, 1080),
        (1280, 720)
    ];

    internal static IReadOnlyList<NativeCameraMode> GetNativeModes(
        IEnumerable<VideoCapabilities>? capabilities)
    {
        return capabilities?
            .Where(capability =>
                capability.FrameSize.Width > 0
                && capability.FrameSize.Height > 0
                && capability.AverageFrameRate > 0)
            .Select(capability => new NativeCameraMode(
                capability.FrameSize.Width,
                capability.FrameSize.Height,
                capability.AverageFrameRate))
            .Distinct()
            .ToList()
            ?? [];
    }

    internal static RecordingProfileRecommendation Recommend(
        IEnumerable<NativeCameraMode> nativeModes,
        Func<NativeCameraMode, RealtimeEncodingBenchmarkResult> benchmark)
    {
        ArgumentNullException.ThrowIfNull(nativeModes);
        ArgumentNullException.ThrowIfNull(benchmark);

        List<NativeCameraMode> validModes = nativeModes
            .Where(mode => mode.Width > 0 && mode.Height > 0 && mode.Fps > 0)
            .Distinct()
            .ToList();
        List<NativeCameraMode> benchmarkModes = StandardBenchmarkResolutions
            .Select(resolution => new NativeCameraMode(
                resolution.Width,
                resolution.Height,
                TargetFps))
            .Concat(validModes.Select(mode => new NativeCameraMode(
                mode.Width,
                mode.Height,
                TargetFps)))
            .GroupBy(mode => (mode.Width, mode.Height))
            .Select(group => group.First())
            .OrderByDescending(mode => mode.PixelCount)
            .ThenByDescending(mode => mode.Width)
            .ThenByDescending(mode => mode.Height)
            .ToList();
        var benchmarks = new List<RealtimeEncodingBenchmarkResult>(benchmarkModes.Count);
        foreach (NativeCameraMode benchmarkMode in benchmarkModes)
            benchmarks.Add(benchmark(benchmarkMode));

        List<NativeCameraMode> eligibleModes = validModes
            .Where(mode => mode.Fps >= TargetFps)
            .ToList();
        if (eligibleModes.Count == 0)
        {
            return new RecordingProfileRecommendation(
                false,
                null,
                "摄像头没有至少 15 FPS 的原生录制模式",
                benchmarks);
        }

        int selectedFps = eligibleModes.Min(mode => mode.Fps);
        NativeCameraMode? selectedMode = eligibleModes
            .Where(mode => mode.Fps == selectedFps)
            .OrderByDescending(mode => mode.PixelCount)
            .ThenByDescending(mode => mode.Width)
            .ThenByDescending(mode => mode.Height)
            .Where(candidate => FindBenchmark(benchmarks, candidate) is { } result
                && result.SupportsFrameRate(candidate.Fps))
            .Select(candidate => (NativeCameraMode?)candidate)
            .FirstOrDefault();
        if (selectedMode is NativeCameraMode recommended)
        {
            return new RecordingProfileRecommendation(
                true,
                recommended,
                $"已推荐 {recommended.Width}×{recommended.Height} @ {recommended.Fps} FPS",
                benchmarks);
        }

        return new RecordingProfileRecommendation(
            false,
            null,
            $"当前编码器没有足够余量稳定录制摄像头 {selectedFps} FPS 的原生规格",
            benchmarks);
    }

    internal static NativeCameraMode? SelectSafeFallback(
        IEnumerable<NativeCameraMode> nativeModes)
    {
        ArgumentNullException.ThrowIfNull(nativeModes);
        List<NativeCameraMode> validModes = nativeModes
            .Where(mode => mode.Width > 0 && mode.Height > 0 && mode.Fps > 0)
            .Distinct()
            .ToList();
        if (validModes.Count == 0)
            return null;

        List<NativeCameraMode> preferredModes = validModes
            .Where(mode => mode.Fps >= TargetFps)
            .ToList();
        int selectedFps = preferredModes.Count > 0
            ? preferredModes.Min(mode => mode.Fps)
            : validModes.Max(mode => mode.Fps);
        IEnumerable<NativeCameraMode> candidates = (preferredModes.Count > 0
                ? preferredModes
                : validModes)
            .Where(mode => mode.Fps == selectedFps);
        return candidates
            .OrderBy(mode =>
                Math.Abs(mode.Width - 1280)
                + Math.Abs(mode.Height - 720))
            .ThenBy(mode => mode.PixelCount)
            .ThenBy(mode => mode.Width)
            .ThenBy(mode => mode.Height)
            .Select(mode => (NativeCameraMode?)mode)
            .FirstOrDefault();
    }

    internal static void UpdateBenchmarkCache(
        AppConfig config,
        string encoder,
        int videoCqp,
        IEnumerable<RealtimeEncodingBenchmarkResult> benchmarks,
        DateTime testedAt)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.RecordingBenchmarkCache ??= [];
        foreach (RealtimeEncodingBenchmarkResult benchmark in benchmarks)
        {
            config.RecordingBenchmarkCache.RemoveAll(entry =>
                entry.SchemaVersion == BenchmarkCacheSchemaVersion
                && string.Equals(entry.Encoder, encoder, StringComparison.OrdinalIgnoreCase)
                && entry.VideoCqp == videoCqp
                && entry.Width == benchmark.Mode.Width
                && entry.Height == benchmark.Mode.Height
                && entry.Fps == benchmark.Mode.Fps);
            config.RecordingBenchmarkCache.Add(new RecordingBenchmarkCacheEntry
            {
                SchemaVersion = BenchmarkCacheSchemaVersion,
                Encoder = encoder,
                VideoCqp = videoCqp,
                Width = benchmark.Mode.Width,
                Height = benchmark.Mode.Height,
                Fps = benchmark.Mode.Fps,
                CompletedSuccessfully = benchmark.CompletedSuccessfully,
                EncodedFrames = benchmark.EncodedFrames,
                ElapsedSeconds = benchmark.ElapsedSeconds,
                MeasuredEncodingFps = benchmark.MeasuredEncodingFps,
                TestedAt = testedAt
            });
        }

        config.RecordingBenchmarkCache = config.RecordingBenchmarkCache
            .Where(entry => entry.SchemaVersion == BenchmarkCacheSchemaVersion)
            .OrderByDescending(entry => entry.TestedAt)
            .Take(100)
            .ToList();
    }

    internal static bool TryGetCachedBenchmark(
        AppConfig config,
        string encoder,
        int videoCqp,
        NativeCameraMode mode,
        out RecordingBenchmarkCacheEntry entry)
    {
        entry = config.RecordingBenchmarkCache?
            .Where(candidate =>
                candidate.SchemaVersion == BenchmarkCacheSchemaVersion
                && string.Equals(candidate.Encoder, encoder, StringComparison.OrdinalIgnoreCase)
                && candidate.VideoCqp == videoCqp
                && candidate.Width == mode.Width
                && candidate.Height == mode.Height
                && candidate.Fps == mode.Fps)
            .OrderByDescending(candidate => candidate.TestedAt)
            .FirstOrDefault()!;
        return entry != null;
    }

    internal static bool CachedBenchmarkSupportsFrameRate(
        RecordingBenchmarkCacheEntry entry,
        int fps)
    {
        return entry != null
            && entry.CompletedSuccessfully
            && fps > 0
            && entry.MeasuredEncodingFps >= fps * RequiredEncodingSpeed;
    }

    internal static bool TryRecommendFromCache(
        AppConfig config,
        string encoder,
        int videoCqp,
        IEnumerable<NativeCameraMode> nativeModes,
        out NativeCameraMode recommendedMode)
    {
        recommendedMode = default;
        List<NativeCameraMode> eligibleModes = nativeModes
            .Where(mode => mode.Width > 0 && mode.Height > 0 && mode.Fps >= TargetFps)
            .Distinct()
            .ToList();
        if (eligibleModes.Count == 0)
            return false;

        int selectedFps = eligibleModes.Min(mode => mode.Fps);
        foreach (NativeCameraMode candidate in eligibleModes
                     .Where(mode => mode.Fps == selectedFps)
                     .OrderByDescending(mode => mode.PixelCount)
                     .ThenByDescending(mode => mode.Width)
                     .ThenByDescending(mode => mode.Height))
        {
            if (TryGetCachedBenchmark(
                    config,
                    encoder,
                    videoCqp,
                    candidate,
                    out RecordingBenchmarkCacheEntry cached)
                && CachedBenchmarkSupportsFrameRate(cached, candidate.Fps))
            {
                recommendedMode = candidate;
                return true;
            }
        }

        return false;
    }

    internal static RealtimeEncodingBenchmarkResult? FindBenchmark(
        IEnumerable<RealtimeEncodingBenchmarkResult> benchmarks,
        NativeCameraMode mode)
    {
        return benchmarks.FirstOrDefault(result =>
            result.Mode.Width == mode.Width
            && result.Mode.Height == mode.Height);
    }

    internal static bool IsRecommendationDifferent(
        AppConfig config,
        NativeCameraMode mode)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.FrameWidth != mode.Width
            || config.FrameHeight != mode.Height
            || config.Fps != mode.Fps;
    }

    internal static void ApplyRecommendation(
        AppConfig config,
        NativeCameraMode mode,
        string? cameraMoniker)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.FrameWidth = mode.Width;
        config.FrameHeight = mode.Height;
        config.Fps = mode.Fps;
        if (string.IsNullOrWhiteSpace(cameraMoniker))
            return;

        if (!config.CameraConfigs.TryGetValue(cameraMoniker, out CameraSettings? settings))
        {
            settings = new CameraSettings
            {
                AudioDeviceName = config.AudioDeviceName,
                AudioDeviceMoniker = config.AudioDeviceMoniker,
                AudioSyncOffsetMs = config.AudioSyncOffsetMs
            };
            config.CameraConfigs[cameraMoniker] = settings;
        }

        settings.FrameWidth = mode.Width;
        settings.FrameHeight = mode.Height;
        settings.Fps = mode.Fps;
    }

    internal static RealtimeEncodingBenchmarkResult Benchmark(
        string ffmpegPath,
        string encoder,
        int videoCqp,
        NativeCameraMode mode)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            return new RealtimeEncodingBenchmarkResult(
                mode,
                false,
                0,
                0,
                0,
                RequiredEncodingSpeed,
                "FFmpeg 不存在");
        }

        string source =
            $"testsrc2=size={mode.Width}x{mode.Height}:rate={BenchmarkInputFps},format=bgr24";
        string encoderArgs = MainViewModel.BuildFFmpegEncoderArgs(
            mode.Width,
            mode.Height,
            mode.Fps,
            encoder,
            videoCqp);
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments =
                $"-hide_banner -nostdin -nostats -stats_period 0.25 -progress pipe:1 " +
                $"-f lavfi -i \"{source}\" -frames:v {BenchmarkFrameCount} -an " +
                $"{encoderArgs} -f null -",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using Process? process = Process.Start(startInfo);
            if (process == null)
            {
                return new RealtimeEncodingBenchmarkResult(
                    mode,
                    false,
                    stopwatch.Elapsed.TotalSeconds,
                    0,
                    0,
                    RequiredEncodingSpeed,
                    "FFmpeg 进程启动失败");
            }

            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            bool exited = process.WaitForExit(BenchmarkTimeoutMs);
            stopwatch.Stop();
            double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { process.WaitForExit(3000); } catch { }
            }

            string progress = ReadOutput(stdoutTask);
            string stderr = ReadOutput(stderrTask);
            int encodedFrames = ParseProgressFrameCount(progress);
            if (exited && process.ExitCode == 0 && encodedFrames <= 0)
                encodedFrames = BenchmarkFrameCount;
            double measuredEncodingFps =
                CalculateMeasuredEncodingFps(encodedFrames, elapsedSeconds);
            bool completedSuccessfully = exited
                && process.ExitCode == 0
                && encodedFrames >= BenchmarkFrameCount;
            string detail = string.Format(
                CultureInfo.InvariantCulture,
                "frames={0}, exit={1}, elapsed={2:F2}s, measuredFps={3:F2}, headroom={4:F2}, stderr={5}",
                encodedFrames,
                exited ? process.ExitCode : -999,
                elapsedSeconds,
                measuredEncodingFps,
                RequiredEncodingSpeed,
                TrimDetail(stderr));

            return new RealtimeEncodingBenchmarkResult(
                mode,
                completedSuccessfully,
                elapsedSeconds,
                encodedFrames,
                measuredEncodingFps,
                RequiredEncodingSpeed,
                detail);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new RealtimeEncodingBenchmarkResult(
                mode,
                false,
                stopwatch.Elapsed.TotalSeconds,
                0,
                0,
                RequiredEncodingSpeed,
                ex.Message);
        }
    }

    internal static int ParseProgressFrameCount(string progress)
    {
        if (string.IsNullOrWhiteSpace(progress))
            return 0;

        int lastFrame = 0;
        foreach (string line in progress.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("frame=", StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(
                    line.AsSpan("frame=".Length).Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int frame))
            {
                lastFrame = Math.Max(lastFrame, frame);
            }
        }

        return lastFrame;
    }

    internal static double CalculateMeasuredEncodingFps(
        int encodedFrames,
        double elapsedSeconds)
    {
        return encodedFrames > 0 && elapsedSeconds > 0
            ? encodedFrames / elapsedSeconds
            : 0;
    }

    internal static int NormalizeVideoCqp(int videoCqp) =>
        videoCqp > 0 ? videoCqp : 25;

    private static string ReadOutput(Task<string> task)
    {
        try
        {
            return task.Wait(TimeSpan.FromSeconds(3)) ? task.Result ?? "" : "";
        }
        catch
        {
            return "";
        }
    }

    private static string TrimDetail(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return "";
        string normalized = detail.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= 300 ? normalized : normalized[..300];
    }
}
