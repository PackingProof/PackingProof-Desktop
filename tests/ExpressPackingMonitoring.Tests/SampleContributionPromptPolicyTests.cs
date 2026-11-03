using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class SampleContributionPromptPolicyTests
{
    [Fact]
    public void ShouldPrompt_AfterPreviewChannelUpdate()
    {
        Assert.True(SampleContributionPromptPolicy.ShouldPrompt(
            packaged: false,
            allowPrereleaseUpdates: true,
            currentVersion: "0.0.95-preview.1",
            previousRunVersion: "0.0.94",
            promptedVersion: ""));
    }

    [Theory]
    [InlineData(true, true, "0.0.95", "0.0.94", "")]
    [InlineData(false, false, "0.0.95", "0.0.94", "")]
    [InlineData(false, true, "0.0.95", "", "")]
    [InlineData(false, true, "0.0.95", "0.0.95", "")]
    [InlineData(false, true, "0.0.95", "0.0.94", "0.0.95")]
    public void ShouldPrompt_RejectsIneligibleCases(
        bool packaged,
        bool allowPrereleaseUpdates,
        string currentVersion,
        string previousRunVersion,
        string promptedVersion)
    {
        Assert.False(SampleContributionPromptPolicy.ShouldPrompt(
            packaged,
            allowPrereleaseUpdates,
            currentVersion,
            previousRunVersion,
            promptedVersion));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void ShouldPromptAfterEnhancedRecognitionDisabled_OnlyPromptsOnTransition(
        bool wasEnabled,
        bool isEnabled,
        bool expected)
    {
        Assert.Equal(
            expected,
            SampleContributionPromptPolicy.ShouldPromptAfterEnhancedRecognitionDisabled(
                wasEnabled,
                isEnabled));
    }
}
