using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class MobileAppUpdatePolicyTests
{
    [Fact]
    public void MinimumPolicyIsBuiltIntoDesktopAndUsesRequiredMessage()
    {
        MobileAppUpdatePolicy policy = MobileAppUpdatePolicyProvider.MinimumPolicy;

        Assert.Equal(2, policy.SchemaVersion);
        Assert.Equal("0.5.6", policy.MinimumVersion);
        Assert.Equal(11006, policy.MinimumBuildNumber);
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
    [InlineData("""{"tag_name":"v0.6.1"}""")]
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
}
