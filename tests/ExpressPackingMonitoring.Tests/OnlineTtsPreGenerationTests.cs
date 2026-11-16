using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// Edge TTS 在线生成不占用本地推理资源，录像期间也必须继续消费后台预生成队列。
/// </summary>
public sealed class OnlineTtsPreGenerationTests
{
    [Fact]
    public void PreGenerationQueue_DoesNotWaitForRecordingPauseGate()
    {
        string source = File.ReadAllText(Path.Combine(
            FindSourceDirectory(),
            "Audio",
            "SpeechService.cs"));

        Assert.DoesNotContain("WaitWhilePaused", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PauseForRecording", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_speechProcessingGate", source, StringComparison.Ordinal);
    }

    private static string FindSourceDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "src",
                "ExpressPackingMonitoring");
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("未找到应用源码目录");
    }
}
