using System.Diagnostics;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.EncodingIntegrationTests;

public sealed class VideoCompressionServiceTests
{
    [Fact]
    public void EncoderCatalog_RecognizesPinnedFfmpegCapabilities()
    {
        string ffmpegPath = Environment.GetEnvironmentVariable("EPM_FFMPEG_PATH")?.Trim() ?? "";
        Assert.True(
            File.Exists(ffmpegPath),
            "EPM_FFMPEG_PATH must point to the pinned ffmpeg.exe. Run tools/Test-EncodingCodecs.ps1.");

        Assert.True(TargetSizeTranscodeEngine.IsEncoderAvailable(
            ffmpegPath,
            "libx264",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompressesToRequestedSizeAndKeepsTheSource()
    {
        string ffmpegPath = Environment.GetEnvironmentVariable("EPM_FFMPEG_PATH")?.Trim() ?? "";
        Assert.True(
            File.Exists(ffmpegPath),
            "EPM_FFMPEG_PATH must point to the pinned ffmpeg.exe. Run tools/Test-EncodingCodecs.ps1.");

        string directory = Path.Combine(
            Path.GetTempPath(),
            $"epm-video-compression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePath = Path.Combine(directory, "source.mp4");
            Assert.Equal(
                0,
                await RunAsync(
                    ffmpegPath,
                    directory,
                    "-loglevel", "error", "-y", "-f", "lavfi",
                    "-i", "testsrc2=size=640x360:rate=30:duration=8",
                    "-c:v", "libx264", "-b:v", "6M", "-pix_fmt", "yuv420p",
                    sourcePath));

            long sourceBytes = new FileInfo(sourcePath).Length;
            Assert.True(
                sourceBytes > 2 * VideoCompressionPolicy.Mebibyte,
                "测试源文件太小，无法验证目标大小压缩");

            string outputPath = Path.Combine(directory, "compressed.mp4");
            long targetBytes = VideoCompressionPolicy.Mebibyte;
            var progressValues = new List<int>();
            var service = new VideoCompressionService(
                () => ffmpegPath,
                new FfmpegWorkLimiter(1));
            VideoCompressionResult result = await service.CompressAsync(
                new VideoCompressionRequest(
                    new VideoCompressionSource(
                        "TEST-001",
                        "source.mp4",
                        sourcePath,
                        "h264",
                        8,
                        sourceBytes),
                    outputPath,
                    targetBytes,
                    VideoCompressionCodecMode.H264),
                new InlineProgress<VideoCompressionProgress>(
                    value =>
                    {
                        lock (progressValues)
                            progressValues.Add(value.Percent);
                    }),
                TestContext.Current.CancellationToken);

            Assert.True(File.Exists(sourcePath));
            Assert.True(File.Exists(outputPath));
            Assert.Equal(sourceBytes, new FileInfo(sourcePath).Length);
            Assert.InRange(result.FileSizeBytes, 1, targetBytes);
            Assert.True(progressValues.Count > 2);
            Assert.Equal(progressValues.OrderBy(value => value).ToList(), progressValues);
            Assert.DoesNotContain(
                Directory.EnumerateFileSystemEntries(directory),
                path => Path.GetFileName(path).Contains(".compressing", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task TranscodesHevcSourceToExplicitH264Target()
    {
        string ffmpegPath = Environment.GetEnvironmentVariable("EPM_FFMPEG_PATH")?.Trim() ?? "";
        Assert.True(
            File.Exists(ffmpegPath),
            "EPM_FFMPEG_PATH must point to the pinned ffmpeg.exe. Run tools/Test-EncodingCodecs.ps1.");

        string directory = Path.Combine(
            Path.GetTempPath(),
            $"epm-video-compression-hevc-to-h264-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePath = Path.Combine(directory, "source-h265.mp4");
            Assert.Equal(
                0,
                await RunAsync(
                    ffmpegPath,
                    directory,
                    "-loglevel", "error", "-y", "-f", "lavfi",
                    "-i", "testsrc2=size=640x360:rate=30:duration=6",
                    "-c:v", "libx265", "-b:v", "5M", "-pix_fmt", "yuv420p",
                    sourcePath));

            long sourceBytes = new FileInfo(sourcePath).Length;
            Assert.True(
                sourceBytes > 2 * VideoCompressionPolicy.Mebibyte,
                "测试源文件太小，无法验证 H.265 到 H.264 的显式转换");

            string outputPath = Path.Combine(directory, "compressed-h264.mp4");
            var service = new VideoCompressionService(
                () => ffmpegPath,
                new FfmpegWorkLimiter(1));
            VideoCompressionResult result = await service.CompressAsync(
                new VideoCompressionRequest(
                    new VideoCompressionSource(
                        "TEST-H265-TO-H264",
                        "source-h265.mp4",
                        sourcePath,
                        "h265",
                        6,
                        sourceBytes),
                    outputPath,
                    2 * VideoCompressionPolicy.Mebibyte,
                    VideoCompressionCodecMode.H264),
                progress: null,
                TestContext.Current.CancellationToken);

            Assert.True(File.Exists(outputPath));
            Assert.InRange(
                result.FileSizeBytes,
                1,
                2 * VideoCompressionPolicy.Mebibyte);
            Assert.Equal(
                "h264",
                await ProbeVideoCodecAsync(ffmpegPath, outputPath));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task Cancellation_CleansTemporaryWorkAndKeepsSource()
    {
        string ffmpegPath = Environment.GetEnvironmentVariable("EPM_FFMPEG_PATH")?.Trim() ?? "";
        Assert.True(
            File.Exists(ffmpegPath),
            "EPM_FFMPEG_PATH must point to the pinned ffmpeg.exe. Run tools/Test-EncodingCodecs.ps1.");

        string directory = Path.Combine(
            Path.GetTempPath(),
            $"epm-video-compression-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePath = Path.Combine(directory, "source.mp4");
            Assert.Equal(
                0,
                await RunAsync(
                    ffmpegPath,
                    directory,
                    "-loglevel", "error", "-y", "-f", "lavfi",
                    "-i", "testsrc2=size=1280x720:rate=30:duration=10",
                    "-c:v", "libx264", "-b:v", "5M", "-pix_fmt", "yuv420p",
                    sourcePath));

            long sourceBytes = new FileInfo(sourcePath).Length;
            string outputPath = Path.Combine(directory, "cancelled.mp4");
            var service = new VideoCompressionService(
                () => ffmpegPath,
                new FfmpegWorkLimiter(1));
            using var cancellation = new CancellationTokenSource();
            var transcodingStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Task<VideoCompressionResult> compression = service.CompressAsync(
                new VideoCompressionRequest(
                    new VideoCompressionSource(
                        "TEST-CANCEL",
                        "source.mp4",
                        sourcePath,
                        "h264",
                        10,
                        sourceBytes),
                    outputPath,
                    2 * VideoCompressionPolicy.Mebibyte,
                    VideoCompressionCodecMode.H265),
                new InlineProgress<VideoCompressionProgress>(
                    value =>
                    {
                        if (value.Stage == VideoCompressionStage.Transcoding)
                            transcodingStarted.TrySetResult();
                    }),
                cancellation.Token);
            await transcodingStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
            string? workOutputPath = null;
            DateTime waitUntil = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < waitUntil)
            {
                string? workDirectory = Directory
                    .EnumerateDirectories(directory, ".*.compressing")
                    .FirstOrDefault();
                string candidate = workDirectory == null
                    ? ""
                    : Path.Combine(workDirectory, "output.mp4");
                if (File.Exists(candidate)
                    && new FileInfo(candidate).Length > 0)
                {
                    workOutputPath = candidate;
                    break;
                }

                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            Assert.False(
                string.IsNullOrWhiteSpace(workOutputPath),
                "取消测试没有等到 ffmpeg 实际开始写入输出文件");
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => compression);

            Assert.True(File.Exists(sourcePath));
            Assert.False(File.Exists(outputPath));
            Assert.True(
                transcodingStarted.Task.IsCompletedSuccessfully,
                "取消测试没有等到实际转码开始");
            Assert.DoesNotContain(
                Directory.EnumerateFileSystemEntries(directory),
                path => Path.GetFileName(path).Contains(
                    ".compressing",
                    StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task<int> RunAsync(
        string ffmpegPath,
        string workingDirectory,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process? process = Process.Start(startInfo);
        Assert.NotNull(process);
        await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static async Task<string> ProbeVideoCodecAsync(
        string ffmpegPath,
        string filePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(filePath);

        using Process? process = Process.Start(startInfo);
        Assert.NotNull(process);
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return VideoCodecProbe.Parse(
            await errorTask + await outputTask);
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        internal InlineProgress(Action<T> report) => _report = report;

        public void Report(T value) => _report(value);
    }
}
