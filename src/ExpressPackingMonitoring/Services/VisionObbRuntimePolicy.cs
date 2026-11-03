namespace ExpressPackingMonitoring.Services;

internal static class VisionObbRuntimePolicy
{
    internal static bool ShouldRunSmartZoom(
        bool smartZoomMasterEnabled,
        bool smartZoomEnabled) =>
        smartZoomMasterEnabled && smartZoomEnabled;

    internal static bool ShouldRunMainBarcodeVision(
        bool enhancedRecognitionEnabled,
        bool mainSmartZoomEnabled,
        bool useMainBarcodeSource) =>
        useMainBarcodeSource
        && ShouldRun(enhancedRecognitionEnabled, mainSmartZoomEnabled);

    internal static bool ShouldRunSeparateSmartZoom(
        bool mainSmartZoomEnabled,
        bool barcodeRecognitionEnabled,
        bool useOverlayBarcodeSource) =>
        mainSmartZoomEnabled
        && barcodeRecognitionEnabled
        && useOverlayBarcodeSource;

    internal static bool ShouldUseFullFrameMode(
        bool componentAvailable,
        bool recognitionEnabled,
        bool enhancedRecognitionEnabled,
        bool modelAvailable) =>
        componentAvailable
        && recognitionEnabled
        && enhancedRecognitionEnabled
        && modelAvailable;

    internal static bool ShouldRun(
        bool enhancedBarcodeRecognition,
        bool smartZoom) =>
        enhancedBarcodeRecognition || smartZoom;
}
