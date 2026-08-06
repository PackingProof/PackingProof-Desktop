using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class NetworkCameraSourceTests
{
    [Fact]
    public void BuildArguments_RtspUsesTcpTransportAndNoAudio()
    {
        string args = NetworkCameraSource.BuildArguments(
            "rtsp://admin:secret@10.0.0.8:554/stream",
            "tcp");

        Assert.Contains("-rtsp_transport tcp", args);
        Assert.Contains("-stimeout 5000000", args);
        Assert.Contains(" -an ", args);
        Assert.Contains("-f rawvideo", args);
        Assert.Contains("-pix_fmt bgr24", args);
        Assert.Contains("pipe:1", args);
        Assert.Contains("rtsp://admin:secret@10.0.0.8:554/stream", args);
    }

    [Fact]
    public void BuildArguments_OnlyRtspGetsTransportOption()
    {
        Assert.DoesNotContain(
            "-rtsp_transport",
            NetworkCameraSource.BuildArguments("rtmp://10.0.0.8/live/stream", "tcp"));
        Assert.Contains(
            "-timeout 5000000",
            NetworkCameraSource.BuildArguments("http://10.0.0.8:8080/video.mjpg", "tcp"));
        Assert.DoesNotContain(
            "-timeout",
            NetworkCameraSource.BuildArguments("rtmp://10.0.0.8/live/stream", "udp"));
    }

    [Theory]
    [InlineData(
        "Stream #0:0: Video: h264 (High), yuv420p(progressive), 1920x1080 [SAR 1:1 DAR 16:9], 25 fps, 25 tbr, 90k tbn, 50 tbc",
        1920,
        1080,
        25)]
    [InlineData(
        "Stream #0:0: Video: mjpeg, yuvj420p(pc), 640x480, 15 fps, 15 tbr, 90k tbn",
        640,
        480,
        15)]
    public void TryParseStreamInfo_ParsesResolutionAndFps(
        string line,
        int expectedWidth,
        int expectedHeight,
        int expectedFps)
    {
        Assert.True(NetworkCameraSource.TryParseStreamInfo(line, out int width, out int height, out int fps));
        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
        Assert.Equal(expectedFps, fps);
    }

    [Fact]
    public void TryParseStreamInfo_ReturnsFalseForNonStreamLine()
    {
        Assert.False(
            NetworkCameraSource.TryParseStreamInfo(
                "Input #0, rtsp, from 'rtsp://10.0.0.8/stream':",
                out _,
                out _,
                out _));
    }

    [Fact]
    public void TryParseStreamInfo_FallsBackToZeroFpsWhenMissing()
    {
        Assert.True(
            NetworkCameraSource.TryParseStreamInfo(
                "Stream #0:0: Video: h264 (High), yuv420p, 1280x720, 90k tbn",
                out int width,
                out int height,
                out int fps));
        Assert.Equal(1280, width);
        Assert.Equal(720, height);
        Assert.Equal(0, fps);
    }

    [Fact]
    public async Task StartAsync_ReadsFramesFromLocalUdpStream()
    {
        string ffmpegPath = AppPaths.FindFFmpeg();
        Assert.True(!string.IsNullOrWhiteSpace(ffmpegPath) && File.Exists(ffmpegPath),
            "ffmpeg.exe 不存在于测试输出目录");

        int port = Random.Shared.Next(20000, 60000);
        string url = $"udp://127.0.0.1:{port}";
        using var server = Process.Start(new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments =
                $"-hide_banner -nostdin -loglevel error " +
                $"-f lavfi -i testsrc2=size=320x240:rate=15 " +
                $"-c:v mpeg4 -q:v 5 -f mpegts {url}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        });
        Assert.NotNull(server);

        try
        {
            using var source = new NetworkCameraSource(url, "tcp", 15, ffmpegPath);
            bool connected = await source.StartAsync();
            Assert.True(connected, source.LastError ?? "连接失败");
            Assert.Equal(320, source.ActualWidth);
            Assert.Equal(240, source.ActualHeight);
            Assert.Equal(15, source.ActualFps);
            Assert.Single(source.NativeModes);

            var frameReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int receivedWidth = 0;
            int receivedHeight = 0;
            source.FrameReady += (_, e) =>
            {
                receivedWidth = e.Frame.Width;
                receivedHeight = e.Frame.Height;
                e.Frame.Dispose();
                frameReceived.TrySetResult(true);
            };

            await frameReceived.Task.WaitAsync(
                TimeSpan.FromSeconds(15),
                TestContext.Current.CancellationToken);
            Assert.Equal(320, receivedWidth);
            Assert.Equal(240, receivedHeight);
            source.Stop();
            Assert.False(source.IsRunning);
        }
        finally
        {
            try { server.Kill(entireProcessTree: true); } catch { }
            try { server.WaitForExit(2000); } catch { }
        }
    }
}
