using ExpressPackingMonitoring.Logging;
using System.IO;
using System.Diagnostics;
using System.Text;

namespace ExpressPackingMonitoring.Services;

internal sealed class NetworkCameraStreamCopyRecorder : IDisposable
{
    internal const string EncoderId = "stream_copy";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(8);
    private const long MinimumReadyFileBytes = 4096;

    private readonly Process _process;
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _url;
    private readonly object _stderrLock = new();
    private readonly StringBuilder _stderr = new();
    private bool _disposed;

    private NetworkCameraStreamCopyRecorder(
        Process process,
        string videoCodec,
        string url)
    {
        _process = process;
        VideoCodec = videoCodec;
        _url = url;
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => _completion.TrySetResult();
        _ = DrainStderrAsync(process);
    }

    internal string VideoCodec { get; }
    internal Task Completion => _completion.Task;
    internal string LastError
    {
        get
        {
            lock (_stderrLock)
                return _stderr.ToString().Trim();
        }
    }

    internal static bool SupportsCodec(string? videoCodec) =>
        NormalizeCodec(videoCodec) is "h264" or "h265";

    internal static string NormalizeCodec(string? videoCodec) =>
        videoCodec?.Trim().ToLowerInvariant() switch
        {
            "h264" or "avc" => "h264",
            "h265" or "hevc" => "h265",
            _ => ""
        };

    internal static async Task<NetworkCameraStreamCopyRecorder?> StartAsync(
        string ffmpegPath,
        string url,
        string transport,
        string videoCodec,
        string filePath,
        CancellationToken cancellationToken)
    {
        string normalizedCodec = NormalizeCodec(videoCodec);
        if (!SupportsCodec(normalizedCodec)
            || string.IsNullOrWhiteSpace(ffmpegPath)
            || !File.Exists(ffmpegPath)
            || string.IsNullOrWhiteSpace(url)
            || string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = BuildArguments(url, transport, filePath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true
            };
            process = Process.Start(startInfo);
            if (process == null)
            {
                return null;
            }

            var recorder = new NetworkCameraStreamCopyRecorder(
                process,
                normalizedCodec,
                url);
            DateTime deadline = DateTime.UtcNow + StartTimeout;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited || recorder.Completion.IsCompleted)
                {
                    RuntimeLog.Warn(
                        "Recording",
                        $"Network stream copy exited before startup: {recorder.LastError}");
                    await recorder.StopAsync();
                    DeletePartialFile(filePath);
                    return null;
                }
                try
                {
                    if (File.Exists(filePath)
                        && new FileInfo(filePath).Length
                            >= MinimumReadyFileBytes)
                    {
                        RuntimeLog.Info(
                            "Recording",
                            $"Network stream copy started codec={normalizedCodec}, file={Path.GetFileName(filePath)}");
                        return recorder;
                    }
                }
                catch (IOException)
                {
                }
                await Task.Delay(50, cancellationToken);
            }

            RuntimeLog.Warn(
                "Recording",
                $"Network stream copy start timed out codec={normalizedCodec}, "
                    + $"detail={recorder.LastError}");
            await recorder.StopAsync();
            DeletePartialFile(filePath);
            return null;
        }
        catch (OperationCanceledException)
        {
            if (process != null)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
            }
            DeletePartialFile(filePath);
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(
                "Recording",
                $"Network stream copy start failed: {ex.Message}");
            if (process != null)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
            }
            DeletePartialFile(filePath);
            return null;
        }
    }

    internal async Task<bool> StopAsync()
    {
        if (_disposed)
            return Completion.IsCompleted;

        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    await _process.StandardInput.WriteLineAsync("q");
                    await _process.StandardInput.FlushAsync();
                    _process.StandardInput.Close();
                }
                catch
                {
                }

                Task completed = await Task.WhenAny(
                    Completion,
                    Task.Delay(StopTimeout));
                if (completed != Completion && !_process.HasExited)
                {
                    RuntimeLog.Warn(
                        "Recording",
                        "Network stream copy did not stop within timeout; killing FFmpeg");
                    try { _process.Kill(entireProcessTree: true); } catch { }
                    try
                    {
                        await _process.WaitForExitAsync()
                            .WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch
                    {
                    }
                }
            }
            await Completion.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        bool cleanExit = false;
        try
        {
            cleanExit = _process.HasExited && _process.ExitCode == 0;
        }
        catch
        {
        }
        Dispose();
        return cleanExit;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _process.Dispose(); } catch { }
    }

    internal static string BuildArguments(
        string url,
        string transport,
        string filePath)
    {
        var builder = new StringBuilder();
        builder.Append("-y -hide_banner -loglevel info");
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && string.Equals(uri.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase))
        {
            string normalizedTransport =
                string.Equals(transport, "udp", StringComparison.OrdinalIgnoreCase)
                    ? "udp"
                    : "tcp";
            builder.Append(" -rtsp_transport ").Append(normalizedTransport);
        }

        string escapedUrl = url.Replace("\"", "\\\"", StringComparison.Ordinal);
        string escapedFile = filePath.Replace("\"", "\\\"", StringComparison.Ordinal);
        builder.Append(" -i \"").Append(escapedUrl).Append('"');
        builder.Append(" -map 0:v:0 -an -sn -dn");
        builder.Append(" -c:v copy");
        builder.Append(" -f matroska \"");
        builder.Append(escapedFile).Append('"');
        return builder.ToString();
    }

    private async Task DrainStderrAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                string sanitized = line.Replace(
                    _url,
                    NetworkCameraUrlPolicy.SanitizeForLog(_url),
                    StringComparison.Ordinal);
                lock (_stderrLock)
                {
                    if (_stderr.Length > 0)
                        _stderr.Append(' ');
                    _stderr.Append(sanitized);
                    if (_stderr.Length > 2000)
                        _stderr.Remove(0, _stderr.Length - 2000);
                }
            }
        }
        catch
        {
        }
    }

    private static void DeletePartialFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch
        {
        }
    }
}
