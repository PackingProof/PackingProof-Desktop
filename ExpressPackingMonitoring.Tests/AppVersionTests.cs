using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class AppVersionTests
{
    [Fact]
    public void ResolveCommitId_PrefersAssemblyMetadataAndNormalizesCase()
    {
        string commit = AppVersion.ResolveCommitId(
            "E1381EAB0123456789ABCDEF0123456789ABCDEF",
            "0.0.31+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        Assert.Equal("e1381eab0123456789abcdef0123456789abcdef", commit);
        Assert.Equal("e1381eab", AppVersion.ShortenCommitId(commit));
    }

    [Fact]
    public void ResolveCommitId_FallsBackToInformationalVersionCommitSuffix()
    {
        Assert.Equal(
            "0123456789abcdef0123456789abcdef01234567",
            AppVersion.ResolveCommitId(null, "0.0.31+0123456789abcdef0123456789abcdef01234567"));
    }

    [Fact]
    public void Current_DoesNotFallBackToPlaceholderVersion()
    {
        Assert.NotEqual("v0.0.0", AppVersion.Current);
        Assert.False(string.IsNullOrWhiteSpace(AppVersion.Current));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("not-a-commit", "0.0.31")]
    [InlineData(null, "0.0.31+dirty.main")]
    [InlineData("123456", "0.0.31+123456")]
    public void ResolveCommitId_ReturnsEmptyWhenNoValidCommitIsEmbedded(
        string? metadataCommit,
        string? informationalVersion)
    {
        Assert.Empty(AppVersion.ResolveCommitId(metadataCommit, informationalVersion));
    }

    [Fact]
    public void CommitLabels_AreLocalized()
    {
        Assert.Equal("Commit unknown", AppLanguage.Get("Commit 未知", new("en-US")));
        Assert.Equal("Full commit ID: {0}", AppLanguage.Get("完整 Commit ID：{0}", new("en-US")));
        Assert.Equal("Click to show advanced options", AppLanguage.Get("点击显示高级选项", new("en-US")));
        Assert.Equal("Click to hide advanced options", AppLanguage.Get("点击隐藏高级选项", new("en-US")));
    }
}
