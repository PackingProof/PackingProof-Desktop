using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using OpenCvSharp;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ExpressPackingMonitoring.Services;

internal sealed class NetworkCameraStreamInfoEventArgs : EventArgs
{
    public NetworkCameraStreamInfoEventArgs(int width, int height, int fps)
    {
        Width = width;
        Height = height;
        Fps = fps;
    }

    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
}

internal sealed class NetworkCameraFrameEventArgs : EventArgs
{
    public NetworkCameraFrameEventArgs(Mat frame)
    {
        Frame = frame;
    }

    /// <summary>帧由事件接收方负责释放。</summary>
    public Mat Frame { get; }
}

internal sealed class NetworkCameraErrorEventArgs : EventArgs
{
    public NetworkCameraErrorEventArgs(string description)
    {
        Description = description;
    }

    public string Description { get; }
}

/// <summary>
/// 使用随包发布的 ffmpeg.exe 解码 RTSP/RTMP/HTTP 等网络流，输出原始 BGR24 帧。
/// 不做缩放、不做强制帧率，分辨率与帧率取自流本身。
/// </summary>
internal sealed class NetworkCameraSource : IDisposable
{
    private const int StreamInfoTimeoutMs = 15_000;
    private const int DefaultFallbackFps = 15;
    private const int MaxStderrLines = 40;
    private const int MaxStderrDetailLength = 240;
    private static readonly ConcurrentDictionary<string, bool> FpsModeSupportCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> FfmpegVersionCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _url;
    private readonly string _transport;
    private readonly int _fallbackFps;
    private readonly string? _ffmpegPathOverride;
    private readonly object _lifecycleLock = new();
    private readonly object _stderrLock = new();
    private readonly List<string> _stderrLines = new(MaxStderrLines);
    private Process? _process;
    private Task? _frameReadTask;
    private TaskCompletionSource<bool>? _streamInfoReady;
    private volatile bool _stopping;
    // 本次会话真正收到的帧数。"拿到 SDP"和"收到画面"是两件事：摄像头接受了
    // RTSP 会话却一帧不送时，只有帧计数能把它和正常连接区分开。
    private long _framesReceived;
    private long _streamInfoReceivedAtUtcTicks;

    public NetworkCameraSource(
        string url,
        string transport = "tcp",
        int fallbackFps = DefaultFallbackFps,
        string? ffmpegPathOverride = null)
    {
        _url = url;
        _transport = string.Equals(transport, "udp", StringComparison.OrdinalIgnoreCase) ? "udp" : "tcp";
        CurrentTransport = _transport;
        _fallbackFps = fallbackFps > 0 ? fallbackFps : DefaultFallbackFps;
        _ffmpegPathOverride = ffmpegPathOverride;
    }

    public event EventHandler<NetworkCameraFrameEventArgs>? FrameReady;
    public event EventHandler<NetworkCameraErrorEventArgs>? SourceError;
    public event EventHandler<NetworkCameraStreamInfoEventArgs>? StreamInfoReady;

    public string Url => _url;
    public string? LastError { get; private set; }

    /// <summary>本条会话实际使用的 RTSP 传输方式：流信息阶段自动换过传输方式时以这里为准。</summary>
    public string CurrentTransport { get; private set; }

    /// <summary>本次会话已解码出的帧数。0 表示"连上了但一帧画面都没有"。</summary>
    public int FramesReceived => (int)Math.Min(int.MaxValue, Interlocked.Read(ref _framesReceived));

    /// <summary>本次会话解析出流信息（SDP）的时刻；还没拿到流信息时为 null。</summary>
    public DateTime? StreamInfoReceivedAtUtc
    {
        get
        {
            long ticks = Interlocked.Read(ref _streamInfoReceivedAtUtcTicks);
            return ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;
        }
    }

    /// <summary>最近一次会话的 ffmpeg stderr 尾部（已脱敏），供"连上但零帧"时定位原因。</summary>
    public string LastStderrTail => BuildStderrTail(6);

