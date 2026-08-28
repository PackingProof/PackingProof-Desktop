using ExpressPackingMonitoring.Audio;
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
}
