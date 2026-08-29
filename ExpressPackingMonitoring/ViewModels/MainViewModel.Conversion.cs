using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Audio;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.Localization;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using OpenCvSharp;
using AForge.Video.DirectShow;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ExpressPackingMonitoring.ViewModels
{
    public partial class MainViewModel
    {
        private void ConvertMkvToMp4(string mkvPath, string? audioPath = null, string? audioLogPath = null, bool audioFailed = false, long audioBytesWritten = 0)
        {
            try
            {
                if (!File.Exists(mkvPath) || !mkvPath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
                    return;

                string ffmpegPath = FindFFmpeg();
                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    Debug.WriteLine("[MkvToMp4] FFmpeg 未找到，跳过自动转换");
                    return;
                }

                string mp4Path = Path.ChangeExtension(mkvPath, ".mp4");
                if (audioFailed)
                {
                    WriteAudioDiagnostic($"音频录制失败，已保留 MKV: mkv={mkvPath}", audioLogPath);
                    _ = Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (!_isDisposed)
                            ShowToast("音频录制失败，已保留原始文件", ToastSeverity.Error);
                    });
                    return;
                }
                if (!ValidateAudioCaptureForMux(audioPath, audioLogPath, audioBytesWritten))
                {
                    Debug.WriteLine("[MkvToMp4] WAV 音频时间线异常，继续生成 MP4 并记录诊断");
                }

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = BuildMkvToMp4Args(mkvPath, audioPath, mp4Path, Config.AudioSyncOffsetMs, Config.VideoCodec),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    Debug.WriteLine("[MkvToMp4] FFmpeg 进程启动失败");
                    return;
                }

                string convertStderr;
                bool convertExited = WaitForProcessExit(process, GetMediaProcessTimeoutMs(mkvPath, audioPath), out convertStderr);

                bool hasExternalAudio = !string.IsNullOrEmpty(audioPath) && File.Exists(audioPath);
                bool hasEmbeddedAudio = HasEmbeddedAudioMarker(mkvPath);
                bool convertedOk = convertExited
                    && process.ExitCode == 0
                    && File.Exists(mp4Path)
                    && new FileInfo(mp4Path).Length > 0
                    && ValidateConvertedMp4(ffmpegPath, mp4Path, hasExternalAudio || hasEmbeddedAudio, audioLogPath);
                if (!convertExited)
                    WriteAudioDiagnostic($"MP4 转换超时: {convertStderr}", audioLogPath);

                if (convertedOk)
                {
                    // 转换成功，删除原始 MKV
                    try { File.Delete(mkvPath); } catch { }
                    DeleteAudioTempFile(audioPath);
                    DeleteEmbeddedAudioMarker(mkvPath);
                    // 更新数据库中的文件路径
                    _db?.UpdateVideoFilePath(mkvPath, mp4Path);
                    Debug.WriteLine($"[MkvToMp4] 转换成功: {mp4Path}");
                }
                else
                {
                    // 仅在没有可接受 MP4 时保留 MKV；音轨诊断异常不会进入这里。
                    try { if (File.Exists(mp4Path)) File.Delete(mp4Path); } catch { }
                    Debug.WriteLine($"[MkvToMp4] 转换失败，保留原始 MKV");
                    WriteAudioDiagnostic($"MP4 容器转换失败，已保留 MKV/WAV: mkv={mkvPath}, wav={audioPath}", audioLogPath);
                    _ = Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (!_isDisposed)
                            ShowToast("录像未生成兼容 MP4，原始录像已保留", ToastSeverity.Warning);
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MkvToMp4] 异常: {ex.Message}");
            }
        }

        public MkvConversionResult ConvertRecordMkvToMp4(VideoRecord record)
        {
            if (record == null) return MkvConversionResult.Fail("录像记录不存在");
            string filePath = record.FilePath;
            if (filePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                string mkvPath = Path.ChangeExtension(filePath, ".mkv");
                if (File.Exists(mkvPath) && HasMuxRecoverySidecar(mkvPath))
                {
                    RuntimeLog.Warn("MkvToMp4", $"Record points to MP4 but MKV sidecar remains, recovering file={Path.GetFileName(mkvPath)}");
                    filePath = mkvPath;
                }
            }

            MkvConversionResult result = ConvertMkvToMp4ForPlayback(
                filePath,
                videoCodec: record.VideoCodec);
            if (result.Success)
                _db?.ClearMkvConversionFailure(filePath);
            else
                _db?.RecordMkvConversionFailure(filePath, DateTime.Now, result.ErrorMessage);
            return result;
        }

        private MkvConversionResult ConvertMkvToMp4ForPlayback(
            string mkvPath,
            CancellationToken cancellationToken = default,
            string? videoCodec = null)
        {
            bool lockTaken = false;
            try
            {
                _mkvConvertLock.Wait(cancellationToken);
                lockTaken = true;
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(mkvPath))
                    return MkvConversionResult.Fail("MKV 文件不存在", mkvPath ?? "");

                if (!File.Exists(mkvPath))
                {
                    string concurrentMp4Path = Path.ChangeExtension(mkvPath, ".mp4");
                    if (IsCompletedConcurrentMkvConversion(mkvPath, concurrentMp4Path))
                    {
                        _db?.UpdateVideoFilePath(mkvPath, concurrentMp4Path);
                        RuntimeLog.Info(
                            "MkvToMp4",
                            $"Conversion already completed by another task, preserving MP4 file={Path.GetFileName(concurrentMp4Path)}");
                        return MkvConversionResult.Ok(concurrentMp4Path, alreadyConverted: true);
                    }

                    return MkvConversionResult.Fail("MKV 文件不存在", mkvPath);
                }
                if (!mkvPath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
                    return MkvConversionResult.Ok(mkvPath, alreadyConverted: true);

                string mp4Path = Path.ChangeExtension(mkvPath, ".mp4");
                MkvAudioMuxPlan audioPlan = ResolveMkvAudioMuxPlan(mkvPath);
                string? audioPath = audioPlan.AudioPath;
                string? audioLogPath = audioPlan.AudioLogPath;

                if (audioPlan.Mode == MkvAudioMuxMode.EmbeddedOrVideoOnly
                    && audioLogPath != null)
                {
                    // 历史录像可能只留下音频诊断文件而没有外部 WAV。继续封装 MKV
                    // 自带的可选音轨；如果源文件没有音轨，也要优先生成可播放的视频 MP4。
                    RuntimeLog.Warn("MkvToMp4", $"Audio diagnostic sidecar exists without WAV, continuing compatible MP4 conversion file={Path.GetFileName(mkvPath)}");
                }

                if (File.Exists(mp4Path) && new FileInfo(mp4Path).Length > 0)
                {
                    if (HasMuxRecoverySidecar(audioPath, audioLogPath))
                    {
                        RuntimeLog.Warn("MkvToMp4", $"Existing MP4 may be incomplete, rebuilding from MKV/WAV file={Path.GetFileName(mkvPath)}");
                        try { File.Delete(mp4Path); } catch { }
                    }
                    else
                    {
                        try { File.Delete(mkvPath); } catch { }
                        DeleteAudioTempFile(audioPath);
                        _db?.UpdateVideoFilePath(mkvPath, mp4Path);
                        return MkvConversionResult.Ok(mp4Path, alreadyConverted: true);
                    }
                }

                string ffmpegPath = FindFFmpeg();
                if (string.IsNullOrEmpty(ffmpegPath))
                    return MkvConversionResult.Fail("未找到 FFmpeg，无法转换", mkvPath);

                // 音轨时间线检查用于诊断，不阻断 MP4 容器转换；否则一个可播放的
                // MP4 会因为音频静音片段被误判为失败，最终只留下设备不兼容的 MKV。
                if (!ValidateAudioCaptureForMux(audioPath, audioLogPath, 0))
                    RuntimeLog.Warn("MkvToMp4", $"Audio capture timeline warning, continuing conversion file={Path.GetFileName(mkvPath)}");

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = BuildMkvToMp4Args(mkvPath, audioPath, mp4Path, Config.AudioSyncOffsetMs, videoCodec),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return MkvConversionResult.Fail("FFmpeg 进程启动失败", mkvPath);

                string stderr;
                bool exited = WaitForProcessExit(process, GetMediaProcessTimeoutMs(mkvPath, audioPath), cancellationToken, out stderr);
                bool hasExternalAudio = !string.IsNullOrEmpty(audioPath) && File.Exists(audioPath);
                bool hasEmbeddedAudio = HasEmbeddedAudioMarker(mkvPath);
                bool ok = exited
                    && process.ExitCode == 0
                    && File.Exists(mp4Path)
                    && new FileInfo(mp4Path).Length > 0
                    && ValidateConvertedMp4(ffmpegPath, mp4Path, hasExternalAudio || hasEmbeddedAudio, audioLogPath, cancellationToken);

                if (!ok)
                {
                    try { if (File.Exists(mp4Path)) File.Delete(mp4Path); } catch { }
                    WriteAudioDiagnostic($"Web/后台 MKV 转 MP4 失败: mkv={mkvPath}, wav={audioPath}, stderr={stderr}", audioLogPath);
                    RuntimeLog.Warn("MkvToMp4", $"Convert failed file={Path.GetFileName(mkvPath)}, exited={exited}, exitCode={(exited ? process.ExitCode : -999)}, stderr={TrimForRuntimeLog(stderr)}");
                    return MkvConversionResult.Fail(exited ? "MP4 转换或视频校验失败" : "MP4 转换超时", mkvPath);
                }

                try { File.Delete(mkvPath); } catch { }
                DeleteAudioTempFile(audioPath);
                DeleteEmbeddedAudioMarker(mkvPath);
                _db?.UpdateVideoFilePath(mkvPath, mp4Path);
                RuntimeLog.Info("MkvToMp4", $"Converted file={Path.GetFileName(mkvPath)}, audio={(hasExternalAudio || hasEmbeddedAudio ? "yes" : "no")}");
                return MkvConversionResult.Ok(mp4Path);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                DeleteIncompleteMp4WhileSourceIsPreserved(mkvPath);
                RuntimeLog.Error("MkvToMp4", $"Convert exception file={Path.GetFileName(mkvPath ?? "")}", ex);
                return MkvConversionResult.Fail(ex.Message, mkvPath ?? "");
            }
            finally
            {
                if (lockTaken)
                    _mkvConvertLock.Release();
            }
        }

        internal static bool IsCompletedConcurrentMkvConversion(string? mkvPath, string? mp4Path)
        {
            if (string.IsNullOrWhiteSpace(mkvPath) || string.IsNullOrWhiteSpace(mp4Path))
                return false;

            try
            {
                return !File.Exists(mkvPath)
                    && File.Exists(mp4Path)
                    && new FileInfo(mp4Path).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        internal static void DeleteIncompleteMp4WhileSourceIsPreserved(string? mkvPath)
        {
            if (string.IsNullOrWhiteSpace(mkvPath) || !File.Exists(mkvPath))
                return;

            try
            {
                string mp4Path = Path.ChangeExtension(mkvPath, ".mp4");
                if (File.Exists(mp4Path))
                    File.Delete(mp4Path);
            }
            catch { }
        }

        private static string? ResolveSidecarPath(string mkvPath, string extension)
        {
            string path = Path.ChangeExtension(mkvPath, extension);
            return File.Exists(path) ? path : null;
        }

        internal static MkvAudioMuxPlan ResolveMkvAudioMuxPlan(string mkvPath)
        {
            string? audioPath = ResolveSidecarPath(mkvPath, ".wav");
            string? audioLogPath = ResolveSidecarPath(mkvPath, ".audio.log");
            return new MkvAudioMuxPlan(
                audioPath,
                audioLogPath,
                audioPath == null
                    ? MkvAudioMuxMode.EmbeddedOrVideoOnly
                    : MkvAudioMuxMode.ExternalWav);
        }

        internal enum MkvAudioMuxMode
        {
            ExternalWav,
            EmbeddedOrVideoOnly
        }

        internal sealed record MkvAudioMuxPlan(
            string? AudioPath,
            string? AudioLogPath,
            MkvAudioMuxMode Mode);

        private static bool HasMuxRecoverySidecar(string mkvPath)
        {
            return File.Exists(Path.ChangeExtension(mkvPath, ".wav"))
                || File.Exists(Path.ChangeExtension(mkvPath, ".audio.log"))
                || HasEmbeddedAudioMarker(mkvPath);
        }

        private static bool HasMuxRecoverySidecar(string? audioPath, string? audioLogPath)
        {
            return (!string.IsNullOrWhiteSpace(audioPath) && File.Exists(audioPath))
                || (!string.IsNullOrWhiteSpace(audioLogPath) && File.Exists(audioLogPath));
        }

        internal static string BuildMkvToMp4Args(
            string mkvPath,
            string? audioPath,
            string mp4Path,
            int audioSyncOffsetMs,
            string? videoCodec = null)
        {
            // 苹果解码器只认 hvc1 标签，NVENC 默认写出的 hev1 在 iOS 上无法解码；
            // 仅在确认为 HEVC 时为视频轨写 hvc1，其他编码保持 movenc 默认标签。
            string hevcTag = videoCodec is "h265" or "hevc"
                ? " -tag:v hvc1"
                : "";

            if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath))
                return $"-y -i \"{mkvPath}\" -map 0:v:0 -map 0:a? -c copy{hevcTag} \"{mp4Path}\"";

            int offsetMs = Math.Clamp(audioSyncOffsetMs, -5000, 5000);
            string audioMap = "[a]";
            string filter;

            if (offsetMs > 0)
            {
                filter = $" -filter_complex \"[1:a]adelay={offsetMs}:all=1,apad[a]\"";
            }
            else if (offsetMs < 0)
            {
                double trimStartSec = Math.Abs(offsetMs) / 1000.0;
                filter = $" -filter_complex \"[1:a]atrim=start={trimStartSec:0.###},asetpts=PTS-STARTPTS,apad[a]\"";
            }
            else
            {
                filter = " -filter_complex \"[1:a]apad[a]\"";
            }

            return $"-y -i \"{mkvPath}\" -i \"{audioPath}\"{filter} -map 0:v:0 -map \"{audioMap}\" -c:v copy{hevcTag} -c:a aac -b:a 128k -shortest \"{mp4Path}\"";
        }

        private bool ValidateConvertedMp4(
            string ffmpegPath,
            string mp4Path,
            bool requireAudio,
            string? audioLogPath,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(mp4Path) || new FileInfo(mp4Path).Length <= 0) return false;

            // 仅把 FFmpeg 的成功退出和非空文件作为候选结果，再实际解码一帧视频，
            // 避免把损坏容器误切换成主文件。
            try
            {
                var videoProbe = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = BuildMp4VideoProbeArgs(mp4Path),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using var probeProcess = Process.Start(videoProbe);
                if (probeProcess == null)
                    return false;

                string probeStderr;
                bool probeExited = WaitForProcessExit(
                    probeProcess,
                    GetMediaProcessTimeoutMs(mp4Path),
                    cancellationToken,
                    out probeStderr);
                if (!probeExited || probeProcess.ExitCode != 0)
                {
                    RuntimeLog.Warn("MkvToMp4", $"MP4 video probe failed file={Path.GetFileName(mp4Path)}, error={TrimForRuntimeLog(probeStderr)}");
                    return false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("MkvToMp4", $"MP4 video probe exception file={Path.GetFileName(mp4Path)}, error={ex.Message}");
                return false;
            }

            if (!requireAudio) return true;

            string decodedWavPath = Path.Combine(
                Path.GetDirectoryName(mp4Path) ?? AppDomain.CurrentDomain.BaseDirectory,
                $"{Path.GetFileNameWithoutExtension(mp4Path)}.mp4_audio_check_{Guid.NewGuid():N}.wav");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-y -v error -i \"{mp4Path}\" -map 0:a:0 -ac 1 -ar 48000 -sample_fmt s16 \"{decodedWavPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    Debug.WriteLine("[MkvToMp4] 音轨校验进程启动失败");
                    WriteAudioDiagnostic("MP4 音轨无法启动校验，保留可用容器结果", audioLogPath);
                    return true;
                }

                string stderr;
                bool exited = WaitForProcessExit(process, GetMediaProcessTimeoutMs(mp4Path), cancellationToken, out stderr);

                if (!exited)
                {
                    try { process.Kill(); } catch { }
                    Debug.WriteLine("[MkvToMp4] 音轨校验超时");
                    WriteAudioDiagnostic("MP4 音轨校验超时，保留可用容器结果", audioLogPath);
                    return true;
                }

                bool ok = process.ExitCode == 0;
                WriteAudioDiagnostic($"MP4 audio validation result: ok={ok}, exitCode={process.ExitCode}", audioLogPath);
                if (!ok)
                {
                    Debug.WriteLine($"[MkvToMp4] 音轨校验失败: {stderr}");
                    WriteAudioDiagnostic($"MP4 音轨校验失败: {stderr}");
                    WriteAudioDiagnostic("MP4 音轨解码校验失败，保留可用容器结果", audioLogPath);
                    return true;
                }

                using var reader = new WaveFileReader(decodedWavPath);
                WriteAudioDiagnostic($"MP4 解码音轨: duration={reader.TotalTime.TotalSeconds:F2}s, format={reader.WaveFormat}, bytes={reader.Length}", audioLogPath);
                var timeline = LogAudioPeakTimeline(reader, audioLogPath, "MP4");
                if (!IsAudioTimelineUsable(reader.TotalTime.TotalSeconds, timeline.FirstActiveSecond, timeline.LastActiveSecond, timeline.ActiveWindowCount, timeline.MaxConsecutiveActiveWindows, out string reason))
                {
                    WriteAudioDiagnostic($"MP4 音轨疑似提前静音: {reason}", audioLogPath);
                    return true;
                }

                WriteAudioDiagnostic("MP4 音轨完整解码和时间线校验通过", audioLogPath);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MkvToMp4] 音轨校验异常: {ex.Message}");
                WriteAudioDiagnostic($"MP4 音轨校验异常: {ex.Message}");
                WriteAudioDiagnostic("MP4 音轨校验异常，保留可用容器结果", audioLogPath);
                return true;
            }
            finally
            {
                try { if (File.Exists(decodedWavPath)) File.Delete(decodedWavPath); } catch { }
            }
        }

        internal static string BuildMp4VideoProbeArgs(string mp4Path) =>
            $"-v error -i \"{mp4Path}\" -map 0:v:0 -frames:v 1 -f null -";

        private bool ValidateAudioCaptureForMux(string? audioPath, string? audioLogPath, long audioBytesWritten)
        {
            if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath)) return true;

            var summary = LogAudioCaptureSummary(audioPath, audioLogPath, audioBytesWritten);
            if (!summary.Checked) return true;

            if (!IsAudioTimelineUsable(summary.DurationSeconds, summary.FirstActiveSecond, summary.LastActiveSecond, summary.ActiveWindowCount, summary.MaxConsecutiveActiveWindows, out string reason))
            {
                WriteAudioDiagnostic($"WAV 音频疑似提前静音: {reason}", audioLogPath);
                return false;
            }
            return true;
        }

        private (bool Checked, double DurationSeconds, double FirstActiveSecond, double LastActiveSecond, int ActiveWindowCount, int MaxConsecutiveActiveWindows) LogAudioCaptureSummary(string? audioPath, string? audioLogPath, long audioBytesWritten)
        {
            if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath))
                return (false, 0, -1, -1, 0, 0);

            try
            {
                using var reader = new WaveFileReader(audioPath);
                string summary = $"WAV 时长={reader.TotalTime.TotalSeconds:F1}s 大小={new FileInfo(audioPath).Length} bytes 写入字节={audioBytesWritten}";
                Debug.WriteLine($"[Audio] {summary}");
                WriteAudioDiagnostic(summary, audioLogPath);
                var timeline = LogAudioPeakTimeline(reader, audioLogPath);
                return (true, reader.TotalTime.TotalSeconds, timeline.FirstActiveSecond, timeline.LastActiveSecond, timeline.ActiveWindowCount, timeline.MaxConsecutiveActiveWindows);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] WAV 检查失败: {ex.Message}");
                WriteAudioDiagnostic($"WAV 检查失败: {ex.Message}");
                return (false, 0, -1, -1, 0, 0);
            }
        }

        internal static bool IsAudioTimelineUsable(double durationSeconds, double firstActiveSecond, double lastActiveSecond, int activeWindowCount, int maxConsecutiveActiveWindows, out string reason)
        {
            reason = string.Empty;
            if (durationSeconds < 10) return true;

            if (lastActiveSecond < 0)
            {
                reason = $"duration={durationSeconds:F1}s, lastActive=none";
                return false;
            }

            double leadingSilentSeconds = firstActiveSecond < 0 ? durationSeconds : firstActiveSecond;
            double trailingSilentSeconds = durationSeconds - lastActiveSecond;
            int sparsePulseLimit = Math.Max(2, (int)Math.Floor(durationSeconds / 6.0));
            if (durationSeconds >= 10
                && leadingSilentSeconds >= Math.Max(5, durationSeconds * 0.5)
                && activeWindowCount <= 3)
            {
                reason = $"duration={durationSeconds:F1}s, firstActive={firstActiveSecond:F1}s, activeWindows={activeWindowCount}, leadingSilent={leadingSilentSeconds:F1}s";
                return false;
            }

            if (durationSeconds >= 10
                && activeWindowCount > 0
                && activeWindowCount <= sparsePulseLimit
                && maxConsecutiveActiveWindows <= 2)
            {
                reason = $"duration={durationSeconds:F1}s, firstActive={firstActiveSecond:F1}s, lastActive={lastActiveSecond:F1}s, activeWindows={activeWindowCount}, maxConsecutive={maxConsecutiveActiveWindows}, sparsePulseLimit={sparsePulseLimit}";
                return false;
            }

            if (durationSeconds >= 10
                && trailingSilentSeconds >= Math.Max(5, durationSeconds * 0.5)
                && activeWindowCount <= 3)
            {
                reason = $"duration={durationSeconds:F1}s, lastActive={lastActiveSecond:F1}s, activeWindows={activeWindowCount}, trailingSilent={trailingSilentSeconds:F1}s";
                return false;
            }

            if (durationSeconds >= 30
                && activeWindowCount > 0
                && activeWindowCount <= 4
                && maxConsecutiveActiveWindows <= 2)
            {
                reason = $"duration={durationSeconds:F1}s, firstActive={firstActiveSecond:F1}s, lastActive={lastActiveSecond:F1}s, activeWindows={activeWindowCount}, maxConsecutive={maxConsecutiveActiveWindows}";
                return false;
            }

            if (durationSeconds >= 30
                && trailingSilentSeconds >= 15
                && activeWindowCount <= 3)
            {
                reason = $"duration={durationSeconds:F1}s, lastActive={lastActiveSecond:F1}s, activeWindows={activeWindowCount}, trailingSilent={trailingSilentSeconds:F1}s";
                return false;
            }

            return true;
        }

        private (double FirstActiveSecond, double LastActiveSecond, int ActiveWindowCount, int MaxConsecutiveActiveWindows) LogAudioPeakTimeline(WaveFileReader reader, string? audioLogPath, string sourceLabel = "WAV")
        {
            try
            {
                reader.Position = 0;
                int bytesPerSecond = Math.Max(1, reader.WaveFormat.AverageBytesPerSecond);
                int blockAlign = Math.Max(1, reader.WaveFormat.BlockAlign);
                int windowBytes = bytesPerSecond;
                windowBytes -= windowBytes % blockAlign;
                if (windowBytes <= 0) windowBytes = bytesPerSecond;

                byte[] buffer = new byte[windowBytes];
                int windowIndex = 0;
                double firstActiveSecond = -1;
                double lastActiveSecond = -1;
                int activeWindowCount = 0;
                int consecutiveActiveWindows = 0;
                int maxConsecutiveActiveWindows = 0;
                var parts = new List<string>();

                while (true)
                {
                    int totalRead = 0;
                    while (totalRead < buffer.Length)
                    {
                        int read = reader.Read(buffer, totalRead, buffer.Length - totalRead);
                        if (read <= 0) break;
                        totalRead += read;
                    }
                    if (totalRead <= 0) break;

                    short peak;
                    bool known = TryGetAudioPeak(buffer, totalRead, reader.WaveFormat, out peak);
                    double start = windowIndex;
                    double end = Math.Min(reader.TotalTime.TotalSeconds, start + (double)totalRead / bytesPerSecond);
                    parts.Add(known ? $"{start:F0}-{end:F0}s={peak}" : $"{start:F0}-{end:F0}s=unknown");
                    if (known && peak > 32)
                    {
                        if (firstActiveSecond < 0)
                            firstActiveSecond = start;
                        lastActiveSecond = end;
                        activeWindowCount++;
                        consecutiveActiveWindows++;
                        if (consecutiveActiveWindows > maxConsecutiveActiveWindows)
                            maxConsecutiveActiveWindows = consecutiveActiveWindows;
                    }
                    else
                    {
                        consecutiveActiveWindows = 0;
                    }
                    windowIndex++;
                }

                string timeline = $"{sourceLabel} 分段峰值: {string.Join("; ", parts)}; firstActive={firstActiveSecond:F1}s; lastActive={lastActiveSecond:F1}s; activeWindows={activeWindowCount}; maxConsecutive={maxConsecutiveActiveWindows}; activeThreshold=32";
                Debug.WriteLine($"[Audio] {timeline}");
                WriteAudioDiagnostic(timeline, audioLogPath);
                return (firstActiveSecond, lastActiveSecond, activeWindowCount, maxConsecutiveActiveWindows);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Audio] {sourceLabel} 分段峰值检查失败: {ex.Message}");
                WriteAudioDiagnostic($"{sourceLabel} 分段峰值检查失败: {ex.Message}", audioLogPath);
                return (-1, -1, 0, 0);
            }
        }

        private static bool WaitForProcessExit(Process process, int timeoutMs, out string stderr)
        {
            return WaitForProcessExit(process, timeoutMs, CancellationToken.None, out stderr);
        }

        private static bool WaitForProcessExit(Process process, int timeoutMs, CancellationToken cancellationToken, out string stderr)
        {
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { }
            });

            bool exited = process.WaitForExit(timeoutMs);
            if (!exited)
            {
                try { process.Kill(); } catch { }
                try { process.WaitForExit(3000); } catch { }
            }

            stderr = TryGetProcessOutput(stderrTask);
            _ = TryGetProcessOutput(stdoutTask);
            cancellationToken.ThrowIfCancellationRequested();
            return exited;
        }

        private static string TryGetProcessOutput(Task<string> outputTask)
        {
            try
            {
                return outputTask.Wait(3000) ? outputTask.Result : "";
            }
            catch
            {
                return "";
            }
        }

        private static int GetMediaProcessTimeoutMs(params string?[] paths)
        {
            long totalBytes = 0;
            foreach (var path in paths)
            {
                try
                {
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        totalBytes += new FileInfo(path).Length;
                }
                catch { }
            }

            long totalMb = Math.Max(1, totalBytes / (1024L * 1024L));
            long timeout = 60_000L + totalMb * 500L;
            return (int)Math.Clamp(timeout, 60_000L, 600_000L);
        }

        private void WriteAudioDiagnostic(string message, string? explicitLogPath = null)
        {
#if DEBUG
            bool shouldWrite = !ShouldSkipDebugAudioDiagnostic(message, explicitLogPath);
#else
            bool shouldWrite = IsImportantAudioDiagnostic(message);
#endif

            if (!shouldWrite) return;
            Debug.WriteLine($"[AudioDiagnostic] {message}");
            if (IsImportantAudioDiagnostic(message))
                RuntimeLog.Warn("Audio", message);
            string? logPath = explicitLogPath ?? _currentAudioLogPath;
            if (string.IsNullOrWhiteSpace(logPath)) return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch { }
        }

        private static bool ShouldSkipDebugAudioDiagnostic(string message, string? explicitLogPath)
        {
            if (explicitLogPath == null && message.StartsWith("Finalize ", StringComparison.Ordinal))
                return true;
            if (explicitLogPath == null
                && (message.StartsWith("MP4 ", StringComparison.Ordinal) || message.StartsWith("WAV ", StringComparison.Ordinal)))
                return true;
            return false;
        }

        private static bool IsImportantAudioDiagnostic(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return false;

            string[] importantKeywords =
            {
                "失败",
                "异常",
                "超时",
                "队列已满",
                "不可用",
                "不稳定",
                "断流",
                "未找到",
                "放弃",
                "保留 MKV",
                "保留原始文件",
                "跳过 MP4"
            };

            return importantKeywords.Any(keyword => message.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        private void ClearCurrentAudioLogPath(string? audioLogPath)
        {
            if (string.Equals(_currentAudioLogPath, audioLogPath, StringComparison.OrdinalIgnoreCase))
                _currentAudioLogPath = null;
        }

        private static string FindFFmpeg()
        {
            return AppPaths.FindFFmpeg();
        }

        private string ResolveEncoder()
        {
            string codec = Config.VideoCodec?.Trim().ToLowerInvariant() ?? "h264";
            if (codec != "h264" && codec != "h265" && codec != "av1") codec = "h264";
            return EncodingHelper.TryResolveEncoder(
                Config.GpuEncoder?.Trim().ToLowerInvariant() ?? "auto",
                codec,
                ValidatedEncoders ?? new HashSet<string>(),
                EncoderPerformanceScores,
                out string encoder)
                ? encoder
                : "";
        }
    }
}
