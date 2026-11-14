namespace ExpressPackingMonitoring.Services;

internal enum ScanInputSource
{
    ScannerGlobal,
    ScannerTextBox,
    CameraObb,
    CameraZxing,
    ManualInput
}

internal static class ScanInputSourceExtensions
{
    internal static bool IsCamera(this ScanInputSource source) =>
        source is ScanInputSource.CameraObb or ScanInputSource.CameraZxing;

    internal static string ToStorageValue(this ScanInputSource source) => source switch
    {
        ScanInputSource.ScannerGlobal => "scanner_global",
        ScanInputSource.ScannerTextBox => "scanner_textbox",
        ScanInputSource.CameraObb => "camera_obb",
        ScanInputSource.CameraZxing => "camera_zxing",
        ScanInputSource.ManualInput => "manual_input",
        _ => "unknown"
    };
}
