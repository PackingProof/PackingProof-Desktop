using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace ExpressPackingMonitoring.UI;

public sealed class SettingsCapabilities
{
    private SettingsCapabilities(DeploymentCapabilities capabilities)
    {
        IsHost = capabilities.IsHost;
        IsRecordingDevice = capabilities.IsRecordingDevice;
        CanUseCamera = capabilities.CanUseCamera;
        CanRecordAudio = capabilities.CanRecordAudio;
        CanUseCameraBarcode = capabilities.CanUseCameraBarcode;
        CanUseScanner = capabilities.CanUseScanner;
        CanRecordPcVideo = capabilities.CanRecordPcVideo;
        CanConfigureStorage = capabilities.CanConfigureStorage;
        CanRunWebServer = capabilities.CanRunWebServer;
        CanReceiveMobileBackup = capabilities.CanReceiveMobileBackup;
        CanConnectHost = capabilities.CanConnectHost;
        CanManageRecordingDevices = capabilities.CanManageRecordingDevices;
        CanGenerateUserscript = capabilities.CanGenerateUserscript;
    }

    public bool IsHost { get; }
    public bool IsRecordingDevice { get; }
    public bool CanUseCamera { get; }
    public bool CanRecordAudio { get; }
    public bool CanUseCameraBarcode { get; }
    public bool CanUseScanner { get; }
    public bool CanRecordPcVideo { get; }
    public bool CanConfigureStorage { get; }
    public bool CanRunWebServer { get; }
    public bool CanReceiveMobileBackup { get; }
    public bool CanConnectHost { get; }
    public bool CanManageRecordingDevices { get; }
    public bool CanGenerateUserscript { get; }
    public bool CanConfigureRecordingCache =>
        IsRecordingDevice && CanConnectHost && !IsHost;

    public bool SupportsSpeechSettings => CanRecordPcVideo;
    public bool SupportsScannerSettings => CanUseScanner;
    public bool SupportsOrderVoiceSettings => IsRecordingDevice;
    public bool SupportsCameraMaintenance => CanUseCamera;
    public bool IsNoCameraWorkstation => !CanUseCamera;

    public static SettingsCapabilities ForPreset(string? preset) =>
        new(DeploymentCapabilities.ForPreset(preset));

    public static SettingsCapabilities ForRole(string? role) =>
        ForPreset(DeploymentPresets.FromLegacyRole(role));
}

public sealed class SettingsContext
{
    public required SettingsCapabilities Capabilities { get; init; }
    public required Func<AppConfig, Task<bool>> ApplyAsync { get; init; }
    public Func<string>? ConnectionAddressProvider { get; init; }
    public Action<Window>? ShowMobileConnection { get; init; }
    public Action? CopyMobileConnectionUrl { get; init; }
    public Action? OpenUserscriptGuide { get; init; }
    public Action? ImportUserscript { get; init; }
    public Func<IReadOnlyList<ExtensionAuthorizationDisplayItem>>? GetExtensionAuthorizations { get; init; }
    public Func<IReadOnlyList<OrderIntegrationDeviceDisplayItem>>? GetOrderIntegrationDevices { get; init; }
    public Func<string, ExtensionCredentialDisplayResult?>? RotateExtensionCredential { get; init; }
    public Func<string, bool>? RevokeExtensionAuthorization { get; init; }
    public Action<double?>? SetPreviewZoomScale { get; init; }
    public Action<CameraBarcodeGuideGeometry?>? SetPreviewGuideGeometry { get; init; }
    public Func<bool>? SuspendCameraForSetupWizard { get; init; }
    public Action? ResumeCameraAfterSetupWizard { get; init; }
    public Action<string, ToastSeverity>? ShowToast { get; init; }
    public Func<IProgress<string>, CancellationToken, Task<MkvBatchConversionResult>>? BatchConvertMkvToMp4Async { get; init; }
    public Func<ManualCleanupOptions, Task<ManualCleanupPreview>>? PreviewManualCleanupAsync { get; init; }
    public Func<ManualCleanupOptions, Func<ManualCleanupPrompt, bool>, Task<ManualCleanupResult>>? RunManualCleanupAsync { get; init; }
    public ICommand? ResetEncoderDetectCommand { get; init; }
    internal Func<AppConfig, IReadOnlyList<NativeCameraMode>, Task<RecordingProfileRecommendation?>>? DetectRecordingProfileAsync { get; init; }
    public object? ToastSource { get; init; }

    public static SettingsContext ForCameraWorkstation(MainViewModel mainViewModel)
    {
        ArgumentNullException.ThrowIfNull(mainViewModel);
        return new SettingsContext
        {
            Capabilities = SettingsCapabilities.ForPreset(mainViewModel.Config.DeploymentPreset),
            ApplyAsync = mainViewModel.ApplySettingsAsync,
            ConnectionAddressProvider = () => mainViewModel.MonitorAccessAddress,
            ShowMobileConnection = mainViewModel.ShowMobileConnection,
            CopyMobileConnectionUrl = mainViewModel.CopyMobileConnectionUrl,
            OpenUserscriptGuide = mainViewModel.OpenUserscriptGuide,
            ImportUserscript = mainViewModel.ImportUserscript,
            GetExtensionAuthorizations = mainViewModel.GetExtensionAuthorizations,
            GetOrderIntegrationDevices = mainViewModel.GetOrderIntegrationDevices,
            RotateExtensionCredential = mainViewModel.RotateExtensionCredential,
            RevokeExtensionAuthorization = mainViewModel.RevokeExtensionAuthorization,
            SetPreviewZoomScale = value => mainViewModel.PreviewZoomScale = value,
            SetPreviewGuideGeometry = value => mainViewModel.PreviewGuideGeometry = value,
            SuspendCameraForSetupWizard = mainViewModel.SuspendCameraForSetupWizard,
            ResumeCameraAfterSetupWizard = mainViewModel.ResumeCameraAfterSetupWizard,
            ShowToast = mainViewModel.ShowToast,
            BatchConvertMkvToMp4Async = (progress, token) =>
                mainViewModel.BatchConvertMkvToMp4Async(progress, token, forceRetry: true),
            PreviewManualCleanupAsync = mainViewModel.PreviewManualCleanupAsync,
            RunManualCleanupAsync = mainViewModel.RunManualCleanupAsync,
            ResetEncoderDetectCommand = mainViewModel.ResetEncoderDetectCommand,
            DetectRecordingProfileAsync = mainViewModel.DetectAndRecommendRecordingProfileAsync,
            ToastSource = mainViewModel
        };
    }
}

public sealed record ExtensionAuthorizationDisplayItem(
    string ExtensionInstanceId,
    string DisplayName,
    string Version,
    string Source,
    string PermissionsText,
    string BindingText,
    int CredentialGeneration,
    DateTimeOffset UpdatedAtUtc,
    bool Online,
    string ActivityText);

public sealed record OrderIntegrationDeviceDisplayItem(
    string NodeId,
    string DisplayName,
    string DeviceTypeText,
    bool Online,
    string ActivityText);

public sealed record ExtensionCredentialDisplayResult(
    string Credential,
    int CredentialGeneration);
