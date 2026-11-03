namespace ExpressPackingMonitoring.Services;

internal enum CameraBarcodeFrameIntent
{
    MotionGatedBarcode,
    ForcedBarcode,
    VisionWithMotionGatedBarcode,
    VisionWithForcedBarcode
}

internal static class CameraBarcodeFrameIntentPolicy
{
    internal static CameraBarcodeFrameIntent Resolve(
        bool forceBarcodeDecode,
        bool updateVision) =>
        (forceBarcodeDecode, updateVision) switch
        {
            (false, false) => CameraBarcodeFrameIntent.MotionGatedBarcode,
            (true, false) => CameraBarcodeFrameIntent.ForcedBarcode,
            (false, true) => CameraBarcodeFrameIntent.VisionWithMotionGatedBarcode,
            (true, true) => CameraBarcodeFrameIntent.VisionWithForcedBarcode
        };

    internal static bool ShouldForceDecode(CameraBarcodeFrameIntent intent) =>
        intent is CameraBarcodeFrameIntent.ForcedBarcode
            or CameraBarcodeFrameIntent.VisionWithForcedBarcode;

    internal static bool ShouldUpdateVision(CameraBarcodeFrameIntent intent) =>
        intent is CameraBarcodeFrameIntent.VisionWithMotionGatedBarcode
            or CameraBarcodeFrameIntent.VisionWithForcedBarcode;
}
