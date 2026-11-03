using ExpressPackingMonitoring.Config;

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
    internal static bool ShouldRunBenchmark(int barcodeRecognitionChannel) =>
        barcodeRecognitionChannel == 0;

    internal static string BuildBenchmarkInput(
        string? modelFingerprint,
        int frameWidth,
        int frameHeight) =>
        string.IsNullOrWhiteSpace(modelFingerprint)
            || frameWidth <= 0
            || frameHeight <= 0
            ? ""
            : $"{modelFingerprint}:{AppConfig.CurrentCameraVisionPolicyVersion}:{frameWidth}x{frameHeight}";

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
