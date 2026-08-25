using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.ViewModels;

namespace ExpressPackingMonitoring.Services;

internal static class EncoderProfileDetectionService
{
    internal static Task<RecordingProfileRecommendation> DetectAsync(
        AppConfig config,
        IReadOnlyList<NativeCameraMode> nativeModes)
    {
        ArgumentNullException.ThrowIfNull(config);
        nativeModes ??= [];
        return Task.Run(() => Detect(config, nativeModes));
    }

    private static RecordingProfileRecommendation Detect(
        AppConfig config,
        IReadOnlyList<NativeCameraMode> nativeModes)
    {
        EncoderDetectionResult detection = MainViewModel.DetectAvailableEncodersSync();
        NativeCameraMode targetMode = ResolveTargetMode(config, nativeModes);
        int videoCqp = RecordingProfileDetector.NormalizeVideoCqp(config.VideoCqp);
        string ffmpegPath = AppPaths.FindFFmpeg();

        config.ValidatedEncodersCache = detection.ValidatedEncoders.ToList();
        config.IsEncoderDetected = detection.Succeeded;
        config.EncoderDetectionCacheVersion = MainViewModel.CurrentEncoderDetectionCacheVersion;
        MainViewModel.UpdateEncoderDriverWarning(config, detection.NvencDriverIssue);

        if (!detection.FfmpegAvailable)
        {
            ClearSelection(config);
            return new RecordingProfileRecommendation(
                false,
                null,
                $"未找到 FFmpeg，录像已阻止。程序目录：{AppDomain.CurrentDomain.BaseDirectory}",
                []);
        }

        if (detection.ValidatedEncoders.Count == 0)
        {
            ClearSelection(config);
            return new RecordingProfileRecommendation(
                false,
                null,
                "已找到 FFmpeg，但没有编码器通过真实试编码。请查看编码器检测日志并重新检测",
                []);
        }

        DateTime testedAt = DateTime.Now;
        var benchmarks = new Dictionary<string, RealtimeEncodingBenchmarkResult>(StringComparer.OrdinalIgnoreCase);
        foreach (string encoder in detection.ValidatedEncoders.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            RealtimeEncodingBenchmarkResult benchmark = RecordingProfileDetector.Benchmark(
                ffmpegPath,
                encoder,
                videoCqp,
                targetMode);
            benchmarks[encoder] = benchmark;
            RecordingProfileDetector.UpdateBenchmarkCache(
                config,
                encoder,
                videoCqp,
                [benchmark],
                testedAt);
            RuntimeLog.Info(
                "RecordingProfile",
                $"encoder={encoder}, mode={targetMode.Width}x{targetMode.Height}@{targetMode.Fps}, stable={benchmark.SupportsFrameRate(targetMode.Fps)}, detail={benchmark.Detail}");
        }

        List<EncodingHelper.EncoderCandidate> candidates = MainViewModel.BuildEncoderCandidates(
            detection.ValidatedEncoders,
            benchmarks,
            targetMode);
        EncodingHelper.EncoderSelection? selection = EncodingHelper.SelectEncoder(
            candidates,
            config.VideoEncoderSelectionMode,
            config.ManualVideoEncoder);
        List<VideoEncoderOption> options = MainViewModel.BuildVisibleEncoderOptions(
            candidates,
            selection,
            targetMode.Fps);
        MainViewModel.AppendEncoderPerformanceLog(
            targetMode,
            videoCqp,
            candidates,
            selection,
            selection == null ? "没有可用于当前选择模式的编码器" : null);

        config.EncoderOptionsCache = options;
        config.EffectiveVideoEncoder = selection?.Encoder ?? "";
        if (selection == null)
        {
            return new RecordingProfileRecommendation(
                false,
                null,
                string.Equals(config.VideoEncoderSelectionMode, "manual", StringComparison.OrdinalIgnoreCase)
                    ? "手动选择的编码器不再满足当前录像规格，请重新选择编码器"
                    : "未检测到可自动使用的 H.264 或 H.265 编码器，请手动选择已验证的编码器",
                []);
        }

        RuntimeLog.Info(
            "RecordingProfile",
            $"selected={selection.Encoder}, manual={selection.IsManual}, meetsHeadroom={selection.MeetsRealtimeRequirement}, reason={selection.Reason}");
        RealtimeEncodingBenchmarkResult selectedBenchmark = benchmarks[selection.Encoder];
        return new RecordingProfileRecommendation(
            true,
            targetMode,
            selection.MeetsRealtimeRequirement
                ? $"编码器检测完成：{EncodingHelper.GetEncoderLabel(selection.Encoder)}"
                : $"{selection.Reason}：{EncodingHelper.GetEncoderLabel(selection.Encoder)}，录像可能丢帧",
            [selectedBenchmark]);
    }

    private static NativeCameraMode ResolveTargetMode(
        AppConfig config,
        IReadOnlyList<NativeCameraMode> nativeModes)
    {
        NativeCameraMode configured = new(
            Math.Max(1, config.FrameWidth),
            Math.Max(1, config.FrameHeight),
            Math.Max(1, config.Fps));
        return nativeModes
            .Where(mode => mode.Width > 0 && mode.Height > 0 && mode.Fps > 0)
            .OrderBy(mode => Math.Abs(mode.Width - configured.Width)
                + Math.Abs(mode.Height - configured.Height)
                + Math.Abs(mode.Fps - configured.Fps))
            .FirstOrDefault(configured);
    }

    private static void ClearSelection(AppConfig config)
    {
        config.EncoderOptionsCache = [];
        config.EffectiveVideoEncoder = "";
    }
}
