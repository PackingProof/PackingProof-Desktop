using System.Diagnostics;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.EncodingIntegrationTests;

/// <summary>
/// 交付副本（超限转码）走的就是这套两遍编码参数。这里用真实 ffmpeg 跑一次，确认第二遍真的基于
/// 第一遍的统计结果，而不是像以前那样被 ffmpeg 静默忽略（那时两遍等于两次独立编码）。
/// </summary>
public sealed class ExtensionRecordingDeliveryTwoPassTests
{
    private const string SourceArguments =
        "-loglevel error -f lavfi -i testsrc2=size=640x360:rate=30:duration=6 -c:v libx264 -pix_fmt yuv420p -y \"{0}\"";

    [Fact]
    public async Task X265TwoPass_ProducesStatisticsAndOutput()
    {
        string ffmpegPath = Environment.GetEnvironmentVariable("EPM_FFMPEG_PATH")?.Trim() ?? "";
        Assert.True(File.Exists(ffmpegPath),
            "EPM_FFMPEG_PATH must point to the pinned ffmpeg.exe. Run tools/Test-EncodingCodecs.ps1.");

        string directory = Path.Combine(Path.GetTempPath(), $"epm-delivery-twopass-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePath = Path.Combine(directory, "source.mp4");
            Assert.Equal(0, await RunAsync(ffmpegPath, string.Format(SourceArguments, sourcePath), directory));

            const long targetBytes = 1024 * 1024;
            Assert.True(TargetSizeTranscodeBitrate.TryCalculate(6, targetBytes, out TargetSizeTranscodeBitrate? bitrate));

            string outputPath = Path.Combine(directory, "delivery.mp4");
            var progressValues = new List<double>();
            bool completed = TargetSizeTranscodeEngine.RunTwoPass(
                ffmpegPath, sourcePath, outputPath, outputPath + ".pass", "libx265", bitrate!, 6,
                value => { lock (progressValues) progressValues.Add(value); },
                TestContext.Current.CancellationToken,
                out TargetSizeTranscodeFailure failure);

            Assert.Equal(TargetSizeTranscodeFailure.None, failure);
            Assert.True(completed);
            Assert.NotEmpty(progressValues);
            Assert.Equal(progressValues.OrderBy(value => value).ToList(), progressValues);
            Assert.True(progressValues[^1] > 0.9d, "两遍跑完时进度应接近 1");
            Assert.True(new FileInfo(outputPath).Length > 0);

            // 只有真正把 pass 交给 x265 时才会留下统计文件；只传 ffmpeg 的 -pass 1/-pass 2 是不会产生的。
            string statsPath = Path.Combine(directory, "x265_2pass.log");
            Assert.True(File.Exists(statsPath), "两遍编码没有产出 x265 统计文件，第二遍没有基于第一遍统计");
            Assert.True(new FileInfo(statsPath).Length > 0);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static async Task<int> RunAsync(string ffmpegPath, string arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using Process? process = Process.Start(startInfo);
        Assert.NotNull(process);
        await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
