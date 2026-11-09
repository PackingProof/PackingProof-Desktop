using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// LibVLC 插件按白名单发布：本地/映射盘走 filesystem，UNC NAS 走 FileStream + imem，
/// 必须保留解码、解封装、视频输出和音频输出这几组，同时不能再把整套插件带进包。
/// </summary>
public sealed class LibVlcPackagingTests
{
    [Fact]
    public void Publish_KeepsLocalFileAccessPlugins()
    {
        string project = ReadProject();
        string[] keptAccessPlugins =
        [
            "libfilesystem_plugin.dll",
            "libidummy_plugin.dll",
            "libattachment_plugin.dll",
            "libimem_plugin.dll",
            "libaccess_concat_plugin.dll"
        ];

        foreach (string plugin in keptAccessPlugins)
            Assert.Contains($"plugins\\access\\{plugin}", project);
        Assert.Contains("libvlc\\win-x64\\plugins\\access\\%(Filename)%(Extension)", project);
    }

    [Fact]
    public void Publish_KeepsCorePlaybackPlugins()
    {
        string project = ReadProject();
        string[] requiredPlugins =
        [
            "codec\\libavcodec_plugin.dll",
            "codec\\libdav1d_plugin.dll",
            "codec\\libd3d11va_plugin.dll",
            "codec\\libdxva2_plugin.dll",
            "codec\\libmft_plugin.dll",
            "demux\\libmkv_plugin.dll",
            "demux\\libmp4_plugin.dll",
            "demux\\libh26x_plugin.dll",
            "packetizer\\libpacketizer_h264_plugin.dll",
            "packetizer\\libpacketizer_hevc_plugin.dll",
            "packetizer\\libpacketizer_av1_plugin.dll",
            "video_output\\libdirect3d11_plugin.dll",
            "video_output\\libdirect3d9_plugin.dll",
            "audio_output\\libwasapi_plugin.dll",
            "audio_output\\libmmdevice_plugin.dll"
        ];

        Assert.Contains("libvlc.dll", project);
        Assert.Contains("libvlccore.dll", project);
        foreach (string plugin in requiredPlugins)
            Assert.Contains(plugin, project);
        Assert.Contains("video_chroma\\*.dll", project);
    }

    [Fact]
    public void Publish_DoesNotCopyWholePluginTree()
    {
        string project = ReadProject();

        Assert.DoesNotContain("plugins\\**\\*", project);
    }

    [Fact]
    public void Publish_UsesManagedStreamForUncNasPlayback()
    {
        string source = ReadRepositoryFile(
            "src", "ExpressPackingMonitoring", "UI", "PlaybackMediaSource.cs");
        string playback = ReadRepositoryFile(
            "src", "ExpressPackingMonitoring", "UI", "PlaybackWindow.xaml.cs");
        string probe = ReadRepositoryFile(
            "src", "ExpressPackingMonitoring", "UI", "PlaybackWindow.Playback.cs");

        Assert.Contains("FileStream", source, StringComparison.Ordinal);
        Assert.Contains("StreamMediaInput", source, StringComparison.Ordinal);
        Assert.Contains("IsUncPath", source, StringComparison.Ordinal);
        Assert.Contains("PlaybackMediaSource.Create(_libVLC!, video.FullPath)", playback, StringComparison.Ordinal);
        Assert.Contains("PlaybackMediaSource.Create(libVlc, path)", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("new Media(_libVLC!, new Uri(video.FullPath))", playback, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackWindow_UsesOnlyTheEmbeddedNativeVideoHost()
    {
        string xaml = ReadRepositoryFile(
            "src", "ExpressPackingMonitoring", "UI", "PlaybackWindow.xaml");
        string code = ReadRepositoryFile(
            "src", "ExpressPackingMonitoring", "UI", "PlaybackWindow.xaml.cs");
        string host = ReadRepositoryFile(
            "src", "ExpressPackingMonitoring", "UI", "VlcVideoHost.cs");
        string project = ReadProject();

        Assert.Contains("Loaded=\"PlayerView_Loaded\"", xaml, StringComparison.Ordinal);
        int waitIndex = code.IndexOf("_playerViewLoaded.Task", StringComparison.Ordinal);
        int attachIndex = code.IndexOf("PlayerView.Attach(_mediaPlayer)", StringComparison.Ordinal);
        Assert.True(
            waitIndex >= 0 && attachIndex > waitIndex,
            "视频宿主必须先完成加载并创建子 HWND，再绑定 MediaPlayer");
        Assert.Contains("<ui:VlcVideoHost", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("LibVLCSharp.WPF", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("LibVLCSharp.WPF", project, StringComparison.Ordinal);
        Assert.Contains("PackageReference Include=\"LibVLCSharp\"", project, StringComparison.Ordinal);
        Assert.Contains("class VlcVideoHost : HwndHost", host, StringComparison.Ordinal);
        Assert.Contains("player.Hwnd = _videoHwnd", host, StringComparison.Ordinal);
        Assert.DoesNotContain("new ForegroundWindow", host, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("codec\\libvpx_plugin.dll")]
    [InlineData("codec\\libaom_plugin.dll")]
    [InlineData("codec\\liblibass_plugin.dll")]
    [InlineData("demux\\libadaptive_plugin.dll")]
    [InlineData("text_renderer\\**\\*")]
    [InlineData("video_filter\\**\\*")]
    [InlineData("spu\\**\\*")]
    [InlineData("access_output\\**\\*")]
    [InlineData("mux\\**\\*")]
    [InlineData("services_discovery\\**\\*")]
    [InlineData("stream_out\\**\\*")]
    [InlineData("visualization\\**\\*")]
    [InlineData("lua\\**\\*")]
    public void Publish_ExcludesUnusedPlaybackPlugins(string excludedPattern)
    {
        string project = ReadProject();

        Assert.DoesNotContain(excludedPattern, project);
    }

    private static string ReadProject() => File.ReadAllText(
        Path.Combine(FindRepositoryRoot(), "src", "ExpressPackingMonitoring", "ExpressPackingMonitoring.csproj"),
        Encoding.UTF8);

    private static string ReadRepositoryFile(params string[] relativeParts) =>
        File.ReadAllText(
            Path.Combine([FindRepositoryRoot(), .. relativeParts]),
            Encoding.UTF8);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
