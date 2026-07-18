using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using OpenCvSharp;
using System.Text.Json;
using Xunit;
using ZXing;
using ZXing.Common;

namespace ExpressPackingMonitoring.Tests;

public sealed class CameraBarcodeRecognitionTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 16, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StabilityTracker_SingleHitDoesNotConfirm()
    {
        var tracker = new CameraBarcodeStabilityTracker();

        CameraBarcodeObservation observation = tracker.Observe("YT123456789012", Start);

        Assert.Equal("YT123456789012", observation.CandidateCode);
        Assert.Empty(observation.ConfirmedCode);
    }

    [Fact]
    public void StabilityTracker_TwoHitsWithinWindowConfirmOnce()
    {
        var tracker = new CameraBarcodeStabilityTracker();
        tracker.Observe("YT123456789012", Start);

        CameraBarcodeObservation confirmed = tracker.Observe("YT123456789012", Start.AddMilliseconds(250));
        CameraBarcodeObservation held = tracker.Observe("YT123456789012", Start.AddSeconds(1));

        Assert.Equal("YT123456789012", confirmed.ConfirmedCode);
        Assert.Empty(held.ConfirmedCode);
    }

    [Fact]
    public void StabilityTracker_BusyResetPreservesConfirmedCodeDebounce()
    {
        var tracker = Confirm(trackingNumber: "YT123456789012");

        tracker.Reset(preserveLockedCodes: true);
        CameraBarcodeObservation held = tracker.Observe("YT123456789012", Start.AddSeconds(2));

        Assert.Empty(held.CandidateCode);
        Assert.Empty(held.ConfirmedCode);
    }

    [Fact]
    public void StabilityTracker_FullResetAllowsSameCodeAgain()
    {
        var tracker = Confirm(trackingNumber: "YT123456789012");

        tracker.Reset();
        CameraBarcodeObservation candidate = tracker.Observe("YT123456789012", Start.AddSeconds(2));
        CameraBarcodeObservation confirmed = tracker.Observe("YT123456789012", Start.AddSeconds(2.25));

        Assert.Equal("YT123456789012", candidate.CandidateCode);
        Assert.Equal("YT123456789012", confirmed.ConfirmedCode);
    }

    [Fact]
    public void StabilityTracker_ShortLossDoesNotRearmLockedCode()
    {
        var tracker = Confirm(trackingNumber: "YT123456789012");

        tracker.Observe(null, Start.AddSeconds(1));
        CameraBarcodeObservation held = tracker.Observe("YT123456789012", Start.AddSeconds(1.2));

        Assert.Empty(held.CandidateCode);
        Assert.Empty(held.ConfirmedCode);
    }

    [Fact]
    public void StabilityTracker_RemovalForRearmDelayAllowsSameCodeAgain()
    {
        var tracker = Confirm(trackingNumber: "YT123456789012");

        tracker.Observe(null, Start.AddSeconds(2));
        CameraBarcodeObservation candidate = tracker.Observe("YT123456789012", Start.AddSeconds(2.1));
        CameraBarcodeObservation confirmed = tracker.Observe("YT123456789012", Start.AddSeconds(2.35));

        Assert.Equal("YT123456789012", candidate.CandidateCode);
        Assert.Equal("YT123456789012", confirmed.ConfirmedCode);
    }

    [Fact]
    public void StabilityTracker_DifferentCodeCanConfirmWithoutWaitingForOldCodeToRearm()
    {
        var tracker = Confirm(trackingNumber: "YT123456789012");

        CameraBarcodeObservation candidate = tracker.Observe("SF123456789012", Start.AddMilliseconds(500));
        CameraBarcodeObservation confirmed = tracker.Observe("SF123456789012", Start.AddMilliseconds(750));

        Assert.Equal("SF123456789012", candidate.CandidateCode);
        Assert.Equal("SF123456789012", confirmed.ConfirmedCode);
    }

    [Theory]
    [InlineData("YT123456789012", true)]
    [InlineData("STARTSTARTSTART", false)]
    [InlineData("SHIP123456789", false)]
    [InlineData("BACK123456789", false)]
    [InlineData("STOP123456789", false)]
    [InlineData("123", false)]
    public void CandidatePolicy_RejectsCommandsAndInvalidOrderNumbers(string value, bool expected)
    {
        Assert.Equal(expected, CameraBarcodeCandidatePolicy.IsValid(value, "^[a-zA-Z0-9-]{12,25}$"));
    }

    [Theory]
    [InlineData("YT123456789012", "YT123456789012", true, true)]
    [InlineData(" yt123456789012 ", "YT123456789012", true, true)]
    [InlineData("YT123456789012", "SF123456789012", true, false)]
    [InlineData("YT123456789012", "YT123456789012", false, false)]
    public void CandidatePolicy_CurrentRecordingCodeIsIgnoredOnlyWhileRecording(
        string value,
        string recordingOrderId,
        bool isRecording,
        bool expected)
    {
        Assert.Equal(
            expected,
            CameraBarcodeCandidatePolicy.IsCurrentRecordingCode(value, recordingOrderId, isRecording));
    }

    [Fact]
    public void Decoder_GuideRegionRecognizesCode128()
    {
        using Mat frame = CreateFrameWithBarcode("YT123456789012", BarcodeFormat.CODE_128, inGuide: true);
        var decoder = new CameraBarcodeFrameDecoder();

        Assert.Equal("YT123456789012", decoder.DecodeGuideRegion(frame));
    }

    [Fact]
    public void Decoder_GuideRegionCoversMostOfFrameWithoutBecomingFullFrame()
    {
        Rect guide = CameraBarcodeFrameDecoder.GetGuideRect(1280, 720);

        Assert.Equal(new Rect(64, 36, 1152, 648), guide);
        Assert.True(guide.Width < 1280);
        Assert.True(guide.Height < 720);
    }

    [Fact]
    public void Decoder_FullFrameFallbackFindsBarcodeOutsideGuide()
    {
        using Mat frame = CreateFrameWithBarcode(
            "SF123456789012",
            BarcodeFormat.CODE_128,
            inGuide: false,
            rotate90: true);
        var decoder = new CameraBarcodeFrameDecoder();

        Assert.Null(decoder.DecodeGuideRegion(frame));
        Assert.Equal("SF123456789012", decoder.DecodeFullFrame(frame));
    }

    [Fact]
    public void Decoder_GuideRegionRecognizesRotatedBarcode()
    {
        using Mat frame = CreateFrameWithBarcode("JD123456789012", BarcodeFormat.CODE_128, inGuide: true, rotate90: true);
        var decoder = new CameraBarcodeFrameDecoder();

        Assert.Equal("JD123456789012", decoder.DecodeGuideRegion(frame));
    }

    [Fact]
    public void Decoder_DoesNotAcceptEanProductBarcode()
    {
        using Mat frame = CreateFrameWithBarcode("6901234567892", BarcodeFormat.EAN_13, inGuide: true);
        var decoder = new CameraBarcodeFrameDecoder();

        Assert.Null(decoder.DecodeGuideRegion(frame));
        Assert.Null(decoder.DecodeFullFrame(frame));
    }

    [Fact]
    public void Decoder_DoesNotAcceptUpcProductBarcode()
    {
        using Mat frame = CreateFrameWithBarcode("012345678905", BarcodeFormat.UPC_A, inGuide: true);
        var decoder = new CameraBarcodeFrameDecoder();

        Assert.Null(decoder.DecodeGuideRegion(frame));
        Assert.Null(decoder.DecodeFullFrame(frame));
    }

    [Fact]
    public async Task RecognitionService_RecordingGateBlocksFullFrameFallback()
    {
        using Mat frame = CreateFrameWithBarcode(
            "ZT123456789012",
            BarcodeFormat.CODE_128,
            inGuide: false,
            rotate90: true);
        using var service = new CameraBarcodeRecognitionService(
            value => CameraBarcodeCandidatePolicy.IsValid(value, "^[a-zA-Z0-9-]{12,25}$"),
            fullFrameAllowed: () => false);
        int confirmedCount = 0;
        service.BarcodeConfirmed += _ => Interlocked.Increment(ref confirmedCount);

        service.TrySubmitFrame(frame, allowFullFrame: true);
        await Task.Delay(950, TestContext.Current.CancellationToken);
        service.TrySubmitFrame(frame, allowFullFrame: true);
        await Task.Delay(350, TestContext.Current.CancellationToken);

        Assert.Equal(0, Volatile.Read(ref confirmedCount));
    }

    [Fact]
    public void ExistingConfigWithoutCameraRecognitionFieldRemainsDisabled()
    {
        const string json = "{\"FirstUseWizardCompleted\":true,\"EnableGlobalKeyboard\":true}";

        AppConfig? config = JsonSerializer.Deserialize<AppConfig>(json);

        Assert.NotNull(config);
        Assert.False(config.EnableCameraBarcodeRecognition);
        Assert.Equal(0, config.CameraBarcodeSetupVersion);
        Assert.True(config.EnableGlobalKeyboard);
    }

    [Fact]
    public void FirstUseDefaultsEnableCameraRecognitionWithoutChangingScannerMode()
    {
        var config = new AppConfig
        {
            EnableGlobalKeyboard = false,
            EnableScannerAutoSubmit = true
        };

        AppConfig.ApplyFirstUseDefaults(config);

        Assert.True(config.FirstUseWizardCompleted);
        Assert.True(config.EnableCameraBarcodeRecognition);
        Assert.Equal(AppConfig.CurrentCameraBarcodeSetupVersion, config.CameraBarcodeSetupVersion);
        Assert.False(config.EnableGlobalKeyboard);
        Assert.True(config.EnableScannerAutoSubmit);
    }

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, false)]
    public void CameraBarcodeUpgradePrompt_OnlyShowsOnceForExistingUsers(
        bool firstUseCompleted,
        int setupVersion,
        bool expected)
    {
        var config = new AppConfig
        {
            FirstUseWizardCompleted = firstUseCompleted,
            CameraBarcodeSetupVersion = setupVersion
        };

        Assert.Equal(expected, AppConfig.ShouldPromptCameraBarcodeUpgrade(config));
    }

    [Fact]
    public void CameraBarcodeUpgradeChoice_EnablePreservesScannerSettings()
    {
        var config = new AppConfig
        {
            FirstUseWizardCompleted = true,
            EnableCameraBarcodeRecognition = false,
            EnableGlobalKeyboard = false,
            EnableScannerAutoSubmit = true
        };

        AppConfig.ApplyCameraBarcodeUpgradeChoice(config, enableRecognition: true);

        Assert.True(config.EnableCameraBarcodeRecognition);
        Assert.False(config.EnableGlobalKeyboard);
        Assert.True(config.EnableScannerAutoSubmit);
        Assert.Equal(AppConfig.CurrentCameraBarcodeSetupVersion, config.CameraBarcodeSetupVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CameraBarcodeUpgradeChoice_DeclinePreservesCurrentRecognitionAndScannerSettings(bool recognitionEnabled)
    {
        var config = new AppConfig
        {
            FirstUseWizardCompleted = true,
            EnableCameraBarcodeRecognition = recognitionEnabled,
            EnableGlobalKeyboard = true,
            EnableScannerAutoSubmit = false
        };

        AppConfig.ApplyCameraBarcodeUpgradeChoice(config, enableRecognition: false);

        Assert.Equal(recognitionEnabled, config.EnableCameraBarcodeRecognition);
        Assert.True(config.EnableGlobalKeyboard);
        Assert.False(config.EnableScannerAutoSubmit);
        Assert.Equal(AppConfig.CurrentCameraBarcodeSetupVersion, config.CameraBarcodeSetupVersion);
    }

    private static CameraBarcodeStabilityTracker Confirm(string trackingNumber)
    {
        var tracker = new CameraBarcodeStabilityTracker();
        tracker.Observe(trackingNumber, Start);
        tracker.Observe(trackingNumber, Start.AddMilliseconds(250));
        return tracker;
    }

    private static Mat CreateFrameWithBarcode(string value, BarcodeFormat format, bool inGuide, bool rotate90 = false)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = format,
            Options = new EncodingOptions
            {
                Width = rotate90 ? 280 : 520,
                Height = rotate90 ? 56 : 120,
                Margin = 16,
                PureBarcode = true
            }
        };
        var pixels = writer.Write(value);
        using Mat bgra = Mat.FromPixelData(pixels.Height, pixels.Width, MatType.CV_8UC4, pixels.Pixels).Clone();
        using Mat barcode = new();
        Cv2.CvtColor(bgra, barcode, ColorConversionCodes.BGRA2BGR);

        using Mat oriented = new();
        if (rotate90)
            Cv2.Rotate(barcode, oriented, RotateFlags.Rotate90Clockwise);
        else
            barcode.CopyTo(oriented);

        var frame = new Mat(new OpenCvSharp.Size(1280, 720), MatType.CV_8UC3, Scalar.White);
        int x = inGuide || !rotate90 ? (frame.Width - oriented.Width) / 2 : 0;
        int y = inGuide || rotate90 ? (frame.Height - oriented.Height) / 2 : 20;
        using Mat target = frame.SubMat(new Rect(x, y, oriented.Width, oriented.Height));
        oriented.CopyTo(target);
        return frame;
    }
}
