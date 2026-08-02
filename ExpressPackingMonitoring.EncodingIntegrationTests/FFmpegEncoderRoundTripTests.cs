using System.Diagnostics;
using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.EncodingIntegrationTests;

public sealed class FFmpegEncoderRoundTripTests
{
    private const int Width = 640;
    private const int Height = 360;
    private const int Fps = 30;
    private const int FrameCount = 30;

    [Fact]
    public async Task RequiredEncoders_CanEncodeAndDecodeProductionFrames()
    {
        string ffmpegPath = Environment.GetEnvironmentVariable("EPM_FFMPEG_PATH")?.Trim() ?? "";
        Assert.True(File.Exists(ffmpegPath),
            "EPM_FFMPEG_PATH must point to the pinned ffmpeg.exe. Run Tools/Test-EncodingCodecs.ps1.");

        string[] requiredEncoders = ParseEncoderList("EPM_REQUIRED_ENCODERS");
        string[] optionalEncoders = ParseEncoderList("EPM_OPTIONAL_ENCODERS");
        Assert.NotEmpty(requiredEncoders);

        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"ExpressPackingMonitoring-encoder-roundtrip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        byte[] frame = CreateBgr24TestFrame();

        try
        {
            foreach (string encoder in requiredEncoders)
            {
                await AssertRoundTripAsync(
                    ffmpegPath,
                    encoder,
                    frame,
                    temporaryDirectory,
                    optional: false);
            }

            foreach (string encoder in optionalEncoders.Except(requiredEncoders, StringComparer.OrdinalIgnoreCase))
            {
                await AssertRoundTripAsync(
                    ffmpegPath,
                    encoder,
                    frame,
                    temporaryDirectory,
                    optional: true);
            }
        }
        finally
        {
            try { Directory.Delete(temporaryDirectory, recursive: true); }
            catch { }
        }
    }

    private static async Task AssertRoundTripAsync(
        string ffmpegPath,
        string encoder,
        byte[] frame,
        string temporaryDirectory,
        bool optional)
    {
        string encodedPath = Path.Combine(temporaryDirectory, $"{encoder}.mkv");
        string decodedPath = Path.Combine(temporaryDirectory, $"{encoder}.bgr24");
        string arguments = MainViewModel.BuildFFmpegArgs(
            Width,
            Height,
            Fps,
            encodedPath,
            encoder,
            withAudio: false,
            videoCqp: 30);

        ProcessResult encode = await RunEncodeAsync(ffmpegPath, arguments, frame);
        if (optional && encode.ExitCode != 0 && IsUnsupportedOptionalEncoder(encode.StandardError))
        {
            Console.WriteLine($"Optional encoder {encoder} is unsupported by the detected hardware: {Shorten(encode.StandardError)}");
            return;
        }

        Assert.True(
            encode.ExitCode == 0,
            $"Encoder {encoder} failed (timeout={encode.TimedOut}, exit={encode.ExitCode}): {Shorten(encode.StandardError)}");
        Assert.True(
            File.Exists(encodedPath) && new FileInfo(encodedPath).Length > 0,
            $"Encoder {encoder} did not produce a video file.");

        ProcessResult decode = await RunProcessAsync(
            ffmpegPath,
            $"-hide_banner -loglevel error -y -i \"{encodedPath}\" -frames:v 1 -f rawvideo -pix_fmt bgr24 \"{decodedPath}\"",
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(
            decode.ExitCode == 0,
            $"Decoder round trip for {encoder} failed (timeout={decode.TimedOut}, exit={decode.ExitCode}): {Shorten(decode.StandardError)}");
        Assert.True(File.Exists(decodedPath), $"Decoder round trip for {encoder} produced no frame.");
        Assert.Equal((long)Width * Height * 3, new FileInfo(decodedPath).Length);
    }

    private static async Task<ProcessResult> RunEncodeAsync(
        string ffmpegPath,
        string arguments,
        byte[] frame)
    {
        using var process = StartProcess(ffmpegPath, arguments, redirectInput: true);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        Exception? writeException = null;
        try
        {
            for (int index = 0; index < FrameCount; index++)
                await process.StandardInput.BaseStream.WriteAsync(frame);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            writeException = ex;
        }
        finally
        {
            try { process.StandardInput.Close(); }
            catch { }
        }

        ProcessResult result = await WaitForExitAsync(process, stderrTask, TimeSpan.FromSeconds(60));
        return writeException == null
            ? result
            : result with { StandardError = $"{result.StandardError}\nstdin: {writeException.Message}" };
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        string arguments,
        TimeSpan timeout)
    {
        using var process = StartProcess(fileName, arguments, redirectInput: false);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        return await WaitForExitAsync(process, stderrTask, timeout);
    }

    private static Process StartProcess(string fileName, string arguments, bool redirectInput)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = redirectInput,
                RedirectStandardError = true
            }
        };
        Assert.True(process.Start(), $"Unable to start {fileName}.");
        return process;
    }

    private static async Task<ProcessResult> WaitForExitAsync(
        Process process,
        Task<string> stderrTask,
        TimeSpan timeout)
    {
        bool timedOut = false;
        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); }
            catch { }
            await process.WaitForExitAsync();
        }

        string stderr = await stderrTask;
        return new ProcessResult(timedOut ? -999 : process.ExitCode, stderr, timedOut);
    }

    private static string[] ParseEncoderList(string environmentVariable)
    {
        return (Environment.GetEnvironmentVariable(environmentVariable) ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static byte[] CreateBgr24TestFrame()
    {
        var frame = new byte[Width * Height * 3];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int offset = (y * Width + x) * 3;
                frame[offset] = (byte)(x * 255 / Width);
                frame[offset + 1] = (byte)(y * 255 / Height);
                frame[offset + 2] = (byte)((x + y) % 256);
            }
        }
        return frame;
    }

    private static bool IsUnsupportedOptionalEncoder(string error)
    {
        if (error.Contains("required nvenc API version", StringComparison.OrdinalIgnoreCase)
            || error.Contains("minimum required Nvidia driver", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] unsupportedMarkers =
        [
            "AMF_NOT_SUPPORTED",
            "not supported by the hardware",
            "unsupported (-3)",
            "MFX_ERR_UNSUPPORTED",
            "No capable devices found",
            "CreateComponent() failed with error 10",
            "error code: -1129203192 (Encoder not found)"
        ];
        return unsupportedMarkers.Any(marker => error.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string Shorten(string value)
    {
        string normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 1600
            ? normalized
            : $"{normalized[..500]} ... {normalized[^1000..]}";
    }

    private sealed record ProcessResult(int ExitCode, string StandardError, bool TimedOut);
}
