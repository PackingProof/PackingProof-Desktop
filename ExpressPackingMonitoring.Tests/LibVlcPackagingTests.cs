using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class LibVlcPackagingTests
{
    [Fact]
    public void Publish_KeepsOnlyLocalFileAccessPlugins()
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

        Assert.Contains("plugins\\access\\**\\*", project);
        foreach (string plugin in keptAccessPlugins)
            Assert.Contains($"plugins\\access\\{plugin}", project);
        Assert.Contains("libvlc\\win-x64\\plugins\\access\\%(Filename)%(Extension)", project);
    }

    [Theory]
    [InlineData("plugins\\access_output\\**\\*")]
    [InlineData("plugins\\mux\\**\\*")]
    [InlineData("plugins\\services_discovery\\**\\*")]
    [InlineData("plugins\\stream_out\\**\\*")]
    [InlineData("plugins\\visualization\\**\\*")]
    [InlineData("plugins\\lua\\**\\*")]
    public void Publish_ExcludesUnusedStreamingPluginGroups(string excludedPattern)
    {
        string project = ReadProject();

        Assert.Contains(excludedPattern, project);
        Assert.DoesNotContain("<LibVlcX64Lua Include=", project);
    }

    [Fact]
    public void Publish_RetainsCorePlaybackFamilies()
    {
        string project = ReadProject();

        Assert.Contains("plugins\\**\\*", project);
        Assert.Contains("libvlc.dll", project);
        Assert.Contains("libvlccore.dll", project);
        Assert.DoesNotContain("codec\\libavcodec_plugin.dll", project);
        Assert.DoesNotContain("codec\\libd3d11va_plugin.dll", project);
        Assert.DoesNotContain("codec\\libdxva2_plugin.dll", project);
        Assert.DoesNotContain("demux\\libmkv_plugin.dll", project);
        Assert.DoesNotContain("demux\\libmp4_plugin.dll", project);
        Assert.DoesNotContain("video_output\\libdirect3d11_plugin.dll", project);
        Assert.DoesNotContain("audio_output\\libwasapi_plugin.dll", project);
        Assert.DoesNotContain("LibVlcX64Hrtf", project);
        Assert.DoesNotContain("plugins\\video_filter\\**\\*", project);
        Assert.DoesNotContain("plugins\\text_renderer\\**\\*", project);
        Assert.DoesNotContain("plugins\\codec\\libaom_plugin.dll", project);
        Assert.DoesNotContain("plugins\\codec\\libvpx_plugin.dll", project);
        Assert.DoesNotContain("plugins\\codec\\liblibass_plugin.dll", project);
        Assert.DoesNotContain("plugins\\demux\\libadaptive_plugin.dll", project);
        Assert.DoesNotContain("StartsWith('misc\\')", project);
    }

    [Theory]
    [InlineData("plugins\\access_output\\**\\*")]
    [InlineData("plugins\\mux\\**\\*")]
    [InlineData("plugins\\services_discovery\\**\\*")]
    [InlineData("plugins\\stream_out\\**\\*")]
    [InlineData("plugins\\visualization\\**\\*")]
    [InlineData("plugins\\lua\\**\\*")]
    public void Publish_ExcludesUnusedPlaybackPlugins(string excludedPattern)
    {
        string project = ReadProject();

        Assert.Contains(excludedPattern, project);
    }

    private static string ReadProject() => File.ReadAllText(
        Path.Combine(FindRepositoryRoot(), "ExpressPackingMonitoring", "ExpressPackingMonitoring.csproj"),
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
