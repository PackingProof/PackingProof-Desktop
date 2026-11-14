using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class PreRecordDiagnosticsTests
{
    [Fact]
    public void RecordingStartupResetsPausedPreRecordStatsWindow()
    {
        string source = ReadProjectFile(
            "src",
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Recording.cs");

        int flush = source.IndexOf(
            "await Task.Run(() => FlushPreRecordFrames",
            StringComparison.Ordinal);
        Assert.True(flush > 0, "未找到预录帧回灌入口");
        string afterFlush = source[flush..Math.Min(source.Length, flush + 1400)];
        Assert.Contains(
            "if (!ShouldCaptureEventRecordingBufferFrame())",
            afterFlush,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResetPreRecordCaptureStatsWindow();",
            afterFlush,
            StringComparison.Ordinal);

        int reset = source.IndexOf(
            "private void ResetPreRecordCaptureStatsWindow()",
            StringComparison.Ordinal);
        Assert.True(reset > 0, "未找到预录统计窗口重置入口");
        string resetBody = source[reset..Math.Min(source.Length, reset + 500)];
        Assert.Contains("_preRecordStatsWindowStartTicks = 0;", resetBody, StringComparison.Ordinal);
        Assert.Contains("_preRecordStatsLastFrameTicks = 0;", resetBody, StringComparison.Ordinal);
        Assert.Contains("_preRecordStatsMaxGapMs = 0;", resetBody, StringComparison.Ordinal);
    }

    private static string ReadProjectFile(params string[] relativeParts) =>
        File.ReadAllText(
            Path.Combine([FindRepositoryRoot(), .. relativeParts]),
            Encoding.UTF8);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "ExpressPackingMonitoring.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException(
            "ExpressPackingMonitoring repository root was not found.");
    }
}
