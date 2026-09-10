using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class JdBarcodePolicyTests
{
    [Theory]
    [InlineData("JD123456789012-1-1-", "JD123456789012")]
    [InlineData(" jdva1234567891234-1-1- ", "JDVA1234567891234")]
    [InlineData("JD123456789012-1-2-", "JD123456789012-1-2-")]
    [InlineData("JD123456789012-2-2-", "JD123456789012-2-2-")]
    [InlineData("JD123456789012-1-1", "JD123456789012-1-1")]
    [InlineData("YT123456789012-1-1-", "YT123456789012-1-1-")]
    [InlineData("JD123456789012-0-1-", "JD123456789012-0-1-")]
    [InlineData("JD123456789012-2-1-", "JD123456789012-2-1-")]
    [InlineData("JD123456789012-01-1-", "JD123456789012-01-1-")]
    [InlineData("JD123456789012-1-2147483648-", "JD123456789012-1-2147483648-")]
    public void NormalizeOnlyConfirmedSinglePackage(string raw, string expected) =>
        Assert.Equal(expected, JdBarcodePolicy.Normalize(raw));

    [Theory]
    [InlineData("JD123456789012-1-2-", true)]
    [InlineData("JD999999999999-1-2-", false)]
    [InlineData("JD123456789012-3-2-", false)]
    [InlineData("JD123456789012-1-2", false)]
    public void MatchRequiresExactWaybillAndValidPackage(string candidate, bool expected) =>
        Assert.Equal(expected, JdBarcodePolicy.MatchesPackage("JD123456789012", candidate));

    [Fact]
    public void ScannerSameCodeStopUsesNormalizedSinglePackage()
    {
        var decision = BarcodeRecordingDecisionPolicy.Evaluate(
            "JD123456789012-1-1-", false, true, true, "JD123456789012",
            true, false, null);
        Assert.Equal(BarcodeRecordingDecisionAction.Stop, decision.Action);
        Assert.Equal("JD123456789012", decision.NormalizedValue);
    }
}
