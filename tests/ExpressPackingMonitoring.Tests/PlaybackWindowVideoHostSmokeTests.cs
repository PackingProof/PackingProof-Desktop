using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ExpressPackingMonitoring.UI;
using LibVLCSharp.Shared;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

[Collection("WPF render tests")]
public sealed class PlaybackWindowVideoHostSmokeTests
{
    private const string EnableVariable = "EPM_PLAYBACK_SMOKE";

    [Fact]
    public void Playback_UsesOnlyTheEmbeddedChildWindowAndCloses()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableVariable),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Skip($"未设置 {EnableVariable}=1，跳过真实视频回放冒烟");
        }

        string repositoryRoot = FindRepositoryRoot();
        string preferredFfmpeg = Path.Combine(
            repositoryRoot,
            "package",
            "dependency-cache",
            "ffmpeg",
            "4.4.1",
            "ffmpeg.exe");
        string? ffmpeg = File.Exists(preferredFfmpeg)
            ? preferredFfmpeg
            : Directory
                .EnumerateFiles(
                    Path.Combine(repositoryRoot, "package"),
                    "ffmpeg.exe",
                    SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        if (ffmpeg == null)
            Assert.Skip("找不到 ffmpeg.exe，跳过真实视频回放冒烟");

        string folder = Path.Combine(
            Path.GetTempPath(),
            "packingproof-playback-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string videoPath = Path.Combine(folder, "playback-smoke.mp4");
            GenerateVideo(ffmpeg, videoPath);
            RunOnStaThread(() => ExerciseEmbeddedPlayback(videoPath));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void GenerateVideo(string ffmpeg, string videoPath)
    {
        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[]
        {
            "-y", "-nostdin", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=c=black:s=640x360:d=6:r=30",
            "-frames:v", "90", "-an", "-c:v", "libx264",
            "-pix_fmt", "yuv420p", videoPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("ffmpeg 未能启动");
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("生成回放测试视频超时");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
    }

    private static void ExerciseEmbeddedPlayback(string videoPath)
    {
        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        Exception? failure = null;
        var host = new VlcVideoHost();
        var window = new Window
        {
            Title = "PackingProof playback smoke",
            Width = 640,
            Height = 360,
            Content = host
        };
        host.Visibility = Visibility.Hidden;

        Core.Initialize();
        using var libVlc = new LibVLC("--no-video-title-show");
        using var player = new MediaPlayer(libVlc);
        using var media = new Media(libVlc, new Uri(videoPath));
        media.AddOption(":start-paused");
        DispatcherTimer? revealFallback = null;
        bool firstFrameSignalReceived = false;

        window.ContentRendered += (_, _) =>
        {
            try
            {
                Assert.False(host.IsVisible);
                Assert.True(host.Attach(player));
                Assert.Equal(host.VideoHwnd, player.Hwnd);

                void RevealHost()
                {
                    firstFrameSignalReceived = true;
                    host.Visibility = Visibility.Visible;
                }
                player.Vout += (_, _) =>
                {
                    application.Dispatcher.BeginInvoke(RevealHost);
                };
                player.Paused += (_, _) =>
                {
                    application.Dispatcher.BeginInvoke(RevealHost);
                };
                Assert.True(player.Play(media));

                // 隐藏的 HwndHost 可能收不到 Vout；生产窗口在 1.5 秒后兜底显示宿主。
                revealFallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                revealFallback.Tick += (_, _) =>
                {
                    revealFallback.Stop();
                    host.Visibility = Visibility.Visible;
                };
                revealFallback.Start();
            }
            catch (Exception ex)
            {
                failure = ex;
                application.Shutdown(1);
                return;
            }

            var waitStarted = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                try
                {
                    if (!host.IsVisible)
                    {
                        if (waitStarted.Elapsed < TimeSpan.FromSeconds(5))
                            return;
                        throw new TimeoutException("等待视频输出并显示内嵌宿主的超时");
                    }

                    if (waitStarted.Elapsed < TimeSpan.FromSeconds(1))
                        return;

                    timer.Stop();
                    revealFallback?.Stop();
                    Assert.True(firstFrameSignalReceived, "普通 H.264 视频应由 Vout 或 Paused 通知首帧就绪，而不是依赖超时兜底");
                    string[] titles = GetVisibleWindowTitlesForCurrentProcess();
                    Assert.Contains(titles, title => title.Contains("PackingProof playback smoke", StringComparison.Ordinal));
                    Assert.DoesNotContain(titles, title => title.Contains("LibVLCSharp.WPF", StringComparison.Ordinal));
                    Assert.DoesNotContain(titles, title => title.Contains("VLC (Direct3D11 output)", StringComparison.Ordinal));

                    host.Detach(player);
                    window.Close();
                    Assert.False(window.IsVisible);
                    application.Shutdown(0);
                }
                catch (Exception ex)
                {
                    failure = ex;
                    application.Shutdown(1);
                }
            };
            timer.Start();
        };

        int exitCode = application.Run(window);
        if (failure != null)
            throw new Xunit.Sdk.XunitException($"真实视频回放冒烟失败：{failure}");
        Assert.Equal(0, exitCode);
    }

    private static string[] GetVisibleWindowTitlesForCurrentProcess()
    {
        var titles = new List<string>();
        uint processId = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint ownerProcessId);
            if (ownerProcessId != processId || !IsWindowVisible(hwnd))
                return true;

            int length = GetWindowTextLength(hwnd);
            if (length <= 0)
                return true;

            var title = new StringBuilder(length + 1);
            _ = GetWindowText(hwnd, title, title.Capacity);
            titles.Add(title.ToString());
            return true;
        }, IntPtr.Zero);
        return [.. titles];
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "真实视频回放冒烟超时");
        if (failure != null)
            throw new Xunit.Sdk.XunitException($"真实视频回放冒烟失败：{failure}");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("ExpressPackingMonitoring repository root was not found.");
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
}
