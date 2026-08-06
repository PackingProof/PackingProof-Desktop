using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using OpenCvSharp;
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
    private const int StreamInfoTimeoutMs = 10_000;
    private const int DefaultFallbackFps = 15;

    private readonly string _url;
    private readonly string _transport;
    private readonly int _fallbackFps;
    private readonly string? _ffmpegPathOverride;
    private readonly object _lifecycleLock = new();
    private Process? _process;
    private Task? _frameReadTask;
    private TaskCompletionSource<bool>? _streamInfoReady;
    private volatile bool _stopping;

    public NetworkCameraSource(
        string url,
        string transport = "tcp",
        int fallbackFps = DefaultFallbackFps,
        string? ffmpegPathOverride = null)
    {
        _url = url;
        _transport = string.Equals(transport, "udp", StringComparison.OrdinalIgnoreCase) ? "udp" : "tcp";
        _fallbackFps = fallbackFps > 0 ? fallbackFps : DefaultFallbackFps;
        _ffmpegPathOverride = ffmpegPathOverride;
    }

    public event EventHandler<NetworkCameraFrameEventArgs>? FrameReady;
    public event EventHandler<NetworkCameraErrorEventArgs>? SourceError;
    public event EventHandler<NetworkCameraStreamInfoEventArgs>? StreamInfoReady;

    public string Url => _url;
    public string? LastError { get; private set; }

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
    public IReadOnlyList<NativeCameraMode> NativeModes { get; private set; } = [];

    /// <summary>启动解码进程并立即返回，流信息就绪后通过 StreamInfoReady 通知。</summary>
    public bool Start()
    {
        lock (_lifecycleLock)
        {
            StopInternalLocked();
            _stopping = false;
            LastError = null;
            _streamInfoReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            string ffmpegPath = _ffmpegPathOverride ?? AppPaths.FindFFmpeg();
            if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                LastError = "未找到 ffmpeg.exe，无法连接网络摄像头";
                RuntimeLog.Warn("NetworkCamera", LastError);
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = BuildArguments(_url, _transport),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            try
            {
                _process = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("NetworkCamera", $"Failed to start ffmpeg: {ex.Message}");
                LastError = "无法启动 ffmpeg 解码进程";
                return false;
            }

            if (_process == null)
            {
                LastError = "无法启动 ffmpeg 解码进程";
                return false;
            }

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

    internal static string BuildArguments(string url, string transport)
    {
        var builder = new StringBuilder();
        builder.Append("-hide_banner -nostdin -loglevel info -fflags nobuffer -flags low_delay");

        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            string scheme = uri.Scheme.ToLowerInvariant();
            if (scheme == "rtsp")
            {
                string normalizedTransport = string.Equals(transport, "udp", StringComparison.OrdinalIgnoreCase)
                    ? "udp"
                    : "tcp";
                builder.Append(" -rtsp_transport ").Append(normalizedTransport);
                builder.Append(" -stimeout 5000000");
            }
            else if (scheme is "http" or "https")
            {
                builder.Append(" -timeout 5000000");
            }
        }

        string escapedUrl = url.Replace("\"", "\\\"", StringComparison.Ordinal);
        builder.Append(" -i \"").Append(escapedUrl).Append('"');
        builder.Append(" -an -fps_mode passthrough -f rawvideo -pix_fmt bgr24 pipe:1");
        return builder.ToString();
    }

    internal static bool TryParseStreamInfo(string line, out int width, out int height, out int fps)
    {
        width = 0;
        height = 0;
        fps = 0;
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
        return true;
    }

    private async Task FrameReadLoop()
    {
        Process? process = _process;
        if (process == null)
            return;

        try
        {
            if (!await WaitForStreamInfoAsync(process))
                return;

            int frameSize = ActualWidth * ActualHeight * 3;
            byte[] buffer = new byte[frameSize];
            using Stream stdout = process.StandardOutput.BaseStream;
            while (!_stopping && !process.HasExited)
            {
                if (!await ReadExactlyAsync(stdout, buffer, frameSize))
                    break;

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
        while (!_stopping && !process.HasExited)
        {
            string? line = await process.StandardError.ReadLineAsync();
            if (line == null)
                break;

            if (TryParseStreamInfo(line, out int width, out int height, out int fps))
            {
                ActualWidth = width;
                ActualHeight = height;
                ActualFps = fps > 0 ? fps : _fallbackFps;
                NativeModes = [new NativeCameraMode(width, height, ActualFps)];
                _streamInfoReady?.TrySetResult(true);
                StreamInfoReady?.Invoke(this, new NetworkCameraStreamInfoEventArgs(width, height, ActualFps));
                return true;
            }
        }

        if (!_stopping)
            RaiseError("无法获取网络摄像头画面信息，请检查地址和协议");
        _streamInfoReady?.TrySetResult(false);
        return false;
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
        RuntimeLog.Warn("NetworkCamera", $"SourceError: {description}");
        SourceError?.Invoke(this, new NetworkCameraErrorEventArgs(description));
    }

    private void StopInternalLocked()
    {
        _stopping = true;
        Process? process = _process;
        _process = null;
        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (Exception ex)
            {
                RuntimeLog.Warn("NetworkCamera", $"Stop ffmpeg failed: {ex.Message}");
            }
            try
            {
                process.WaitForExit(3000);
            }
            catch { }
            process.Dispose();
        }
        _streamInfoReady?.TrySetResult(false);
        ActualWidth = 0;
        ActualHeight = 0;
        ActualFps = 0;
        NativeModes = [];
    }
}