    public bool IsRunning
    {
        get
        {
            Process? process = _process;
            if (process == null)
                return false;
            try
            {
                return !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public int ActualWidth { get; private set; }
    public int ActualHeight { get; private set; }
    public int ActualFps { get; private set; }
    public string SourceVideoCodec { get; private set; } = "";
    public IReadOnlyList<NativeCameraMode> NativeModes { get; private set; } = [];

    /// <summary>启动解码进程并立即返回，流信息就绪后通过 StreamInfoReady 通知。</summary>
    public bool Start()
    {
        lock (_lifecycleLock)
        {
            StopInternalLocked();
            _stopping = false;
            LastError = null;
            _stderrLines.Clear();
            Interlocked.Exchange(ref _framesReceived, 0);
            Interlocked.Exchange(ref _streamInfoReceivedAtUtcTicks, 0);
            SourceVideoCodec = "";
            _streamInfoReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _process = LaunchProcess(_transport);
            if (_process == null)
                return false;

            _frameReadTask = Task.Run(FrameReadLoop);
            return true;
        }
    }

    /// <summary>启动并等待流信息解析完成，适合测试连接按钮等需要即时结果的场景。</summary>
    public async Task<bool> StartAsync()
    {
        if (!Start())
            return false;

        Task<bool> readyTask = _streamInfoReady?.Task ?? Task.FromResult(false);
        Task finished = await Task.WhenAny(readyTask, Task.Delay(StreamInfoTimeoutMs));
        if (finished != readyTask)
        {
            Stop();
            LastError = "连接网络摄像头超时，请检查地址和网络";
            return false;
        }

        return await readyTask;
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            StopInternalLocked();
        }
    }

    public void Dispose()
    {
        Stop();
    }

    internal static string BuildArguments(string url, string transport, bool useFpsMode)
    {
        var builder = new StringBuilder();
        // 不传 -fflags nobuffer / -flags low_delay：它们只省几十毫秒启动延迟，
        // 却会让部分 RTSP 相机在抖动后停在"会话正常、媒体不再送来"的状态，
        // 而同一台相机用不带这两个参数的探测（自检、现场 ffmpeg）每次都能出帧。
        // 打包取证对百毫秒级延迟不敏感，稳定性优先。
        builder.Append("-hide_banner -nostdin -loglevel info");

        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            string scheme = uri.Scheme.ToLowerInvariant();
            if (scheme == "rtsp")
            {
                string normalizedTransport = string.Equals(transport, "udp", StringComparison.OrdinalIgnoreCase)
                    ? "udp"
                    : "tcp";
                builder.Append(" -rtsp_transport ").Append(normalizedTransport);
            }
            // FFmpeg 8.x 已移除 -stimeout，4.4.x 的 -timeout 在 RTSP 上又会挂起；
            // 不再传 socket 超时参数，由应用层 15 秒连接超时与断流看门狗兜底。
        }

        string escapedUrl = url.Replace("\"", "\\\"", StringComparison.Ordinal);
        builder.Append(" -i \"").Append(escapedUrl).Append('"');
        builder.Append(" -an ");
        // -fps_mode 需要 FFmpeg 5.1+，老显卡/Win7 机器使用的 FFmpeg 4.4.x
        // 只支持 -vsync passthrough，两者语义相同，按实际版本动态选择。
        builder.Append(useFpsMode ? "-fps_mode passthrough" : "-vsync passthrough");
        builder.Append(" -f rawvideo -pix_fmt bgr24 pipe:1");
        return builder.ToString();
    }

    internal static bool SupportsFpsMode(string ffmpegPath) =>
        FpsModeSupportCache.GetOrAdd(ffmpegPath ?? "", DetectFpsModeSupport);

    private static bool DetectFpsModeSupport(string ffmpegPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
                return false;

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using Process? proc = Process.Start(startInfo);
            if (proc == null)
                return false;

            Task<string> outputTask = proc.StandardOutput.ReadToEndAsync();
            bool exited = proc.WaitForExit(3000);
            if (!exited)
            {
                try { proc.Kill(); } catch { /* 进程可能已退出，忽略 */ }
            }

            string firstLine = outputTask.Wait(TimeSpan.FromSeconds(2))
                ? (outputTask.Result.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "")
                : "";
            return ParseVersionSupportsFpsMode(firstLine);
        }
        catch
        {
            return false;
        }
    }

