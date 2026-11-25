using ExpressPackingMonitoring.Audio;
using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class SpeechShortBeepTests
{
    [Fact]
    public void CreateSingleUseEdgeTtsClient_DisposeDoesNotThrow()
    {
        using var client = SpeechService.CreateSingleUseEdgeTtsClient();

        Assert.NotNull(client);
    }

    [Fact]
    public void BuildShortBeepWav_ReturnsValidShortWav()
    {
        byte[] wav = SpeechService.BuildShortBeepWav();

        Assert.True(wav.Length > 44, "WAV 文件应包含头与采样数据");
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wav, 36, 4));

        int sampleRate = BitConverter.ToInt32(wav, 24);
        int dataSize = BitConverter.ToInt32(wav, 40);
        double durationMs = dataSize / 2.0 / sampleRate * 1000.0;
        Assert.InRange(durationMs, 70, 90);

        bool hasAudio = false;
        for (int index = 44; index + 1 < wav.Length; index += 2)
        {
            if (BitConverter.ToInt16(wav, index) != 0)
            {
                hasAudio = true;
                break;
            }
        }
        Assert.True(hasAudio, "短音波形应包含非零采样");
    }

    [Fact]
    public void Preview_FallsBackToImmediateEdgeGenerationWhenWindowsTtsCannotPlay()
    {
        string repositoryRoot = FindRepositoryRoot();
        string source = File.ReadAllText(
            Path.Combine(
                repositoryRoot,
                "src",
                "ExpressPackingMonitoring",
                "Audio",
                "SpeechService.cs"),
            Encoding.UTF8);

        int previewStart = source.IndexOf(
            "public void Preview(string text)",
            StringComparison.Ordinal);
        int previewEnd = source.IndexOf(
            "public void SpeakWarning(",
            previewStart,
            StringComparison.Ordinal);
        Assert.True(previewStart >= 0 && previewEnd > previewStart);
        Assert.Contains(
            "PreferImmediateAiGeneration = true",
            source[previewStart..previewEnd],
            StringComparison.Ordinal);

        int cacheMiss = source.IndexOf(
            "Cache MISS, trying Windows TTS fallback",
            StringComparison.Ordinal);
        int windowsFallback = source.IndexOf(
            "if (SpeakWithWindowsTts(text, isWarning))",
            cacheMiss,
            StringComparison.Ordinal);
        int immediateEdge = source.IndexOf(
            "preferImmediateAiGeneration &&",
            windowsFallback,
            StringComparison.Ordinal);
        int generatedPlayback = source.IndexOf(
            "PlayAudioFileBlocking(cachePath)",
            immediateEdge,
            StringComparison.Ordinal);
        Assert.True(cacheMiss >= 0);
        Assert.True(windowsFallback > cacheMiss);
        Assert.True(immediateEdge > windowsFallback);
        Assert.True(generatedPlayback > immediateEdge);
        Assert.Contains(
            "PlaybackError?.Invoke(message)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "WAV playback failed",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsTtsPreview_UsesConfiguredPlaybackDevice()
    {
        string source = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src",
                "ExpressPackingMonitoring",
                "UI",
                "SettingsWindow.xaml.cs"),
            Encoding.UTF8);

        Assert.Contains(
            "PlaybackDeviceId = Config.PlaybackDeviceMoniker",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "PlaybackDeviceName = Config.PlaybackDeviceName",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_previewSpeechService.PlaybackDeviceId = Config.PlaybackDeviceMoniker",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_previewSpeechService.PlaybackDeviceName = Config.PlaybackDeviceName",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EdgeTts_RetriesWithoutSystemProxyAfterNetworkFailure()
    {
        string source = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src",
                "ExpressPackingMonitoring",
                "Audio",
                "SpeechService.cs"),
            Encoding.UTF8);

        Assert.Contains(
            "webSocket.Options.Proxy = null",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "catch (Exception ex) when (IsEdgeTtsNetworkFailure(ex))",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "bypassSystemProxy: true",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TtsPreprocess_DoesNotHardSplitLongChineseWithoutConfiguredWords()
    {
        using var service = new SpeechService();
        service.UpdateBreakWords([]);
        const string text = "日常用品组合套装配件替换手柄防尘盖";

        Assert.Equal(text, SpeechService.PreprocessTextForTts(text));
    }

    [Fact]
    public void TtsPreprocess_OnlyUsesConfiguredBreakWords()
    {
        using var service = new SpeechService();
        service.UpdateBreakWords(["配件"]);
        const string text = "日常用品组合套装配件替换手柄防尘盖";

        Assert.Equal(
            "日常用品组合套装，配件替换手柄防尘盖",
            SpeechService.PreprocessTextForTts(text));
    }

    private static string FindRepositoryRoot()
    {
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(Path.GetFullPath(startPath));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("ExpressPackingMonitoring repository root was not found.");
    }
}
