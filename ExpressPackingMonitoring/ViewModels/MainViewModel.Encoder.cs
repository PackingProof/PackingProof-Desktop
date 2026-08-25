using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ExpressPackingMonitoring.ViewModels
{
    internal sealed record EncoderDetectionResult(
        List<VideoEncoderOption> Options,
        HashSet<string> ValidatedEncoders,
        bool FfmpegAvailable,
        NvencDriverCompatibilityIssue? NvencDriverIssue)
    {
        internal bool Succeeded => FfmpegAvailable && ValidatedEncoders.Count > 0;
    }

    internal sealed record NvencDriverCompatibilityIssue(
        string RequiredApiVersion,
        string DetectedApiVersion,
        string MinimumDriverVersion);

    public partial class MainViewModel
    {
        internal const int CurrentEncoderDetectionCacheVersion = AppConfig.CurrentEncoderSelectionCacheVersion;
        internal const string NvencDriverTooOldWarningCode = "nvenc_driver_too_old";

        private static string QueryFFmpegEncoders(string ffmpegPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-hide_banner -encoders",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return "";
                Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
                if (!WaitForEncoderProbeExit(proc, 5000))
                {
                    RuntimeLog.Warn("EncoderDetect", "ffmpeg -encoders timed out");
                    return "";
                }

                string output = ReadProbeOutput(stdoutTask);
                string stderr = ReadProbeOutput(stderrTask);
                if (proc.ExitCode != 0)
                    RuntimeLog.Warn("EncoderDetect", $"ffmpeg -encoders failed exit={proc.ExitCode}, stderr={stderr}");
                return output;
            }
            catch { return ""; }
        }

        public async void ResetEncoderDetect()
        {
            RecordingProfileRecommendation? recommendation =
                await DetectAndRecommendRecordingProfileAsync(Config, null);
            ShowToast(
                recommendation?.Message ?? "录制性能检测失败，录像已阻止，请重新检测",
                recommendation?.Success == true ? ToastSeverity.Success : ToastSeverity.Error);
        }

        internal async Task<RecordingProfileRecommendation?> DetectAndRecommendRecordingProfileAsync(
            AppConfig detectionConfig,
            IReadOnlyList<NativeCameraMode>? selectedNativeModes)
        {
            ArgumentNullException.ThrowIfNull(detectionConfig);
            if (_isEncoderDetectRunning)
                return null;

            _isEncoderDetectRunning = true;
            IReadOnlyList<NativeCameraMode> nativeModes = selectedNativeModes ?? [];
            if (nativeModes.Count == 0)
            {
                if (IsNetworkCameraConfigured())
                {
                    nativeModes = _networkCameraSource?.NativeModes ?? [];
                }
                else
                {
                    try
                    {
                        nativeModes = RecordingProfileDetector.GetNativeModes(_videoSource?.VideoCapabilities);
                    }
                    catch
                    {
                        nativeModes = [];
                    }
                }
            }

            try
            {
                return await Task.Run(() =>
                {
                    EncoderDetectionResult detection = DetectAvailableEncodersSync();
                    NativeCameraMode targetMode = ResolveEncoderBenchmarkMode(detectionConfig, nativeModes);
                    int videoCqp = RecordingProfileDetector.NormalizeVideoCqp(detectionConfig.VideoCqp);
                    string ffmpegPath = AppPaths.FindFFmpeg();

                    ApplyCapabilityDetection(detectionConfig, detection);
                    if (!detection.Succeeded)
                    {
                        if (ReferenceEquals(Config, detectionConfig))
                            SaveConfig();
                        return new RecordingProfileRecommendation(
                            false,
                            null,
                            "未检测到可用的 FFmpeg 编码器，录像已阻止，请检查 FFmpeg 后重新检测",
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
                            detectionConfig,
                            encoder,
                            videoCqp,
                            [benchmark],
                            testedAt);
                        RuntimeLog.Info(
                            "RecordingProfile",
                            $"encoder={encoder}, mode={targetMode.Width}x{targetMode.Height}@{targetMode.Fps}, stable={benchmark.SupportsFrameRate(targetMode.Fps)}, detail={benchmark.Detail}");
                    }

                    List<EncodingHelper.EncoderCandidate> candidates = BuildEncoderCandidates(
                        detection.ValidatedEncoders,
                        benchmarks,
                        targetMode);
                    EncodingHelper.EncoderSelection? selection = EncodingHelper.SelectEncoder(
                        candidates,
                        detectionConfig.VideoEncoderSelectionMode,
                        detectionConfig.ManualVideoEncoder);
                    List<VideoEncoderOption> options = BuildVisibleEncoderOptions(candidates, selection, targetMode.Fps);
                    AppendEncoderPerformanceLog(
                        targetMode,
                        videoCqp,
                        candidates,
                        selection,
                        selection == null ? "没有可用于当前选择模式的编码器" : null);
                    if (selection == null)
                    {
                        ApplyEncoderSelection(detectionConfig, options, null);
                        CachedEncoderOptions = options;
                        ValidatedEncoders = detection.ValidatedEncoders;
                        if (ReferenceEquals(Config, detectionConfig))
                            SaveConfig();
                        return new RecordingProfileRecommendation(
                            false,
                            null,
                            string.Equals(detectionConfig.VideoEncoderSelectionMode, "manual", StringComparison.OrdinalIgnoreCase)
                                ? "手动选择的编码器不再满足当前录像规格，请重新选择编码器"
                                : "未检测到可自动使用的 H.264 或 H.265 编码器，请手动选择已验证的编码器",
                            []);
                    }

                    ApplyEncoderSelection(detectionConfig, options, selection);
                    CachedEncoderOptions = options;
                    ValidatedEncoders = detection.ValidatedEncoders;
                    if (ReferenceEquals(Config, detectionConfig))
                        SaveConfig();
                    RuntimeLog.Info(
                        "RecordingProfile",
                        $"selected={selection.Encoder}, manual={selection.IsManual}, meetsHeadroom={selection.MeetsRealtimeRequirement}, reason={selection.Reason}");

                    RealtimeEncodingBenchmarkResult selectedBenchmark = benchmarks[selection.Encoder];
                    return new RecordingProfileRecommendation(
                        selection.MeetsRealtimeRequirement,
                        selection.MeetsRealtimeRequirement ? targetMode : null,
                        selection.MeetsRealtimeRequirement
                            ? $"编码器检测完成：{EncodingHelper.GetEncoderLabel(selection.Encoder)}"
                            : $"{selection.Reason}：{EncodingHelper.GetEncoderLabel(selection.Encoder)}",
                        [selectedBenchmark]);
                });
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("EncoderDetect", "Manual recording profile detection failed", ex);
                return null;
            }
            finally
            {
                _isEncoderDetectRunning = false;
            }
        }

        private NativeCameraMode ResolveEncoderBenchmarkMode(
            AppConfig detectionConfig,
            IReadOnlyList<NativeCameraMode> nativeModes)
        {
            NativeCameraMode configured = new(
                Math.Max(1, detectionConfig.FrameWidth),
                Math.Max(1, detectionConfig.FrameHeight),
                Math.Max(1, detectionConfig.Fps));
            NativeCameraMode? selectedNativeMode = nativeModes
                .Where(mode => mode.Width > 0 && mode.Height > 0 && mode.Fps > 0)
                .OrderBy(mode => Math.Abs(mode.Width - configured.Width)
                    + Math.Abs(mode.Height - configured.Height)
                    + Math.Abs(mode.Fps - configured.Fps))
                .Select(mode => (NativeCameraMode?)mode)
                .FirstOrDefault();
            if (selectedNativeMode is NativeCameraMode mode)
                return mode;

            if (_actualCameraWidth > 0 && _actualCameraHeight > 0 && _actualCameraFps > 0)
                return new NativeCameraMode(_actualCameraWidth, _actualCameraHeight, _actualCameraFps);

            return configured;
        }

        private static void ApplyCapabilityDetection(AppConfig config, EncoderDetectionResult detection)
        {
            config.ValidatedEncodersCache = detection.ValidatedEncoders.ToList();
            config.IsEncoderDetected = detection.Succeeded;
            config.EncoderDetectionCacheVersion = CurrentEncoderDetectionCacheVersion;
            UpdateEncoderDriverWarning(config, detection.NvencDriverIssue);
        }

        private static void ApplyEncoderSelection(
            AppConfig config,
            List<VideoEncoderOption> options,
            EncodingHelper.EncoderSelection? selection)
        {
            config.EncoderOptionsCache = options;
            config.EffectiveVideoEncoder = selection?.Encoder ?? "";
        }

        internal static List<EncodingHelper.EncoderCandidate> BuildEncoderCandidates(
            IEnumerable<string> validatedEncoders,
            IReadOnlyDictionary<string, RealtimeEncodingBenchmarkResult> benchmarks,
            NativeCameraMode targetMode)
        {
            return validatedEncoders
                .Where(EncodingHelper.IsKnownEncoder)
                .Select(encoder =>
                {
                    if (!benchmarks.TryGetValue(encoder, out RealtimeEncodingBenchmarkResult? benchmark)
                        || benchmark == null)
                    {
                        return new EncodingHelper.EncoderCandidate(
                            encoder,
                            EncodingHelper.GetCodecFromEncoder(encoder),
                            EncodingHelper.IsHardwareEncoder(encoder),
                            0,
                            false);
                    }

                    return new EncodingHelper.EncoderCandidate(
                        encoder,
                        EncodingHelper.GetCodecFromEncoder(encoder),
                        EncodingHelper.IsHardwareEncoder(encoder),
                        benchmark.MeasuredEncodingFps,
                        benchmark.SupportsFrameRate(targetMode.Fps));
                })
                .ToList();
        }

        internal static List<VideoEncoderOption> BuildVisibleEncoderOptions(
            IEnumerable<EncodingHelper.EncoderCandidate> candidates,
            EncodingHelper.EncoderSelection? selection,
            int targetFps)
        {
            List<EncodingHelper.EncoderCandidate> all = candidates.ToList();
            List<EncodingHelper.EncoderCandidate> visible = all
                .Where(candidate => candidate.MeetsRealtimeRequirement)
                .ToList();
            if (visible.Count == 0)
            {
                EncodingHelper.EncoderCandidate? fallback = all.FirstOrDefault(candidate =>
                    string.Equals(candidate.Encoder, selection?.Encoder, StringComparison.OrdinalIgnoreCase));
                fallback ??= EncodingHelper.SelectEncoder(all, "auto", "") is { } automaticFallback
                    ? all.FirstOrDefault(candidate => string.Equals(
                        candidate.Encoder,
                        automaticFallback.Encoder,
                        StringComparison.OrdinalIgnoreCase))
                    : null;
                if (fallback != null)
                    visible.Add(fallback);
            }

            return visible
                .OrderByDescending(candidate => string.Equals(candidate.Encoder, selection?.Encoder, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(candidate => candidate.MeetsRealtimeRequirement)
                .ThenByDescending(candidate => candidate.MeasuredEncodingFps)
                .ThenBy(candidate => candidate.Encoder, StringComparer.OrdinalIgnoreCase)
                .Select(candidate => new VideoEncoderOption
                {
                    Value = candidate.Encoder,
                    Codec = candidate.Codec,
                    IsHardware = candidate.IsHardware,
                    MeasuredEncodingFps = candidate.MeasuredEncodingFps,
                    MeetsRealtimeRequirement = candidate.MeetsRealtimeRequirement,
                    PerformanceText = candidate.MeetsRealtimeRequirement
                        ? $"{candidate.MeasuredEncodingFps:F1} FPS，满足 {targetFps} FPS 的 20% 余量"
                        : $"{candidate.MeasuredEncodingFps:F1} FPS，未满足 {targetFps} FPS 的 20% 余量",
                    DisplayName =
                        $"{EncodingHelper.GetEncoderLabel(candidate.Encoder)} ({candidate.Encoder}) · " +
                        $"{candidate.MeasuredEncodingFps:F1} FPS"
                })
                .ToList();
        }

        internal static bool TryResolveCachedEncoder(
            AppConfig config,
            IEnumerable<string> validatedEncoders,
            NativeCameraMode targetMode,
            out EncodingHelper.EncoderSelection? selection)
        {
            selection = null;
            if (!config.IsEncoderDetected
                || config.EncoderDetectionCacheVersion != CurrentEncoderDetectionCacheVersion)
            {
                return false;
            }

            int cqp = RecordingProfileDetector.NormalizeVideoCqp(config.VideoCqp);
            List<string> encoders = validatedEncoders
                .Where(EncodingHelper.IsKnownEncoder)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (encoders.Count == 0)
                return false;

            var benchmarks = new Dictionary<string, RealtimeEncodingBenchmarkResult>(StringComparer.OrdinalIgnoreCase);
            foreach (string encoder in encoders)
            {
                if (!RecordingProfileDetector.TryGetCachedBenchmark(config, encoder, cqp, targetMode, out RecordingBenchmarkCacheEntry cached))
                    return false;

                benchmarks[encoder] = new RealtimeEncodingBenchmarkResult(
                    targetMode,
                    cached.CompletedSuccessfully,
                    cached.ElapsedSeconds,
                    cached.EncodedFrames,
                    cached.MeasuredEncodingFps,
                    RecordingProfileDetector.RequiredEncodingSpeed,
                    "cached");
            }

            selection = EncodingHelper.SelectEncoder(
                BuildEncoderCandidates(encoders, benchmarks, targetMode),
                config.VideoEncoderSelectionMode,
                config.ManualVideoEncoder);
            return selection != null;
        }

        internal static void AppendEncoderPerformanceLog(
            NativeCameraMode targetMode,
            int videoCqp,
            IEnumerable<EncodingHelper.EncoderCandidate> candidates,
            EncodingHelper.EncoderSelection? selection,
            string? failureReason)
        {
            try
            {
                var log = new StringBuilder();
                log.AppendLine();
                log.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] === 实际编码器性能检测 ===");
                log.AppendLine(
                    $"目标规格: {targetMode.Width}x{targetMode.Height}@{targetMode.Fps} FPS, CQP={videoCqp}, " +
                    $"实时余量要求={RecordingProfileDetector.RequiredEncodingSpeed:P0}");
                foreach (EncodingHelper.EncoderCandidate candidate in candidates
                             .OrderBy(candidate => candidate.Encoder, StringComparer.OrdinalIgnoreCase))
                {
                    log.AppendLine(
                        $"候选 {candidate.Encoder}: 硬件={candidate.IsHardware}, 格式={candidate.Codec}, " +
                        $"实测={candidate.MeasuredEncodingFps:F1} FPS, " +
                        $"满足20%余量={candidate.MeetsRealtimeRequirement}");
                }

                if (selection != null)
                {
                    log.AppendLine(
                        $"最终选择: {selection.Encoder}, 手动={selection.IsManual}, " +
                        $"满足20%余量={selection.MeetsRealtimeRequirement}, 原因={selection.Reason}");
                }
                else
                {
                    log.AppendLine($"最终选择: 无, 原因={failureReason ?? "未找到可用编码器"}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.EncoderDetectLogPath)!);
                File.AppendAllText(AppPaths.EncoderDetectLogPath, log.ToString(), Encoding.UTF8);
            }
            catch
            {
                // 日志不可写不能影响检测结果。
            }
        }

        private static (bool ok, string stderr) TestEncoder(string ffmpegPath, string encoder)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-f lavfi -i color=black:s=256x256:d=0.1 -frames:v 2 -an -pix_fmt yuv420p -c:v {encoder} -f null -",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return (false, "Process.Start returned null");
                Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
                bool exited = WaitForEncoderProbeExit(proc, 15000);
                string stderr = ReadProbeOutput(stderrTask);
                int exitCode = exited ? proc.ExitCode : -999;
                return (exited && exitCode == 0, $"exit={exitCode} stderr={stderr}");
            }
            catch (Exception ex) { return (false, $"exception: {ex.Message}"); }
        }

        internal static NvencDriverCompatibilityIssue? ParseNvencDriverCompatibilityIssue(string? stderr)
        {
            if (string.IsNullOrWhiteSpace(stderr)
                || !stderr.Contains("nvenc", StringComparison.OrdinalIgnoreCase)
                || (!stderr.Contains("required nvenc API version", StringComparison.OrdinalIgnoreCase)
                    && !stderr.Contains("minimum required Nvidia driver", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            Match apiMatch = Regex.Match(
                stderr,
                @"Required:\s*(?<required>[0-9.]+)\s+Found:\s*(?<detected>[0-9.]+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            Match driverMatch = Regex.Match(
                stderr,
                @"minimum required Nvidia driver for nvenc is\s+(?<driver>[0-9.]+)\s+or newer",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            return new NvencDriverCompatibilityIssue(
                apiMatch.Success ? apiMatch.Groups["required"].Value : "",
                apiMatch.Success ? apiMatch.Groups["detected"].Value : "",
                driverMatch.Success ? driverMatch.Groups["driver"].Value : "");
        }

        internal static void UpdateEncoderDriverWarning(
            AppConfig config,
            NvencDriverCompatibilityIssue? issue)
        {
            ArgumentNullException.ThrowIfNull(config);
            config.EncoderDriverWarningCode = issue == null ? "" : NvencDriverTooOldWarningCode;
            config.EncoderDriverRequiredApiVersion = issue?.RequiredApiVersion ?? "";
            config.EncoderDriverDetectedApiVersion = issue?.DetectedApiVersion ?? "";
            config.EncoderDriverMinimumVersion = issue?.MinimumDriverVersion ?? "";
        }

        internal static string? BuildEncoderDriverWarningMessage(AppConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            if (!string.Equals(
                    config.EncoderDriverWarningCode,
                    NvencDriverTooOldWarningCode,
                    StringComparison.Ordinal))
            {
                return null;
            }

            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(config.EncoderDriverRequiredApiVersion)
                && !string.IsNullOrWhiteSpace(config.EncoderDriverDetectedApiVersion))
            {
                details.Add(
                    $"NVENC API：需要 {config.EncoderDriverRequiredApiVersion}，当前驱动仅提供 {config.EncoderDriverDetectedApiVersion}");
            }
            if (!string.IsNullOrWhiteSpace(config.EncoderDriverMinimumVersion))
                details.Add($"FFmpeg 要求 NVIDIA 驱动版本不低于 {config.EncoderDriverMinimumVersion}");

            string detailText = details.Count == 0
                ? "当前 NVIDIA 驱动无法满足 FFmpeg 的 NVENC 版本要求"
                : string.Join("\n", details);
            return
                "已检测到 NVIDIA 显卡驱动与当前 FFmpeg 的 NVENC 版本不兼容。\n\n" +
                detailText + "\n\n" +
                "请升级 NVIDIA 显卡驱动，然后在设置中重新检测编码器";
        }

        private static bool WaitForEncoderProbeExit(Process process, int timeoutMs)
        {
            if (process.WaitForExit(timeoutMs))
                return true;

            try { process.Kill(entireProcessTree: true); }
            catch { }
            try { process.WaitForExit(3000); }
            catch { }
            return false;
        }

        private static string ReadProbeOutput(Task<string> outputTask)
        {
            try
            {
                return outputTask.Wait(TimeSpan.FromSeconds(3)) ? outputTask.Result ?? "" : "";
            }
            catch
            {
                return "";
            }
        }

        internal static EncoderDetectionResult DetectAvailableEncodersSync()
        {
            return DetectAvailableEncodersSync(AppPaths.FindFFmpeg());
        }

        internal static EncoderDetectionResult DetectAvailableEncodersSync(string? ffmpegPath)
        {
            var log = new StringBuilder();
            log.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] === 实际 FFmpeg 编码器能力检测开始 ===");

            var list = new List<VideoEncoderOption>();
            var validated = new HashSet<string>();
            NvencDriverCompatibilityIssue? nvencDriverIssue = null;

            log.AppendLine($"FFmpeg 路径: {ffmpegPath}");
            log.AppendLine($"FFmpeg 存在: {!string.IsNullOrEmpty(ffmpegPath) && File.Exists(ffmpegPath)}");

            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                log.AppendLine("FFmpeg 不存在，跳过检测");
                WriteEncoderLog(log);
                return new EncoderDetectionResult(list, validated, false, null);
            }

            string output = QueryFFmpegEncoders(ffmpegPath);
            log.AppendLine($"ffmpeg -encoders 输出长度: {output.Length}");

            foreach (string encoder in new[] { "libx264", "libx265", "libsvtav1", "libaom-av1" })
            {
                bool inList = IsEncoderListed(output, encoder);
                log.AppendLine($"\n=== CPU {encoder} ===");
                log.AppendLine($"  ffmpeg -encoders 包含: {inList}");
                if (!inList)
                {
                    log.AppendLine("  跳过试编码（不在编码器列表中）");
                    continue;
                }

                var (testOk, testDetail) = TestEncoder(ffmpegPath, encoder);
                log.AppendLine($"  试编码结果: {(testOk ? "✓ 通过" : "✗ 失败")}");
                log.AppendLine($"  详情: {testDetail}");
                if (testOk)
                    validated.Add(encoder);
            }

            var gpuGroups = new[]
            {
                (gpu: "nvidia", label: "NVIDIA GPU (NVENC)",  encs: new[] { "h264_nvenc", "hevc_nvenc", "av1_nvenc" }),
                (gpu: "amd",    label: "AMD GPU (AMF)",       encs: new[] { "h264_amf",   "hevc_amf",   "av1_amf" }),
                (gpu: "intel",  label: "Intel GPU (QSV)",     encs: new[] { "h264_qsv",   "hevc_qsv",   "av1_qsv" })
            };

            foreach (var (gpu, label, encs) in gpuGroups)
            {
                log.AppendLine($"\n=== {label} ===");
                foreach (var enc in encs)
                {
                    bool inList = IsEncoderListed(output, enc);
                    log.AppendLine($"  --- {enc} ---");
                    log.AppendLine($"    ffmpeg -encoders 包含: {inList}");

                    if (!inList)
                    {
                        log.AppendLine($"    跳过试编码（不在编码器列表中）");
                        continue;
                    }

                    var (testOk, testDetail) = TestEncoder(ffmpegPath, enc);
                    log.AppendLine($"    试编码结果: {(testOk ? "✓ 通过" : "✗ 失败")}");
                    log.AppendLine($"    详情: {testDetail}");

                    if (testOk)
                    {
                        validated.Add(enc);
                    }
                    else if (enc.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase))
                    {
                        nvencDriverIssue ??= ParseNvencDriverCompatibilityIssue(testDetail);
                    }
                }

            }

            list.AddRange(validated
                .OrderBy(encoder => encoder, StringComparer.OrdinalIgnoreCase)
                .Select(encoder => new VideoEncoderOption
                {
                    Value = encoder,
                    Codec = EncodingHelper.GetCodecFromEncoder(encoder),
                    IsHardware = EncodingHelper.IsHardwareEncoder(encoder),
                    DisplayName = EncodingHelper.GetEncoderLabel(encoder)
                }));

            log.AppendLine($"\n已验证实际编码器: {string.Join(", ", list.Select(e => e.Value))}");
            log.AppendLine($"已验证编码器: {string.Join(", ", validated)}");
            log.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] === 检测结束 ===");
            WriteEncoderLog(log);
            return new EncoderDetectionResult(list, validated, true, nvencDriverIssue);
        }

        private static bool IsEncoderListed(string output, string encoder)
        {
            return !string.IsNullOrWhiteSpace(output)
                && Regex.IsMatch(
                    output,
                    $@"(?m)^\s*[VAS\.][A-Z\.]{5}\s+{Regex.Escape(encoder)}(?:\s|$)",
                    RegexOptions.CultureInvariant);
        }

        private static void WriteEncoderLog(StringBuilder log)
        {
            try
            {
                string logPath = AppPaths.EncoderDetectLogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }
}
