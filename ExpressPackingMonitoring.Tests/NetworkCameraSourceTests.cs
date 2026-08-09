using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

// FFmpeg 兼容性矩阵（勿凭单一版本推断）：
// - FFmpeg 4.4.x：支持 -stimeout，不支持 -fps_mode（须用 -vsync passthrough）；
//   -timeout 用在 RTSP 上会挂起。
// - FFmpeg 8.x：支持 -fps_mode，已移除 -stimeout。
// 因此网络摄像头参数不传 socket 超时选项，由应用层超时兜底。
public sealed class NetworkCameraSourceTests
{
    [Fact]
    public void BuildArguments_DoesNotUseVersionDependentSocketTimeoutOptions()
    {
        string[] argumentSets =
        [
            NetworkCameraSource.BuildArguments("rtsp://10.0.0.8:554/stream", "tcp", useFpsMode: false),
            NetworkCameraSource.BuildArguments("rtsp://10.0.0.8:554/stream", "udp", useFpsMode: true),
            NetworkCameraSource.BuildArguments("http://10.0.0.8:8080/video.mjpg", "tcp", useFpsMode: false),
            NetworkCameraSource.BuildArguments("rtmp://10.0.0.8/live/stream", "udp", useFpsMode: false)
        ];

        foreach (string args in argumentSets)
        {
            Assert.DoesNotContain("-stimeout", args);
            Assert.DoesNotContain("-timeout", args);
            Assert.DoesNotContain("-rw_timeout", args);
        }
    }

    [Fact]
    public void BuildArguments_EveryOptionIsRecognizedByPinnedFfmpeg()
    {
        string ffmpegPath = AppPaths.FindFFmpeg();
        Assert.True(File.Exists(ffmpegPath), "ffmpeg.exe 不存在于测试输出目录");
        string fullHelp = RunFfmpegFullHelp(ffmpegPath);

        string[] argumentSets =
        [
            NetworkCameraSource.BuildArguments("rtsp://10.0.0.8:554/stream", "tcp", useFpsMode: false),
            NetworkCameraSource.BuildArguments("rtsp://10.0.0.8:554/stream", "udp", useFpsMode: false),
            NetworkCameraSource.BuildArguments("http://10.0.0.8:8080/video.mjpg", "tcp", useFpsMode: false),
            NetworkCameraSource.BuildArguments("rtmp://10.0.0.8/live/stream", "udp", useFpsMode: false)
        ];

        foreach (string args in argumentSets)
        {
            foreach (string option in ExtractOptionNames(args))
            {
                // -nostdin 是命令行别名，不在 -h full 的选项列表中列出。
                if (option == "nostdin")
                    continue;
                Assert.True(
                    IsRecognizedByFullHelp(fullHelp, option),
                    $"随包 ffmpeg 不识别参数 -{option}：{args}");
            }
        }
    }

    [Fact]
    public void BuildArguments_RtspUsesTcpTransportAndNoAudio()
    {
        string args = NetworkCameraSource.BuildArguments(
            "rtsp://admin:secret@10.0.0.8:554/stream",
            "tcp",
            useFpsMode: false);

        Assert.Contains("-rtsp_transport tcp", args);
        Assert.DoesNotContain("-stimeout", args);
        Assert.Contains(" -an ", args);
        Assert.Contains("-vsync passthrough", args);
        Assert.DoesNotContain("-fps_mode", args);
        Assert.Contains("-f rawvideo", args);
        Assert.Contains("-pix_fmt bgr24", args);
        Assert.Contains("pipe:1", args);
        Assert.Contains("rtsp://admin:secret@10.0.0.8:554/stream", args);
    }

    [Fact]
    public void BuildArguments_UsesFpsModeWhenSupported()
    {
        string args = NetworkCameraSource.BuildArguments(
            "rtsp://admin:secret@10.0.0.8:554/stream",
            "tcp",
            useFpsMode: true);

        Assert.Contains("-fps_mode passthrough", args);
        Assert.DoesNotContain("-vsync", args);
    }

    [Fact]
    public void BuildArguments_OnlyRtspGetsTransportOption()
    {
        Assert.DoesNotContain(
            "-rtsp_transport",
            NetworkCameraSource.BuildArguments("rtmp://10.0.0.8/live/stream", "tcp", useFpsMode: false));
        Assert.DoesNotContain(
            "-timeout",
            NetworkCameraSource.BuildArguments("http://10.0.0.8:8080/video.mjpg", "tcp", useFpsMode: false));
        Assert.DoesNotContain(
            "-timeout",
            NetworkCameraSource.BuildArguments("rtmp://10.0.0.8/live/stream", "udp", useFpsMode: false));
    }

    [Theory]
    [InlineData("ffmpeg version 4.4.1-full_build-www.gyan.dev Copyright (c) 2000-2021 the FFmpeg developers", false)]
    [InlineData("ffmpeg version 5.0.1-full_build-www.gyan.dev Copyright (c) 2000-2022 the FFmpeg developers", false)]
    [InlineData("ffmpeg version 5.1.2-full_build-www.gyan.dev Copyright (c) 2000-2022 the FFmpeg developers", true)]
    [InlineData("ffmpeg version 8.0.1-essentials_build-www.gyan.dev Copyright (c) 2000-2025 the FFmpeg developers", true)]
    [InlineData("ffmpeg version N-110001-gabcdef", false)]
    [InlineData("", false)]
    public void ParseVersionSupportsFpsMode_MatchesReleaseCutoff(string versionLine, bool expected)
    {
        Assert.Equal(expected, NetworkCameraSource.ParseVersionSupportsFpsMode(versionLine));
    }

    [Fact]
    public void SanitizeStderrText_MasksUrlCredentials()
    {
        string url = "rtsp://admin:secret@10.0.0.8:554/stream";
        string line = $"{url}: Connection timed out";

        string sanitized = NetworkCameraSource.SanitizeStderrText(line, url);

        Assert.DoesNotContain("secret", sanitized);
        Assert.Contains("admin:***@10.0.0.8", sanitized);
        Assert.Contains("Connection timed out", sanitized);
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

        int port;
        using (var portProbe = new System.Net.Sockets.UdpClient(0))
            port = ((System.Net.IPEndPoint)portProbe.Client!.LocalEndPoint!).Port;
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

    private static string RunFfmpegFullHelp(string ffmpegPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = "-hide_banner -h full",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(5000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
        return output;
    }

    private static IEnumerable<string> ExtractOptionNames(string args)
    {
        return Regex.Matches(args, "(^|\\s)-([A-Za-z_]+)(?=\\s|<|\")")
            .Select(match => match.Groups[2].Value)
            .Distinct(StringComparer.Ordinal);
    }

    private static bool IsRecognizedByFullHelp(string fullHelp, string option)
    {
        return Regex.IsMatch(
            fullHelp,
            "(^|\\s)-" + Regex.Escape(option) + "(\\s|<)");
    }
}