    internal static bool ParseVersionSupportsFpsMode(string? versionLine)
    {
        if (string.IsNullOrWhiteSpace(versionLine))
            return false;

        Match match = Regex.Match(versionLine, @"ffmpeg version\s+(\d+)\.(\d+)");
        if (!match.Success)
            return false;

        int major = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        int minor = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return major > 5 || (major == 5 && minor >= 1);
    }

    internal static bool TryParseStreamInfo(string line, out int width, out int height, out int fps)
    {
        return TryParseStreamInfo(
            line,
            out width,
            out height,
            out fps,
            out _);
    }

    internal static bool TryParseStreamInfo(
        string line,
        out int width,
        out int height,
        out int fps,
        out string videoCodec)
    {
        width = 0;
        height = 0;
        fps = 0;
        videoCodec = "";
        if (string.IsNullOrWhiteSpace(line) || !line.Contains("Stream #", StringComparison.Ordinal))
            return false;

        Match resolutionMatch = Regex.Match(
            line,
            @"Video:.*?(?<width>\d{2,5})x(?<height>\d{2,5})",
            RegexOptions.IgnoreCase);
        if (!resolutionMatch.Success)
            return false;

        width = int.Parse(resolutionMatch.Groups["width"].Value, CultureInfo.InvariantCulture);
        height = int.Parse(resolutionMatch.Groups["height"].Value, CultureInfo.InvariantCulture);
        Match fpsMatch = Regex.Match(line, @"(?<fps>\d+(?:\.\d+)?) fps");
        if (fpsMatch.Success)
            fps = (int)Math.Round(
                double.Parse(fpsMatch.Groups["fps"].Value, CultureInfo.InvariantCulture),
                MidpointRounding.AwayFromZero);
        Match codecMatch = Regex.Match(
            line,
            @"Video:\s*(?<codec>h264|avc1|avc|hevc|h265)",
            RegexOptions.IgnoreCase);
        if (codecMatch.Success)
        {
            videoCodec = codecMatch.Groups["codec"].Value.ToLowerInvariant() switch
            {
                "hevc" or "h265" => "h265",
                _ => "h264"
            };
        }
        return true;
    }

    private async Task FrameReadLoop()
    {
        try
        {
            string transport = _transport;
            Process? process = _process;
            if (process == null)
                return;

            bool streamReady = await WaitForStreamInfoAsync(process);
            if (!streamReady && !_stopping && IsRtspUrl)
            {
                // 部分无线摄像头只支持 UDP 回传，TCP 拿不到流时自动换一次传输方式。
                string fallbackTransport = transport == "tcp" ? "udp" : "tcp";
                RuntimeLog.Warn(
                    "NetworkCamera",
                    $"RTSP stream info unavailable with {transport}, retrying with {fallbackTransport}");
                KillProcess(process);
                process = LaunchProcess(fallbackTransport);
                if (process != null)
                {
                    CurrentTransport = fallbackTransport;
                    _process = process;
                    streamReady = await WaitForStreamInfoAsync(process);
                }
            }

            if (!streamReady)
            {
                if (!_stopping)
                    FailStreamInfo();
                return;
            }

            if (process == null)
            {
                if (!_stopping)
                    FailStreamInfo();
                return;
            }

            _ = Task.Run(() => DrainStderrAsync(process));

            int frameSize = ActualWidth * ActualHeight * 3;
            byte[] buffer = new byte[frameSize];
            using Stream stdout = process.StandardOutput.BaseStream;
            while (!_stopping && !process.HasExited)
            {
                if (!await ReadExactlyAsync(stdout, buffer, frameSize))
                    break;

                Interlocked.Increment(ref _framesReceived);
                Mat? frame = BufferToMat(buffer);
                if (frame != null)
                    FrameReady?.Invoke(this, new NetworkCameraFrameEventArgs(frame));
            }

            if (!_stopping)
                RaiseError("网络摄像头流已断开");
        }
        catch (Exception ex)
        {
            if (!_stopping)
            {
                RuntimeLog.Error("NetworkCamera", $"Frame read failed: {ex.Message}");
                RaiseError("读取网络摄像头画面失败");
            }
        }
    }

