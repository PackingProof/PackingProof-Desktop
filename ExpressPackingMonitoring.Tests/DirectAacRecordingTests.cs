using ExpressPackingMonitoring.ViewModels;
using NAudio.Wave;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class DirectAacRecordingTests
{
    [Fact]
    public void DirectAudioRecording_UsesNamedPcmPipeAndAacInsideMkv()
    {
        string args = MainViewModel.BuildFFmpegArgs(
            1920,
            1080,
            15,
            "recording.mkv",
            "libx264",
            true,
            25,
            "test-audio-pipe");

        Assert.Contains("-f s16le -ar 48000 -ac 1", args);
        Assert.Contains(@"-i \\.\pipe\test-audio-pipe", args);
        Assert.Contains("-map 0:v:0 -map 1:a:0", args);
        Assert.Contains("-c:a aac -profile:a aac_low -b:a 128k", args);
        Assert.DoesNotContain(".wav", args, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VideoOnlyRecording_DoesNotAddAudioInput()
    {
        string args = MainViewModel.BuildFFmpegArgs(
            1280,
            720,
            15,
            "recording.mkv",
            "libx264",
            false,
            25);

        Assert.DoesNotContain("s16le", args);
        Assert.DoesNotContain("-c:a", args);
    }

    [Fact]
    public void VideoInput_UsesConfiguredFrameRateInsteadOfArrivalWallClock()
    {
        string args = MainViewModel.BuildFFmpegArgs(
            1280, 720, 30, "recording.mkv", "libx264", false, 25);

        Assert.Contains("-framerate 30", args);
        Assert.DoesNotContain("use_wallclock_as_timestamps", args);
    }

    [Fact]
    public void EmbeddedAudioConversion_StreamCopiesBothTracks()
    {
        string args = MainViewModel.BuildMkvToMp4Args(
            "recording.mkv",
            null,
            "recording.mp4",
            250);

        Assert.Contains("-map 0:v:0 -map 0:a? -c copy", args);
        Assert.DoesNotContain("adelay", args);
        Assert.DoesNotContain("atrim", args);
    }

    [Fact]
    public void HistoricalAudioDiagnosticWithoutWav_UsesEmbeddedAudioOrVideoOnlyPlan()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"MkvAudioMuxPlanTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string mkvPath = Path.Combine(directory, "recording.mkv");
            File.WriteAllBytes(mkvPath, [1]);
            File.WriteAllText(Path.ChangeExtension(mkvPath, ".audio.log"), "audio capture failed");

            MainViewModel.MkvAudioMuxPlan plan =
                MainViewModel.ResolveMkvAudioMuxPlan(mkvPath);

            Assert.Equal(
                MainViewModel.MkvAudioMuxMode.EmbeddedOrVideoOnly,
                plan.Mode);
            Assert.Null(plan.AudioPath);
            Assert.NotNull(plan.AudioLogPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("h265")]
    [InlineData("hevc")]
    public void HevcMkvConversion_WritesHvc1Tag(string videoCodec)
    {
        string args = MainViewModel.BuildMkvToMp4Args(
            "recording.mkv",
            null,
            "recording.mp4",
            250,
            videoCodec);

        Assert.Contains("-tag:v hvc1", args);
    }

    [Theory]
    [InlineData("h264")]
    [InlineData("av1")]
    [InlineData("")]
    [InlineData(null)]
    public void NonHevcMkvConversion_KeepsDefaultVideoTag(string? videoCodec)
    {
        string args = MainViewModel.BuildMkvToMp4Args(
            "recording.mkv",
            null,
            "recording.mp4",
            250,
            videoCodec);

        Assert.DoesNotContain("-tag:v hvc1", args);
    }

    [Fact]
    public void Mp4AcceptanceProbe_DecodesOneVideoFrame()
    {
        string args = MainViewModel.BuildMp4VideoProbeArgs("recording.mp4");

        Assert.Contains("-map 0:v:0", args);
        Assert.Contains("-frames:v 1", args);
        Assert.Contains("-f null -", args);
    }

    [Theory]
    [InlineData(250, 24000)]
    [InlineData(-250, -24000)]
    [InlineData(9000, 480000)]
    [InlineData(-9000, -480000)]
    public void DirectAudioOffset_IsAppliedToPcmAndClamped(int offsetMs, int expectedBytes)
    {
        var format = new WaveFormat(48000, 16, 1);

        int bytes = MainViewModel.CalculateInitialAudioOffsetBytes(offsetMs, format);

        Assert.Equal(expectedBytes, bytes);
        Assert.Equal(0, bytes % format.BlockAlign);
    }
}
