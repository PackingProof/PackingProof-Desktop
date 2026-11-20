using ExpressPackingMonitoring.Config;
using System.Globalization;

namespace ExpressPackingMonitoring.Services;

internal enum CameraVisionEnableDecisionKind
{
    AutoEnabled,
    AutoDisabled,
    ManualOnRetained,
    ManualOffRetained
}

internal readonly record struct CameraVisionEnableDecision(
    bool Enabled,
    string Source,
    CameraVisionEnableDecisionKind Kind);

internal static class CameraVisionAutoEnablePolicy
{
    internal static bool ShouldRunBenchmark(
        bool barcodeRecognitionEnabled,
        int barcodeRecognitionChannel) =>
        barcodeRecognitionEnabled && barcodeRecognitionChannel == 0;

    internal static string BuildBenchmarkInput(
        string? modelFingerprint,
        int frameWidth,
        int frameHeight,
        CameraBarcodeGuideGeometry guideGeometry,
        int rotationDegrees) =>
        string.IsNullOrWhiteSpace(modelFingerprint)
            || frameWidth <= 0
            || frameHeight <= 0
            ? ""
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{modelFingerprint}:{AppConfig.CurrentCameraVisionPolicyVersion}:"
                    + $"{frameWidth}x{frameHeight}:"
                    + $"g{guideGeometry.WidthRatio:F3},{guideGeometry.HeightRatio:F3},"
                    + $"{guideGeometry.OffsetX:F3},{guideGeometry.OffsetY:F3}:"
                    + $"r{CameraFrameOrientation.NormalizeDegrees(rotationDegrees)}");

    internal static CameraVisionEnableDecision ResolveEnableDecision(
        string? configuredSource,
        bool benchmarkRecommended)
    {
        string source = AppConfig.NormalizeCameraVisionEnableSource(configuredSource);
        return source switch
        {
            AppConfig.CameraVisionEnableSourceManualOn =>
                new CameraVisionEnableDecision(
                    true,
                    source,
                    CameraVisionEnableDecisionKind.ManualOnRetained),
            AppConfig.CameraVisionEnableSourceManualOff =>
                new CameraVisionEnableDecision(
                    false,
                    source,
                    CameraVisionEnableDecisionKind.ManualOffRetained),
            _ when benchmarkRecommended =>
                new CameraVisionEnableDecision(
                    true,
                    AppConfig.CameraVisionEnableSourceAuto,
                    CameraVisionEnableDecisionKind.AutoEnabled),
            _ => new CameraVisionEnableDecision(
                false,
                AppConfig.CameraVisionEnableSourceAuto,
                CameraVisionEnableDecisionKind.AutoDisabled)
        };
    }
}