    private async Task<bool> WaitForStreamInfoAsync(Process process)
    {
        try
        {
            while (!_stopping && !process.HasExited)
            {
                string? line = await process.StandardError.ReadLineAsync();
                if (line == null)
                    break;

                RecordStderrLine(line);
                if (TryParseStreamInfo(
                        line,
                        out int width,
                        out int height,
                        out int fps,
                        out string videoCodec))
                {
                    ActualWidth = width;
                    ActualHeight = height;
                    ActualFps = fps > 0 ? fps : _fallbackFps;
                    SourceVideoCodec = videoCodec;
                    NativeModes = [new NativeCameraMode(width, height, ActualFps)];
                    Interlocked.Exchange(ref _streamInfoReceivedAtUtcTicks, DateTime.UtcNow.Ticks);
                    _streamInfoReady?.TrySetResult(true);
                    StreamInfoReady?.Invoke(this, new NetworkCameraStreamInfoEventArgs(width, height, ActualFps));
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("NetworkCamera", $"Read ffmpeg stderr failed: {ex.Message}");
        }

        // 进程已退出或流结束：把剩余的 stderr 读完，用于定位真实原因。
        try
        {
            while (!_stopping && await process.StandardError.ReadLineAsync() is { } tailLine)
                RecordStderrLine(tailLine);
        }
        catch { }

        return false;
    }

    private async Task DrainStderrAsync(Process process)
    {
        try
        {
            while (!_stopping)
            {
                string? line = await process.StandardError.ReadLineAsync();
                if (line == null)
                    break;
                RecordStderrLine(line);
            }
        }
        catch { }
    }

    private Process? LaunchProcess(string transport)
    {
        string ffmpegPath = _ffmpegPathOverride ?? AppPaths.FindFFmpeg();
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            LastError = "未找到 ffmpeg.exe，无法连接网络摄像头";
            RuntimeLog.Warn("NetworkCamera", LastError);
            return null;
        }

        string ffmpegVersion = GetFfmpegVersion(ffmpegPath);
        if (ffmpegVersion.Length > 0)
            RuntimeLog.Info("NetworkCamera", $"ffmpeg version: {ffmpegVersion}");

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = BuildArguments(_url, transport, SupportsFpsMode(ffmpegPath)),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            Process? process = Process.Start(startInfo);
            if (process == null)
                LastError = "无法启动 ffmpeg 解码进程";
            return process;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("NetworkCamera", $"Failed to start ffmpeg: {ex.Message}");
            LastError = "无法启动 ffmpeg 解码进程";
            return null;
        }
    }

