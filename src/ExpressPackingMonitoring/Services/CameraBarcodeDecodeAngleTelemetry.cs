using OpenCvSharp;
using ZXing;
using ZXing.Common;

namespace ExpressPackingMonitoring.Services;

internal static class CameraBarcodeDecodeAngleTelemetry
{
    internal static Result? Decode(
        BarcodeReaderGeneric fastReader,
        BarcodeReaderGeneric? slowReader,
        LuminanceSource luminance,
        int orientation,
        Size imageSize)
    {
        Result? result = fastReader.Decode(luminance);
        if (result != null)
        {
            Record(imageSize, orientation, slowReader: false);
            return result;
        }

        if (slowReader == null)
            return null;

        result = slowReader.Decode(luminance);
        if (result != null)
            Record(imageSize, orientation, slowReader: true);
        return result;
    }

    internal static void Record(
        Size imageSize,
        int orientation,
        bool slowReader,
        int attempts = 1) =>
        VisionObbTimingDiagnosticLog.AppendZxingSuccess(
            imageSize.Width,
            imageSize.Height,
            orientation,
            slowReader,
            attempts);
}
