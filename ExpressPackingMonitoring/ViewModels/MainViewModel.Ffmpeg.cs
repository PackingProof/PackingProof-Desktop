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
        private (bool ok, string error) RunFFmpegPipeline(string filePath, string ffmpegPath, CancellationToken token,
            int w, int h, int fps, string encoder, bool withAudio, string? audioPipeName = null)
        {
            Process? ffmpeg = null;
            Stream? stdin = null;
            bool anyFrameWritten = false;
            bool unexpectedFrameSizeLogged = false;
            string stderrText = "";
            bool stdinClosed = false;

            try
            {
                string args = BuildFFmpegArgs(
                    w,
                    h,
                    fps,
                    filePath,
                    encoder,
                    withAudio,
                    GetVideoCqp(),
                    audioPipeName);
                Debug.WriteLine($"[FFmpeg] encoder={encoder} audio={withAudio} args={args}");
                RuntimeLog.Info("FFmpeg", $"Start encoder={encoder}, audio={withAudio}, size={w}x{h}, fps={fps}, file={Path.GetFileName(filePath)}");

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true
                };

                ffmpeg = Process.Start(psi);
                if (ffmpeg == null) RuntimeLog.Error("FFmpeg", "Process.Start returned null");
                if (ffmpeg == null) return (false, "FFmpeg 进程启动失败");

                // 将进程保存为全局变量，允许从外部强制 Kill
                _currentFfmpegProcess = ffmpeg;

                var stderrTask = Task.Run(() => { try { return ffmpeg.StandardError.ReadToEnd(); } catch { return ""; } });

                for (int wait = 0; wait < 30 && !ffmpeg.HasExited; wait += 30)
                    Thread.Sleep(30);
                if (ffmpeg.HasExited)
                {
                    stderrText = stderrTask.GetAwaiter().GetResult();
                    Debug.WriteLine($"[FFmpeg] early exit ({encoder}): {stderrText}");
                    string shortErr = ExtractFFmpegError(stderrText);
                    RuntimeLog.Error("FFmpeg", $"Early exit encoder={encoder}, error={shortErr}, stderr={TrimForRuntimeLog(stderrText)}");
                    return (false, shortErr);
                }

                stdin = ffmpeg.StandardInput.BaseStream;

                int expectedBytes = w * h * 3;
                byte[] buffer = new byte[expectedBytes];

                foreach (var frame in _videoWriteQueue.GetConsumingEnumerable())
                {
                    // 检查 FFmpeg 进程是否已经崩溃。如果已经退出，直接退出循环
                    if (ffmpeg.HasExited)
                    {
                        frame?.Dispose();
                        break;
                    }

                    if (token.IsCancellationRequested)
                    {
                        frame?.Dispose();
                        break;
                    }
                    if (frame == null || frame.IsDisposed) continue;

                    bool pipeError = false;
                    try
                    {
                        if (frame.Width != w || frame.Height != h)
                        {
                            if (!unexpectedFrameSizeLogged)
                            {
                                unexpectedFrameSizeLogged = true;
                                RuntimeLog.Warn(
                                    "FFmpeg",
                                    $"Native camera frame size changed unexpectedly, expected={w}x{h}, actual={frame.Width}x{frame.Height}; frame skipped without software resampling");
                            }
                            continue;
                        }

                        if (ffmpeg.HasExited) { pipeError = true; break; }

                        if (frame.IsContinuous() && frame.Type() == MatType.CV_8UC3)
                        {
                            Marshal.Copy(frame.Data, buffer, 0, expectedBytes);
                            // 此处可能会抛出 IOException/InvalidOperationException，标志着管道断开
                            stdin.Write(buffer, 0, expectedBytes);
                            anyFrameWritten = true;
                            var firstFrameSignal = _firstRecordingFrameWritten;
                            if (firstFrameSignal != null && !firstFrameSignal.Task.IsCompleted)
                            {
                                long elapsedMs = (long)(1000.0 * (Stopwatch.GetTimestamp() - _recordingStartTimestamp) / Stopwatch.Frequency);
                                firstFrameSignal.TrySetResult(elapsedMs);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[FFmpeg] 管道写入异常: {ex.Message}");
                        RuntimeLog.Error("FFmpeg", $"Pipe write exception encoder={encoder}", ex);
                        pipeError = true;
                    }
                    finally
                    {
                        frame.Dispose();
                    }

                    if (pipeError) break;
                }

                try { stdin?.Close(); stdinClosed = true; } catch { }

                if (ffmpeg != null && !ffmpeg.HasExited)
                {
                    if (!ffmpeg.WaitForExit(15000))
                    {
                        try { ffmpeg.Kill(); } catch { }
                    }
                }

                stderrText = stderrTask.GetAwaiter().GetResult();
                bool fileOk = false;
                try { fileOk = File.Exists(filePath) && new FileInfo(filePath).Length > 0; } catch { }
                bool processOk = ffmpeg != null && ffmpeg.HasExited && ffmpeg.ExitCode == 0;

                if (token.IsCancellationRequested)
                    return (fileOk, fileOk ? "" : ExtractFFmpegError(stderrText));

                if (anyFrameWritten && processOk && fileOk)
                {
                    if (!string.IsNullOrWhiteSpace(stderrText))
                        Debug.WriteLine($"[FFmpeg] stderr (success): {stderrText[..Math.Min(stderrText.Length, 500)]}");
                    RuntimeLog.Info("FFmpeg", $"Exit ok encoder={encoder}, fileSize={new FileInfo(filePath).Length}, anyFrameWritten={anyFrameWritten}");
                    return (true, "");
                }

                string finalErr = ExtractFFmpegError(stderrText);
                if (string.IsNullOrWhiteSpace(finalErr))
                    finalErr = !fileOk ? "FFmpeg 未生成有效视频文件" : $"FFmpeg 退出码: {ffmpeg?.ExitCode}";
                RuntimeLog.Error("FFmpeg", $"Exit failed encoder={encoder}, processOk={processOk}, fileOk={fileOk}, anyFrameWritten={anyFrameWritten}, error={finalErr}, stderr={TrimForRuntimeLog(stderrText)}");
                return (false, finalErr);
            }
            catch (OperationCanceledException)
            {
                RuntimeLog.Info("FFmpeg", $"Canceled encoder={encoder}, anyFrameWritten={anyFrameWritten}");
                return (anyFrameWritten, "");
            }
            catch (IOException ex)
            {
                RuntimeLog.Error("FFmpeg", $"IOException encoder={encoder}, canceled={token.IsCancellationRequested}, anyFrameWritten={anyFrameWritten}", ex);
                return token.IsCancellationRequested && anyFrameWritten
                    ? (true, "")
                    : (false, ex.Message);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("FFmpeg", $"Exception encoder={encoder}", ex);
                return (false, ex.Message);
            }
            finally
            {
                if (!stdinClosed) { try { stdin?.Close(); } catch { } }

                try
                {
                    if (ffmpeg != null && !ffmpeg.HasExited)
                    {
                        if (!ffmpeg.WaitForExit(8000))
                        {
                            try { ffmpeg.Kill(); } catch { }
                        }
                    }
                }
                catch { }
                finally
                {
                    try { ffmpeg?.Dispose(); } catch { }
                }
            }
        }

        private static string ExtractFFmpegError(string stderr)
        {
            if (string.IsNullOrEmpty(stderr)) return "";
            var lines = stderr.Split('\n');
            for (int i = lines.Length - 1; i >= Math.Max(0, lines.Length - 10); i--)
            {
                string line = lines[i].Trim();
                if (line.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Could not", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("No such", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    return line.Length > 80 ? line[..80] : line;
            }
            return "";
        }

        private static string TrimForRuntimeLog(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= 500 ? text : text[..500];
        }

        internal static string BuildFFmpegArgs(
            int w,
            int h,
            int fps,
            string filePath,
            string encoder,
            bool withAudio,
            int videoCqp,
            string? audioPipeName = null)
        {
            // rawvideo 的 -framerate 负责按固定帧率生成时间戳。不能使用 wallclock 时间戳，
            // 否则预录帧会在短时间内批量写入而被 FFmpeg 当成快进视频。
            string args = $"-y -fflags +genpts -f rawvideo -video_size {w}x{h} -pixel_format bgr24 -framerate {fps} -i pipe:0";
            if (withAudio)
            {
                if (string.IsNullOrWhiteSpace(audioPipeName))
                    throw new InvalidOperationException("启用实时 AAC 时必须提供音频管道名称");
                args += $" -thread_queue_size 512 -f s16le -ar 48000 -ac 1 -i \\\\.\\pipe\\{audioPipeName}";
            }
            args += $" {BuildFFmpegEncoderArgs(w, h, fps, encoder, videoCqp)}";
            if (withAudio)
                args += " -map 0:v:0 -map 1:a:0 -c:a aac -profile:a aac_low -b:a 128k -af aresample=async=1:first_pts=0";
            args += " -muxdelay 0 -muxpreload 0";
            args += $" \"{filePath}\"";
            return args;
        }

        internal static string BuildFFmpegEncoderArgs(int w, int h, int fps, string encoder, int videoCqp)
        {
            string args = "";
            int cqp = videoCqp > 0 ? videoCqp : 25;
            int gop = Math.Max(1, fps * 2);

            if (encoder == "h264_nvenc") args += $" -c:v h264_nvenc -pix_fmt yuv420p -preset p4 -rc vbr -cq {cqp} -b:v 0 -g {gop} -max_muxing_queue_size 1024";
            else if (encoder == "h264_amf") args += $" -c:v h264_amf -pix_fmt yuv420p -quality balanced -rc cqp -qp_i {cqp} -qp_p {cqp} -g {gop}";
            else if (encoder == "h264_qsv") args += $" -c:v h264_qsv -pix_fmt nv12 -preset medium -global_quality {cqp} -g {gop}";
            else if (encoder == "libx264") args += $" -c:v libx264 -pix_fmt yuv420p -preset fast -crf {cqp} -g {gop}";
            else if (encoder == "hevc_nvenc") args += $" -c:v hevc_nvenc -pix_fmt yuv420p -preset p4 -rc vbr -cq {cqp} -b:v 0 -g {gop} -max_muxing_queue_size 1024";
            else if (encoder == "hevc_amf") args += $" -c:v hevc_amf -pix_fmt yuv420p -quality balanced -rc cqp -qp_i {cqp} -qp_p {cqp} -g {gop}";
            else if (encoder == "hevc_qsv") args += $" -c:v hevc_qsv -pix_fmt nv12 -preset medium -global_quality {cqp} -g {gop}";
            else if (encoder == "libx265") args += $" -c:v libx265 -pix_fmt yuv420p -preset fast -crf {cqp} -g {gop}";
            else if (encoder == "av1_nvenc") args += $" -c:v av1_nvenc -pix_fmt yuv420p -preset p4 -rc vbr -cq {cqp} -b:v 0 -g {gop} -max_muxing_queue_size 1024";
            else if (encoder == "av1_amf") args += $" -c:v av1_amf -pix_fmt yuv420p -quality balanced -rc cqp -qp_i {cqp} -qp_p {cqp} -g {gop}";
            else if (encoder == "av1_qsv") args += $" -c:v av1_qsv -pix_fmt nv12 -preset medium -global_quality {cqp} -g {gop}";
            else if (encoder == "libsvtav1") args += $" -c:v libsvtav1 -pix_fmt yuv420p -preset {GetCpuAv1Preset(w, h, fps)} -crf {cqp} -svtav1-params tune=0 -g {gop}";
            else args += $" -c:v {encoder} -pix_fmt yuv420p -g {gop}";

            return args.TrimStart();
        }

        private static int GetCpuAv1Preset(int w, int h, int fps)
        {
            long pixels = (long)w * h;
            if (pixels >= 1920L * 1080 && fps >= 30) return 10;
            if (pixels >= 1920L * 1080 || fps >= 25) return 9;
            return 8;
        }

    }
}