    private static string GetFfmpegVersion(string ffmpegPath)
    {
        return FfmpegVersionCache.GetOrAdd(ffmpegPath ?? "", path =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return "";

                var startInfo = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "-hide_banner -version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using Process? process = Process.Start(startInfo);
                if (process == null)
                    return "";

                string output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(2000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                }
                string firstLine = output.Split('\n', 2)[0].Trim();
                return firstLine.Length > 0 ? firstLine : "";
            }
            catch
            {
                return "";
            }
        });
    }

    private bool IsRtspUrl =>
        Uri.TryCreate(_url, UriKind.Absolute, out Uri? uri)
        && string.Equals(uri.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase);

    private void FailStreamInfo()
    {
        string detail = BuildStderrError();
        string tail = BuildStderrTail(5);
        if (tail.Length > 0)
            RuntimeLog.Warn("NetworkCamera", $"ffmpeg stderr tail: {tail}");

        LastError = detail.Length > 0
            ? $"无法获取网络摄像头画面信息：{detail}"
            : "无法获取网络摄像头画面信息，请检查地址和协议";
        _streamInfoReady?.TrySetResult(false);
        RaiseError(LastError);
    }

    /// <summary>把最近几行 stderr 拼成一条（已脱敏）。零帧/断流时这是唯一能说明原因的现场证据。</summary>
    private string BuildStderrTail(int lineCount)
    {
        if (lineCount <= 0)
            return "";

        lock (_stderrLock)
        {
            if (_stderrLines.Count == 0)
                return "";
            return string.Join(" | ", _stderrLines.TakeLast(lineCount));
        }
    }

    private string BuildStderrError()
    {
        string[] lines;
        lock (_stderrLock)
            lines = _stderrLines.ToArray();

        string? candidate = null;
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (line.Length == 0)
                continue;
            candidate = line;
            if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || line.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                || line.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                || line.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
                || line.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
                || line.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || line.Contains("refused", StringComparison.OrdinalIgnoreCase)
                || line.Contains("permission", StringComparison.OrdinalIgnoreCase)
                || line.Contains("401", StringComparison.Ordinal)
                || line.Contains("403", StringComparison.Ordinal)
                || line.Contains("404", StringComparison.Ordinal))
                break;
        }

        if (string.IsNullOrWhiteSpace(candidate))
            return "";
        return candidate.Length <= MaxStderrDetailLength
            ? candidate
            : candidate[..MaxStderrDetailLength];
    }

    internal static string SanitizeStderrText(string line, string url)
    {
        if (string.IsNullOrEmpty(line))
            return line ?? "";
        string sanitizedUrl = NetworkCameraUrlPolicy.SanitizeForLog(url);
        return line.Replace(url, sanitizedUrl, StringComparison.Ordinal);
    }

    private void RecordStderrLine(string line)
    {
        string sanitized = SanitizeStderrText(line, _url);
        lock (_stderrLock)
        {
            _stderrLines.Add(sanitized);
            if (_stderrLines.Count > MaxStderrLines)
                _stderrLines.RemoveAt(0);
        }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (Exception ex)
        {
            RuntimeLog.Warn("NetworkCamera", $"Kill ffmpeg failed: {ex.Message}");
        }
        try
        {
            process.WaitForExit(3000);
        }
        catch { }
        process.Dispose();
    }

    private Mat? BufferToMat(byte[] buffer)
    {
        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            IntPtr pointer = handle.AddrOfPinnedObject();
            using Mat header = Mat.FromPixelData(
                ActualHeight,
                ActualWidth,
                MatType.CV_8UC3,
                pointer,
                ActualWidth * 3);
            return header.Clone();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("NetworkCamera", $"Frame conversion failed: {ex.Message}");
            return null;
        }
        finally
        {
            handle.Free();
        }
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset));
            if (read <= 0)
                return false;
            offset += read;
        }
        return true;
    }

    private void RaiseError(string description)
    {
        // 断流时把 ffmpeg 最后几行带上：现场反馈里只有"流已断开"这一句时，
        // 根本分不清是摄像头停止送流、TCP 被重置还是解码失败。
        string tail = BuildStderrTail(4);
        string message = tail.Length > 0
            ? $"SourceError: {description}；ffmpeg={tail}"
            : $"SourceError: {description}";
        RuntimeLog.Warn("NetworkCamera", message);
        SourceError?.Invoke(this, new NetworkCameraErrorEventArgs(description));
    }

    private void StopInternalLocked()
    {
        _stopping = true;
        Process? process = _process;
        _process = null;
        if (process != null)
            KillProcess(process);
        _streamInfoReady?.TrySetResult(false);
        ActualWidth = 0;
        ActualHeight = 0;
        ActualFps = 0;
        NativeModes = [];
    }
}
