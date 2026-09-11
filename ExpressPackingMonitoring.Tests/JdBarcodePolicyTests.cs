using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class JdBarcodePolicyTests
{
    [Theory]
    [InlineData("JDX058278770023", "JDX058278770023-1-1-")]
    [InlineData("JDX058278770023-1-1-", "JDX058278770023")]
    [InlineData("JDAA123456789", "JDAA123456789-1-1-")]
    [InlineData("JDZ9123456789-1-1-", "JDZ9123456789")]
    public void JdxCameraAliasStopsAfterReentryAndCooldown(string current, string scanned)
    {
        var tracker = new CameraBarcodeStabilityTracker();
        var now = DateTimeOffset.UtcNow;
        tracker.LockFromStartTrigger(current, now);
        Assert.Empty(tracker.Observe(scanned, now.AddMilliseconds(100)).ConfirmedCode);
        tracker.Observe(null, now.AddSeconds(1));
        tracker.Observe(null, now.AddSeconds(5));
        tracker.Observe(scanned, now.AddSeconds(6));
        var confirmed = tracker.Observe(scanned, now.AddSeconds(6.1)).ConfirmedCode;
        Assert.Equal(scanned, confirmed);
        Assert.Equal(BarcodeRecordingDecisionAction.Queue,
            BarcodeRecordingDecisionPolicy.Evaluate(confirmed, true, true, true, current, true, true, null).Action);
        var decision = BarcodeRecordingDecisionPolicy.Evaluate(confirmed, true, true, true, current, true, false, null);
        Assert.Equal(BarcodeRecordingDecisionReason.SameCodeMatched, decision.Reason);
        Assert.Equal(BarcodeRecordingDecisionAction.Stop, decision.Action);
        Assert.Equal(BarcodeRecordingDecisionReason.CameraCurrentCodeIgnored,
            BarcodeRecordingDecisionPolicy.Evaluate(confirmed, true, true, true, current, false, false, null).Reason);
    }

    [Fact]
    public void JdxDifferentPackagesRemainDistinct()
    {
        Assert.Equal("JDX058278770023", JdBarcodePolicy.Waybill("JDX058278770023-1-2-"));
        Assert.Equal(BarcodeRecordingDecisionReason.RecordingOrderMismatch,
            BarcodeRecordingDecisionPolicy.Evaluate("JDX058278770023-2-2-", true, true, true,
                "JDX058278770023-1-2-", true, false, null).Reason);
    }

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
