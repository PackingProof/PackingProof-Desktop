using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class MobileAppUpdatePolicyTests
{
    [Fact]
    public void WebDownloadInfoUsesCachedReleaseOrStableFallbackImmediately()
    {
        MobileAppDownloadInfo cached = WebServer.CreateMobileAppDownloadInfo(
            new MobileAppReleaseInfo(
                "v0.5.8+11008",
                "0.5.8",
                11008,
                "https://gitee.com/PackingProof/PackingProof-Mobile/attach_files/1/download"));
        MobileAppDownloadInfo fallback = WebServer.CreateMobileAppDownloadInfo(null!);

        Assert.Equal("0.5.8", cached.Version);
        Assert.Contains("attach_files/1/download", cached.DownloadUrl, StringComparison.Ordinal);
        Assert.StartsWith("data:image/png;base64,", cached.QrCode, StringComparison.Ordinal);
        Assert.Equal("", fallback.Version);
        Assert.Equal(MobileAppUpdatePolicyProvider.ReleasesUrl, fallback.DownloadUrl);
        Assert.StartsWith("data:image/png;base64,", fallback.QrCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MinimumPolicyIsBuiltIntoDesktopAndUsesRequiredMessage()
    {
        MobileAppUpdatePolicy policy = MobileAppUpdatePolicyProvider.MinimumPolicy;

        Assert.Equal(2, policy.SchemaVersion);
        Assert.Equal("0.5.10", policy.MinimumVersion);
        Assert.Equal(11010, policy.MinimumBuildNumber);
        Assert.Equal("当前 APP 版本过低，需要更新", policy.Message);
    }

    [Fact]
    public void LatestMobileReleaseFallsBackToGiteeRepositoryWhenNoApkAssetExists()
    {
        MobileAppReleaseInfo release = MobileAppUpdatePolicyProvider.ParseLatestRelease(
            """{"tag_name":"v0.6.1+12001","name":"v0.6.1"}""");

        Assert.Equal("v0.6.1+12001", release.TagName);
        Assert.Equal("0.6.1", release.Version);
        Assert.Equal(12001, release.BuildNumber);
        Assert.Equal(
            "https://gitee.com/PackingProof/PackingProof-Mobile/releases",
            release.DownloadUrl);
    }

    [Fact]
    public void LatestMobileReleaseAcceptsShortTagAndFallsBackToSemanticVersion()
    {
        MobileAppReleaseInfo release = MobileAppUpdatePolicyProvider.ParseLatestRelease(
            """{"tag_name":"v0.6.1"}""");

        Assert.Equal("v0.6.1", release.TagName);
        Assert.Equal("0.6.1", release.Version);
        Assert.Equal(0, release.BuildNumber);
    }

    [Fact]
    public void ShortTagCanResolveBuildNumberFromTrustedBuildManifest()
    {
        string releaseJson =
            """
            {
              "tag_name":"v0.6.1",
              "assets":[
                {
                  "name":"build-manifest.json",
                  "browser_download_url":"https://gitee.com/PackingProof/PackingProof-Mobile/attach_files/1/download"
                }
              ]
            }
            """;

        Assert.Equal(
            "https://gitee.com/PackingProof/PackingProof-Mobile/attach_files/1/download",
            MobileAppUpdatePolicyProvider.ResolveBuildManifestUrl(releaseJson));
        Assert.Equal(
            12001,
            MobileAppUpdatePolicyProvider.ParseBuildManifest(
                """{"versionName":"0.6.1","versionCode":12001}""",
                "0.6.1"));
    }

    [Theory]
    [InlineData("https://example.com/build-manifest.json")]
    [InlineData("http://gitee.com/PackingProof/PackingProof-Mobile/build-manifest.json")]
    public void ShortTagRejectsUntrustedBuildManifestUrl(string url)
    {
        string releaseJson =
            $$"""{"tag_name":"v0.6.1","assets":[{"name":"build-manifest.json","browser_download_url":"{{url}}"}]}""";

        Assert.Equal("", MobileAppUpdatePolicyProvider.ResolveBuildManifestUrl(releaseJson));
    }

    [Fact]
    public void ShortTagRejectsMismatchedBuildManifest()
    {
        Assert.Throws<InvalidDataException>(() =>
            MobileAppUpdatePolicyProvider.ParseBuildManifest(
                """{"versionName":"0.6.0","versionCode":12001}""",
                "0.6.1"));
    }

    [Fact]
    public void LatestMobileReleasePrefersNamedGiteeApkAsset()
    {
        MobileAppReleaseInfo release = MobileAppUpdatePolicyProvider.ParseLatestRelease(
            """
            {
              "tag_name":"v0.6.1+12001",
              "assets":[
                {
                  "name":"another.apk",
                  "browser_download_url":"https://gitee.com/PackingProof/PackingProof-Mobile/releases/download/v0.6.1/another.apk"
                },
                {
                  "name":"PackingProof-Mobile.apk",
                  "browser_download_url":"https://gitee.com/PackingProof/PackingProof-Mobile/releases/download/v0.6.1/PackingProof-Mobile.apk"
                }
              ]
            }
            """);

        Assert.Equal(
            "https://gitee.com/PackingProof/PackingProof-Mobile/releases/download/v0.6.1/PackingProof-Mobile.apk",
            release.DownloadUrl);
    }

    [Theory]
    [InlineData("http://gitee.com/PackingProof/PackingProof-Mobile/releases/download/v0.6.1/PackingProof-Mobile.apk")]
    [InlineData("https://example.com/PackingProof-Mobile.apk")]
    [InlineData("not-a-url")]
    public void LatestMobileReleaseRejectsUntrustedApkAssetUrl(string downloadUrl)
    {
        string json =
            $$"""{"tag_name":"v0.6.1+12001","assets":[{"name":"PackingProof-Mobile.apk","browser_download_url":"{{downloadUrl}}"}]}""";

        MobileAppReleaseInfo release =
            MobileAppUpdatePolicyProvider.ParseLatestRelease(json);

        Assert.Equal(MobileAppUpdatePolicyProvider.ReleasesUrl, release.DownloadUrl);
    }

    [Theory]
    [InlineData("""{"tag_name":""}""")]
    [InlineData("""{"tag_name":"latest"}""")]
    public void InvalidLatestReleaseIsRejected(string json)
    {
        Assert.Throws<InvalidDataException>(
            () => MobileAppUpdatePolicyProvider.ParseLatestRelease(json));
    }

    [Fact]
    public void ConnectedPhoneIsPromptedOnlyWhenItIsOlderThanLatestRelease()
    {
        var latest = new MobileAppReleaseInfo("v0.6.1+12001", "0.6.1", 12001, "download");

        Assert.True(MobileAppUpdatePolicyProvider.IsUpdateAvailable(11006, latest));
        Assert.False(MobileAppUpdatePolicyProvider.IsUpdateAvailable(12001, latest));
        Assert.True(MobileAppUpdatePolicyProvider.IsUpdateAvailable(0, latest));
        Assert.False(MobileAppUpdatePolicyProvider.IsUpdateAvailable(11006, null!));
    }

    [Theory]
    [InlineData("", null, true)]
    [InlineData("0.5.10", 0, true)]
    [InlineData("unknown", 11010, true)]
    [InlineData("0.5.9", 11009, false)]
    [InlineData("0.5.10", 11010, false)]
    public void DesktopPromptsOnlyWhenConnectedMobileVersionIsUnknown(
        string version,
        int? buildNumber,
        bool expected)
    {
        var heartbeat = new ConnectedClientHeartbeat
        {
            ClientId = "mobile-client-1",
            ClientType = "mobile-app",
            DisplayName = "手机1",
            Connected = true,
            AppVersion = version,
            AppBuildNumber = buildNumber
        };

        Assert.Equal(expected, WebServer.ShouldNotifyUnknownMobileVersion(heartbeat));
    }
}
