using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Helpers;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class NetworkCameraStreamCopyRecorderTests
{
    [Theory]
    [InlineData("h264", true)]
    [InlineData("avc", true)]
    [InlineData("h265", true)]
    [InlineData("hevc", true)]
    [InlineData("mjpeg", false)]
    public void SupportsOnlyH264AndH265(
        string codec,
        bool expected)
    {
        Assert.Equal(expected, NetworkCameraStreamCopyRecorder.SupportsCodec(codec));
    }

    [Fact]
    public void BuildArguments_CopiesH265WithoutReencoding()
    {
        string args = NetworkCameraStreamCopyRecorder.BuildArguments(
            "rtsp://admin:secret@10.0.0.8/live",
            "udp",
            "D:\\Videos\\clip.mkv");

        Assert.Contains("-rtsp_transport udp", args, StringComparison.Ordinal);
        Assert.Contains("-map 0:v:0", args, StringComparison.Ordinal);
        Assert.Contains("-c:v copy", args, StringComparison.Ordinal);
        Assert.Contains("-f matroska", args, StringComparison.Ordinal);
        Assert.DoesNotContain("libx265", args, StringComparison.Ordinal);
        Assert.DoesNotContain("hevc_nvenc", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArguments_DoesNotAddRtspTransportForHttpSource()
    {
        string args = NetworkCameraStreamCopyRecorder.BuildArguments(
            "http://10.0.0.8/live.ts",
            "tcp",
            "clip.mkv");

        Assert.DoesNotContain("-rtsp_transport", args, StringComparison.Ordinal);
        Assert.Contains("-c:v copy", args, StringComparison.Ordinal);
    }

    [Fact]
    public void StreamCopyIsOptIn()
    {
        Assert.False(new AppConfig().EnableNetworkCameraStreamCopy);
    }

    [Fact]
    public void StreamCopyEncoderLabelIsUserFacing()
    {
        string label = EncodingHelper.GetEncoderLabel(
            NetworkCameraStreamCopyRecorder.EncoderId);
        Assert.DoesNotContain(
            NetworkCameraStreamCopyRecorder.EncoderId,
            label,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecordingWiresFailureCleanupAndMonitorAfterDatabaseInsert()
    {
        string recording = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Recording.cs"));
        string main = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.cs"));
        string recorder = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "Services",
            "NetworkCameraStreamCopyRecorder.cs"));

        Assert.Contains("streamCopyInterrupted", recording, StringComparison.Ordinal);
        Assert.Contains("RecordRecordingFailed", recording, StringComparison.Ordinal);
        Assert.Contains(
            "network_stream_copy_interrupted",
            recording,
            StringComparison.Ordinal);
        Assert.Contains("MarkVideoDeleted", recording, StringComparison.Ordinal);
        Assert.Contains("network-stream-copy-file-stalled", recording, StringComparison.Ordinal);
        int databaseInsert = recording.IndexOf(
            "Database record inserted",
            StringComparison.Ordinal);
        int monitorStart = recording.IndexOf(
            "StartNetworkStreamCopyMonitorIfNeeded();",
            StringComparison.Ordinal);
        Assert.True(monitorStart > databaseInsert);
        Assert.Contains("streamCopyRecorder.StopAsync", main, StringComparison.Ordinal);
        Assert.Contains("DeletePartialFile", recorder, StringComparison.Ordinal);
        Assert.Contains("WaitForExitAsync", recorder, StringComparison.Ordinal);
    }

    [Fact]
    public void EnablingStreamCopyWarnsWithoutOverwritingRecordingSettings()
    {
        string settingsXaml = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "SettingsWindow.xaml"));
        string settingsCode = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "SettingsWindow.NetworkStreamCopy.cs"));
        string recording = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Recording.cs"));

        Assert.Contains(
            "NetworkCameraStreamCopyToggle_Changed",
            settingsXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "NetworkCameraStreamCopyToggle_PreviewMouseLeftButtonDown",
            settingsXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "NetworkCameraStreamCopyToggle_PreviewKeyDown",
            settingsXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "NetworkCameraStreamCopyUnavailableFeatures",
            settingsCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "以下功能将失效",
            settingsCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "AppDialog.Confirm(",
            settingsCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "_networkCameraStreamCopyControlStates",
            settingsCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "MicSelectionRow",
            settingsXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "AudioSyncOffsetRow",
            settingsXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Config.EnableEventRecordingBuffer = false",
            settingsCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Config.EnableWatermark = false",
            settingsCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Config.EnableThirdPartyWatermark = false",
            settingsCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Config.EnableAudioRecording = false",
            settingsCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Config.CameraRotationDegrees = 0",
            settingsCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SupportsRecordingSettings",
            recording,
            StringComparison.Ordinal);
        Assert.Contains(
            "NetworkCameraStreamCopyRecorder.SupportsCodec(",
            recording,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine(
                [current.FullName, .. relativeParts]);
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(
            string.Join(Path.DirectorySeparatorChar, relativeParts));
    }
}
