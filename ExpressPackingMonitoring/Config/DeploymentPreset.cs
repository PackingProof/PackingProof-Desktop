namespace ExpressPackingMonitoring.Config;

public static class DeploymentPresets
{
    public const int CurrentSchemaVersion = 1;
    public const string RecordingHost = "RecordingHost";
    public const string RecordingWorkstation = "RecordingWorkstation";
    public const string ViewerClient = "ViewerClient";
    public const string MobileBackupHost = "MobileBackupHost";

    public static bool IsKnown(string? preset) =>
        string.Equals(preset, RecordingHost, StringComparison.OrdinalIgnoreCase)
        || string.Equals(preset, RecordingWorkstation, StringComparison.OrdinalIgnoreCase)
        || string.Equals(preset, ViewerClient, StringComparison.OrdinalIgnoreCase)
        || string.Equals(preset, MobileBackupHost, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? preset)
    {
        if (string.Equals(preset, RecordingHost, StringComparison.OrdinalIgnoreCase))
            return RecordingHost;
        if (string.Equals(preset, RecordingWorkstation, StringComparison.OrdinalIgnoreCase))
            return RecordingWorkstation;
        if (string.Equals(preset, ViewerClient, StringComparison.OrdinalIgnoreCase))
            return ViewerClient;
        if (string.Equals(preset, MobileBackupHost, StringComparison.OrdinalIgnoreCase))
            return MobileBackupHost;
        return "";
    }

    public static string FromLegacyRole(string? role)
    {
        if (string.Equals(role, "CameraMonitor", StringComparison.OrdinalIgnoreCase))
            return RecordingHost;
        if (string.Equals(role, "PrintStation", StringComparison.OrdinalIgnoreCase))
            return MobileBackupHost;
        return "";
    }

    public static string GetDisplayName(string? preset) => Normalize(preset) switch
    {
        RecordingHost => "录像并保存在这台电脑",
        RecordingWorkstation => "录像并保存到其他主机",
        ViewerClient => "连接已有主机",
        MobileBackupHost => "接收手机录像",
        _ => "尚未配置"
    };
}

public sealed class DeploymentCapabilities
{
    private DeploymentCapabilities(
        bool isHost,
        bool isRecordingDevice,
        bool canUseCamera,
        bool canRecordAudio,
        bool canUseCameraBarcode,
        bool canUseScanner,
        bool canRecordPcVideo,
        bool canConfigureStorage,
        bool canRunWebServer,
        bool canReceiveMobileBackup,
        bool canConnectHost,
        bool canManageRecordingDevices,
        bool canGenerateUserscript)
    {
        IsHost = isHost;
        IsRecordingDevice = isRecordingDevice;
        CanUseCamera = canUseCamera;
        CanRecordAudio = canRecordAudio;
        CanUseCameraBarcode = canUseCameraBarcode;
        CanUseScanner = canUseScanner;
        CanRecordPcVideo = canRecordPcVideo;
        CanConfigureStorage = canConfigureStorage;
        CanRunWebServer = canRunWebServer;
        CanReceiveMobileBackup = canReceiveMobileBackup;
        CanConnectHost = canConnectHost;
        CanManageRecordingDevices = canManageRecordingDevices;
        CanGenerateUserscript = canGenerateUserscript;
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

    public static DeploymentCapabilities ForPreset(string? preset) =>
        DeploymentPresets.Normalize(preset) switch
        {
            DeploymentPresets.RecordingHost => new DeploymentCapabilities(
                isHost: true,
                isRecordingDevice: true,
                canUseCamera: true,
                canRecordAudio: true,
                canUseCameraBarcode: true,
                canUseScanner: true,
                canRecordPcVideo: true,
                canConfigureStorage: true,
                canRunWebServer: true,
                canReceiveMobileBackup: true,
                canConnectHost: false,
                canManageRecordingDevices: true,
                canGenerateUserscript: true),
            DeploymentPresets.RecordingWorkstation => new DeploymentCapabilities(
                isHost: false,
                isRecordingDevice: true,
                canUseCamera: true,
                canRecordAudio: true,
                canUseCameraBarcode: true,
                canUseScanner: true,
                canRecordPcVideo: true,
                canConfigureStorage: false,
                canRunWebServer: false,
                canReceiveMobileBackup: false,
                canConnectHost: true,
                canManageRecordingDevices: false,
                canGenerateUserscript: false),
            DeploymentPresets.ViewerClient => new DeploymentCapabilities(
                isHost: false,
                isRecordingDevice: false,
                canUseCamera: false,
                canRecordAudio: false,
                canUseCameraBarcode: false,
                canUseScanner: false,
                canRecordPcVideo: false,
                canConfigureStorage: false,
                canRunWebServer: false,
                canReceiveMobileBackup: false,
                canConnectHost: true,
                canManageRecordingDevices: false,
                canGenerateUserscript: true),
            DeploymentPresets.MobileBackupHost => new DeploymentCapabilities(
                isHost: true,
                isRecordingDevice: false,
                canUseCamera: false,
                canRecordAudio: false,
                canUseCameraBarcode: false,
                canUseScanner: false,
                canRecordPcVideo: false,
                canConfigureStorage: true,
                canRunWebServer: true,
                canReceiveMobileBackup: true,
                canConnectHost: false,
                canManageRecordingDevices: true,
                canGenerateUserscript: true),
            _ => new DeploymentCapabilities(
                isHost: false,
                isRecordingDevice: false,
                canUseCamera: false,
                canRecordAudio: false,
                canUseCameraBarcode: false,
                canUseScanner: false,
                canRecordPcVideo: false,
                canConfigureStorage: false,
                canRunWebServer: false,
                canReceiveMobileBackup: false,
                canConnectHost: false,
                canManageRecordingDevices: false,
                canGenerateUserscript: false)
        };
}

public static class PackingProofCapabilities
{
    public const string Host = "host";
    public const string WebPlayback = "web-playback";
    public const string PcRecording = "pc-recording";
    public const string Recording = "recording";
    public const string OrderReceiver = "order-receiver";
    public const string MobileBackup = "mobile-backup";
    public const string CameraBarcode = "camera-barcode";
    public const string Scanner = "scanner";
    public const string Microphone = "microphone";

    public static IReadOnlyList<string> ForPreset(string? preset) =>
        DeploymentPresets.Normalize(preset) switch
        {
            DeploymentPresets.RecordingHost =>
            [
                Host,
                WebPlayback,
                PcRecording,
                Recording,
                OrderReceiver,
                MobileBackup,
                CameraBarcode,
                Scanner,
                Microphone
            ],
            DeploymentPresets.MobileBackupHost =>
            [
                Host,
                WebPlayback,
                MobileBackup
            ],
            DeploymentPresets.RecordingWorkstation =>
            [
                PcRecording,
                Recording,
                OrderReceiver,
                CameraBarcode,
                Scanner,
                Microphone
            ],
            _ => Array.Empty<string>()
        };
}
