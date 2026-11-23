using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.UpdateCore;

namespace ExpressPackingMonitoring.Services;

internal static class RuntimeCapabilityEnvironment
{
    internal static RuntimeEnvironment FromConfig(AppConfig? config)
    {
        if (config == null || !config.FirstUseWizardCompleted)
            return RuntimeEnvironment.FullyAvailable;

        bool cameraConfigured =
            !string.IsNullOrWhiteSpace(config.CameraMonikerString)
            || !string.IsNullOrWhiteSpace(config.NetworkCameraUrl);
        bool microphoneConfigured =
            config.EnableAudioRecording
            && !string.IsNullOrWhiteSpace(config.AudioDeviceName);
        bool scannerConfigured =
            config.EnableGlobalKeyboard || config.EnableScannerAutoSubmit;
        return new RuntimeEnvironment(
            CameraAvailable: cameraConfigured,
            MicrophoneAvailable: microphoneConfigured,
            ScannerAvailable: scannerConfigured,
            CameraBarcodeAvailable:
                cameraConfigured && config.EnableCameraBarcodeRecognition,
            VisionObbAvailable: VisionObbHost.IsAvailable);
    }
}
