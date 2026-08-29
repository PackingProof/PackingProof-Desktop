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
        List<GpuEncoderOption> Options,
        HashSet<string> ValidatedEncoders,
        Dictionary<string, double> PerformanceScores,
        List<EncoderPerformanceCacheEntry> PerformanceResults,
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
        internal const int CurrentEncoderDetectionCacheVersion = 4;
        internal const int EncoderScoreSchemaVersion = 1;
        internal const int EncoderScoreCqp = 30;
        internal static readonly NativeCameraMode EncoderScoreMode = new(1280, 720, 15);
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

        public async Task ResetEncoderDetectAsync()
        {
            RecordingProfileRecommendation? recommendation =
                await DetectAndRecommendRecordingProfileAsync(Config, null);
            ShowToast(
                recommendation?.Message ?? "录制性能检测失败，已保留现有设置",
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
                    CachedEncoderOptions = detection.Options;
                    ValidatedEncoders = detection.ValidatedEncoders;
                    EncoderPerformanceScores = detection.PerformanceScores;

                    // 检测缓存属于设备能力，可立即持久化；推荐规格必须由设置页确认后再写入。
                    Config.EncoderOptionsCache = detection.Options;
                    Config.ValidatedEncodersCache = detection.ValidatedEncoders.ToList();
                    Config.EncoderPerformanceCache = detection.PerformanceResults;
                    Config.IsEncoderDetected = detection.Succeeded;
                    Config.EncoderDetectionCacheVersion = CurrentEncoderDetectionCacheVersion;
                    detectionConfig.EncoderOptionsCache = detection.Options;
                    detectionConfig.ValidatedEncodersCache = detection.ValidatedEncoders.ToList();
                    detectionConfig.EncoderPerformanceCache = detection.PerformanceResults;
                    detectionConfig.IsEncoderDetected = detection.Succeeded;
                    detectionConfig.EncoderDetectionCacheVersion = CurrentEncoderDetectionCacheVersion;
                    UpdateEncoderDriverWarning(Config, detection.NvencDriverIssue);
                    if (!ReferenceEquals(Config, detectionConfig))
                        UpdateEncoderDriverWarning(detectionConfig, detection.NvencDriverIssue);
                    if (!detection.Succeeded)
                    {
                        SaveConfig();
                        return new RecordingProfileRecommendation(
                            false,
                            null,
                            "未检测到可用的 FFmpeg 编码器，已保留当前配置",
                            []);
                    }

                    string codec = (detectionConfig.VideoCodec ?? "h264").Trim().ToLowerInvariant();
                    if (codec is not ("h264" or "h265" or "av1"))
                        codec = "h264";
                    if (!EncodingHelper.TryResolveEncoder(
                        detectionConfig.GpuEncoder ?? "auto",
                        codec,
                        detection.ValidatedEncoders,
                        detection.PerformanceScores,
                        out string encoder))
                    {
                        SaveConfig();
                        return new RecordingProfileRecommendation(false, null, "当前选择未通过编码器检测", []);
                    }
                    int videoCqp = RecordingProfileDetector.NormalizeVideoCqp(detectionConfig.VideoCqp);
                    string ffmpegPath = AppPaths.FindFFmpeg();
                    RuntimeLog.Info(
                        "RecordingProfile",
                        $"manual ffmpeg={ffmpegPath}, encoder={encoder}, cqp={videoCqp}");
                    RecordingProfileRecommendation recommendation = RecordingProfileDetector.Recommend(
                        nativeModes,
                        mode => RecordingProfileDetector.Benchmark(
                            ffmpegPath,
                            encoder,
                            videoCqp,
                            mode));
                    DateTime testedAt = DateTime.Now;
                    RecordingProfileDetector.UpdateBenchmarkCache(
                        detectionConfig,
                        encoder,
                        videoCqp,
                        recommendation.Benchmarks,
                        testedAt);
                    if (!ReferenceEquals(Config, detectionConfig))
                    {
                        RecordingProfileDetector.UpdateBenchmarkCache(
                            Config,
                            encoder,
                            videoCqp,
                            recommendation.Benchmarks,
                            testedAt);
                    }
                    SaveConfig();
                    foreach (RealtimeEncodingBenchmarkResult benchmark in recommendation.Benchmarks)
                    {
                        RuntimeLog.Info(
                            "RecordingProfile",
                            $"manual mode={benchmark.Mode.Width}x{benchmark.Mode.Height}@{benchmark.Mode.Fps}, encoder={encoder}, stable={benchmark.Stable}, detail={benchmark.Detail}");
                    }

                    if (recommendation.Success && recommendation.Mode is NativeCameraMode recommendedMode)
                    {
                        RuntimeLog.Info(
                            "RecordingProfile",
                            $"manual recommended={recommendedMode.Width}x{recommendedMode.Height}@{recommendedMode.Fps}, encoder={encoder}");
                    }

                    return recommendation;
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
                    EncodingHelper.NormalizeGpuSetting(config.GpuEncoder),
                    "nvidia",
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
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
                "请升级 NVIDIA 显卡驱动，然后在设置中重新检测编码器或选择其他可用编码器";
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
            log.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] === GPU 编码器检测开始 ===");

            var list = new List<GpuEncoderOption>();
            var validated = new HashSet<string>();
            var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var performanceResults = new List<EncoderPerformanceCacheEntry>();
            NvencDriverCompatibilityIssue? nvencDriverIssue = null;

            log.AppendLine($"FFmpeg 路径: {ffmpegPath}");
            log.AppendLine($"FFmpeg 存在: {!string.IsNullOrEmpty(ffmpegPath) && File.Exists(ffmpegPath)}");

            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                log.AppendLine("FFmpeg 不存在，跳过检测");
                WriteEncoderLog(log);
                return new EncoderDetectionResult(list, validated, scores, performanceResults, false, null);
            }

            string output = QueryFFmpegEncoders(ffmpegPath);
            log.AppendLine($"ffmpeg -encoders 输出长度: {output.Length}");

            foreach (string encoder in new[] { "libx264", "libx265", "libsvtav1" })
            {
                bool inList = output.Contains(encoder, StringComparison.Ordinal);
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
                bool anyPassed = false;

                foreach (var enc in encs)
                {
                    bool inList = output.Contains(enc);
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
                        anyPassed = true;
                        RealtimeEncodingBenchmarkResult benchmark = RecordingProfileDetector.Benchmark(
                            ffmpegPath, enc, EncoderScoreCqp, EncoderScoreMode);
                        if (benchmark.CompletedSuccessfully && benchmark.MeasuredEncodingFps > 0)
                            scores[enc] = benchmark.MeasuredEncodingFps;
                        performanceResults.Add(CreateEncoderPerformanceResult(gpu, enc, benchmark));
                    }
                    else if (enc.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase))
                    {
                        nvencDriverIssue ??= ParseNvencDriverCompatibilityIssue(testDetail);
                        performanceResults.Add(CreateEncoderPerformanceFailure(gpu, enc, testDetail));
                    }
                    else
                        performanceResults.Add(CreateEncoderPerformanceFailure(gpu, enc, testDetail));
                }

                if (anyPassed && encs.Any(scores.ContainsKey))
                    list.Add(new GpuEncoderOption { Value = gpu, DisplayName = label });
            }

            if (scores.Count > 0)
                list.Insert(0, new GpuEncoderOption { Value = "auto", DisplayName = "自动检测（按实测性能）" });
            if (validated.Any(encoder => encoder is "libx264" or "libx265" or "libsvtav1"))
                list.Add(new GpuEncoderOption { Value = "cpu", DisplayName = "CPU 软编码" });

            log.AppendLine($"\nGPU 选项: {string.Join(", ", list.Select(e => e.Value))}");
            log.AppendLine($"已验证编码器: {string.Join(", ", validated)}");
            log.AppendLine($"性能评分: {string.Join(", ", scores.Select(pair => $"{pair.Key}={pair.Value:F2}fps"))}");
            log.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] === 检测结束 ===");
            WriteEncoderLog(log);
            return new EncoderDetectionResult(list, validated, scores, performanceResults, true, nvencDriverIssue);
        }

        private static EncoderPerformanceCacheEntry CreateEncoderPerformanceResult(
            string gpu,
            string encoder,
            RealtimeEncodingBenchmarkResult benchmark)
        {
            return new EncoderPerformanceCacheEntry
            {
                SchemaVersion = EncoderScoreSchemaVersion,
                Encoder = encoder,
                Gpu = gpu,
                Codec = EncodingHelper.GetCodecFromEncoder(encoder),
                VideoCqp = EncoderScoreCqp,
                Width = EncoderScoreMode.Width,
                Height = EncoderScoreMode.Height,
                CompletedSuccessfully = benchmark.CompletedSuccessfully,
                MeasuredEncodingFps = benchmark.MeasuredEncodingFps,
                TestedAt = DateTime.Now,
                Detail = benchmark.Detail
            };
        }

        private static EncoderPerformanceCacheEntry CreateEncoderPerformanceFailure(
            string gpu,
            string encoder,
            string detail) =>
            new()
            {
                SchemaVersion = EncoderScoreSchemaVersion,
                Encoder = encoder,
                Gpu = gpu,
                Codec = EncodingHelper.GetCodecFromEncoder(encoder),
                VideoCqp = EncoderScoreCqp,
                Width = EncoderScoreMode.Width,
                Height = EncoderScoreMode.Height,
                CompletedSuccessfully = false,
                TestedAt = DateTime.Now,
                Detail = detail
            };
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
