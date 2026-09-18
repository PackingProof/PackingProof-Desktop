using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using ExpressPackingMonitoring.Services.Gpu;
using ExpressPackingMonitoring.ViewModels;
using OpenCvSharp;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>显式启用的 GPU 帧音视频集成验证，不打开用户数据库或修改运行时配置。</summary>
public sealed class GpuRecordingRoundTripTests(ITestOutputHelper output)
{
    [Fact]
    public async Task GpuFramesPreserveWatermarkBurstTimingAndAudioSync()
    {
        if (Environment.GetEnvironmentVariable("PACKINGPROOF_CAPTURE_PROBE") != "1")
            return;
        string ffmpeg = Environment.GetEnvironmentVariable("EPM_FFMPEG_PATH") ?? "";
        Assert.True(File.Exists(ffmpeg), "EPM_FFMPEG_PATH is required");
        string encoder = Environment.GetEnvironmentVariable("EPM_GPU_ENCODER") ?? "h264_nvenc";
        const int width = 640, height = 360, fps = 30, frames = 90;
        string directory = Path.Combine(Path.GetTempPath(), "PackingProof-gpu-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string movie = Path.Combine(directory, "recording.mkv");
        string video = Path.Combine(directory, "decoded.bgr");
        string audio = Path.Combine(directory, "decoded.pcm");
        output.WriteLine($"Artifacts: {directory}");
        using var converter = GpuFrameConverter.TryCreate(width, height, width, height, false);
        Assert.NotNull(converter);
        using var destination = new Mat(height, width, MatType.CV_8UC3);
        byte[] source = new byte[width * height * 2];
        var bufferedFrames = new List<byte[]>();
        GCHandle pin = GCHandle.Alloc(source, GCHandleType.Pinned);
        try
        {
            for (int index = 0; index < frames; index++)
            {
                // 第一秒暗灰，之后亮灰；下半图不受水印影响，用来定位视频时刻。
                for (int pixel = 0; pixel < source.Length; pixel += 2)
                {
                    source[pixel] = (byte)(index < fps ? 48 : 200);
                    source[pixel + 1] = 128;
                }
                Assert.True(converter.TryRender(pin.AddrOfPinnedObject(), width * 2, true));
                Assert.True(converter.TryReadBackInto(destination));
                using var retained = destination.Clone();
                MainViewModel.ApplyWatermarkToFrame(retained,
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(index / (double)fps), "GPU-PROBE");
                byte[] bytes = new byte[width * height * 3];
                Marshal.Copy(retained.Data, bytes, 0, bytes.Length);
                bufferedFrames.Add(bytes);
            }
        }
        finally { pin.Free(); }

        // 与视频跳变同在 1 秒处开始的 0.1 秒脉冲，使用生产的 PCM 命名管道。
        byte[] pcm = new byte[48000 * 3 * 2];
        for (int sample = 48000; sample < 52800; sample++)
        {
            short value = (short)(12000 * Math.Sin((sample - 48000) * 2 * Math.PI * 1000 / 48000));
            pcm[sample * 2] = (byte)value;
            pcm[sample * 2 + 1] = (byte)(value >> 8);
        }
        string pipeName = "PackingProof-gpu-test-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var process = Start(ffmpeg, MainViewModel.BuildFFmpegArgs(width, height, fps, movie, encoder, true, 18, pipeName), true);
        Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            Task audioWrite = Task.Run(async () =>
            {
                await pipe.WaitForConnectionAsync(timeout.Token);
                await pipe.WriteAsync(pcm, timeout.Token);
                await pipe.FlushAsync(timeout.Token);
                pipe.Dispose();
            }, timeout.Token);
            Task videoWrite = Task.Run(async () =>
            {
                // 整批送入，验证时间戳按帧率而非提交墙钟生成。
                foreach (byte[] frame in bufferedFrames)
                    await process.StandardInput.BaseStream.WriteAsync(frame, timeout.Token);
                process.StandardInput.Close();
            }, timeout.Token);
            await Task.WhenAll(audioWrite, videoWrite).WaitAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await errors);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        await Run(ffmpeg, $"-y -v error -i \"{movie}\" -map 0:v:0 -vsync 0 -f rawvideo -pix_fmt bgr24 \"{video}\"");
        await Run(ffmpeg, $"-y -v error -i \"{movie}\" -map 0:a:0 -f s16le -ar 48000 -ac 1 \"{audio}\"");
        byte[] decoded = await File.ReadAllBytesAsync(video, timeout.Token);
        int frameSize = width * height * 3;
        Assert.Equal(frames * frameSize, decoded.Length);
        int transitionFrame = -1;
        for (int index = 0; index < frames; index++)
        {
            byte level = decoded[index * frameSize + ((height - 10) * width + width / 2) * 3];
            if (level > 150) { transitionFrame = index; break; }
        }
        Assert.Equal(fps, transitionFrame);
        // 第一帧上半部需包含白色水印，下半部则保持暗灰。
        Assert.Contains(decoded.Take(width * 90 * 3), value => value > 200);
        byte[] decodedAudio = await File.ReadAllBytesAsync(audio, timeout.Token);
        int firstPulse = -1;
        for (int sample = 0; sample < decodedAudio.Length / 2; sample++)
        {
            if (Math.Abs((int)BitConverter.ToInt16(decodedAudio, sample * 2)) > 3000)
            { firstPulse = sample; break; }
        }
        Assert.True(firstPulse >= 0, "Audio pulse missing");
        double offset = firstPulse / 48000.0 - transitionFrame / (double)fps;
        Assert.InRange(Math.Abs(offset), 0, 0.1);
        output.WriteLine($"encoder={encoder} frames={frames} videoTransition={transitionFrame / (double)fps:F3}s audioPulse={firstPulse / 48000.0:F3}s delta={offset:F3}s");
    }

    private static Process Start(string ffmpeg, string arguments, bool input = false) => Process.Start(new ProcessStartInfo(ffmpeg, arguments)
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardInput = input
    }) ?? throw new InvalidOperationException("Cannot start FFmpeg");

    private static async Task Run(string ffmpeg, string arguments)
    {
        using var process = Start(ffmpeg, arguments);
        Task<string> errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(process.ExitCode == 0, await errors);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
