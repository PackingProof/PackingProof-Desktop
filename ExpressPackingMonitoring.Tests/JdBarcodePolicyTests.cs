using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class JdBarcodePolicyTests
{
    [Theory]
    [InlineData("JD123456789012", "JD123456789012-1-1-", true)]
    [InlineData("JD123456789012-1-2-", "JD123456789012", true)]
    [InlineData("JD123456789012-1-2-", "JD123456789012-2-2-", false)]
    [InlineData("JD123456789012", "JD999999999999-1-2-", false)]
    public void StopIdentityDoesNotMergeDifferentPackages(string current, string scanned, bool same)
    {
        var decision = BarcodeRecordingDecisionPolicy.Evaluate(
            scanned, false, true, true, current, true, false, null);
        Assert.Equal(same ? BarcodeRecordingDecisionAction.Stop : BarcodeRecordingDecisionAction.Ignore, decision.Action);
    }

    [Fact]
    public void CameraAliasStaysLockedButAnotherPackageCanConfirm()
    {
        var tracker = new CameraBarcodeStabilityTracker();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        tracker.Observe("JD123456789012", now);
        Assert.Equal("JD123456789012-1-2-", tracker.Observe("JD123456789012-1-2-", now.AddMilliseconds(100)).ConfirmedCode);
        Assert.Empty(tracker.Observe("JD123456789012", now.AddSeconds(4)).ConfirmedCode);
        Assert.Empty(tracker.Observe("JD123456789012-1-2-", now.AddSeconds(5)).ConfirmedCode);
        tracker.Observe("JD123456789012-2-2-", now.AddSeconds(6));
        Assert.Equal("JD123456789012-2-2-", tracker.Observe("JD123456789012-2-2-", now.AddSeconds(6.1)).ConfirmedCode);
    }

    [Fact]
    public void CameraAliasChangeWaitsForLabelReentryBeforeStopRecording()
    {
        var tracker = new CameraBarcodeStabilityTracker();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        tracker.Observe("JD123456789012", now);
        Assert.Equal("JD123456789012", tracker.Observe("JD123456789012", now.AddMilliseconds(100)).ConfirmedCode);

        Assert.Empty(tracker.Observe(
            "JD123456789012-1-2-", now.AddMilliseconds(200)).ConfirmedCode);
        tracker.Observe(null, now.AddMilliseconds(300));
        tracker.Observe(null, now.AddMilliseconds(3400));
        tracker.Observe("JD123456789012-1-2-", now.AddMilliseconds(3500));
        Assert.Equal("JD123456789012-1-2-", tracker.Observe(
            "JD123456789012-1-2-", now.AddMilliseconds(3600)).ConfirmedCode);
    }

    [Fact]
    public void CameraStartLocksPackageAndBareAliasTogether()
    {
        var tracker = new CameraBarcodeStabilityTracker();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        tracker.Observe("JD123456789012-1-2-", now);
        tracker.Observe("JD123456789012-1-2-", now.AddMilliseconds(100));
        tracker.LockFromStartTrigger("JD123456789012-1-2-", now.AddMilliseconds(150));

        Assert.Empty(tracker.Observe(
            "JD123456789012", now.AddMilliseconds(200)).ConfirmedCode);
    }
    [Theory]
    [InlineData("JD123456789012-1-1-", "JD123456789012-1-1-")]
    [InlineData(" jdva1234567891234-1-1- ", "JDVA1234567891234-1-1-")]
    [InlineData("JD123456789012-1-2-", "JD123456789012-1-2-")]
    [InlineData("JD123456789012-2-2-", "JD123456789012-2-2-")]
    [InlineData("JD123456789012-1-1", "JD123456789012-1-1")]
    [InlineData("YT123456789012-1-1-", "YT123456789012-1-1-")]
    [InlineData("JD123456789012-0-1-", "JD123456789012-0-1-")]
    [InlineData("JD123456789012-2-1-", "JD123456789012-2-1-")]
    [InlineData("JD123456789012-01-1-", "JD123456789012-01-1-")]
    [InlineData("JD123456789012-1-2147483648-", "JD123456789012-1-2147483648-")]
    public void NormalizePreservesPackageSuffix(string raw, string expected) =>
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
        Assert.Equal("JD123456789012-1-1-", decision.NormalizedValue);
    }

    [Theory]
    [InlineData("JD123456789012")]
    [InlineData("JD123456789012-1-2-")]
    public void StartRecordingAcceptsEitherSameLabelCode(string scanned)
    {
        BarcodeRecordingDecision decision = BarcodeRecordingDecisionPolicy.Evaluate(
            scanned, fromCamera: true, canProcess: true, isRecording: false,
            recordingOrderId: "", sameBarcodeStopEnabled: true,
            inputOnCooldown: false, orderIdRegex: null);

        Assert.Equal(BarcodeRecordingDecisionAction.Start, decision.Action);
    }

    [Theory]
    [InlineData("JD123456789012", "JD123456789012-1-2-")]
    [InlineData("JD123456789012-1-2-", "JD123456789012")]
    public void StartRecordingAliasDoesNotSwitchAnExistingRecording(string current, string scanned)
    {
        BarcodeRecordingDecision decision = BarcodeRecordingDecisionPolicy.Evaluate(
            scanned, fromCamera: true, canProcess: true, isRecording: true,
            recordingOrderId: current, sameBarcodeStopEnabled: true,
            inputOnCooldown: false, orderIdRegex: null);

        Assert.Equal(BarcodeRecordingDecisionAction.Stop, decision.Action);
    }
}
