using ExpressPackingMonitoring.Vision;
using OpenCvSharp;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 一帧摄像头画面的共享视觉处理入口：OBB 生产者始终可独立运行，
/// 条码解码消费者和智能跟随消费者只在各自开关需要时读取结果。
/// </summary>
internal sealed class CameraBarcodeVisionProcessor : IDisposable
{
    private readonly CameraBarcodeObbDecoder _decoder;
    private readonly Func<bool> _cameraBarcodeDecodingEnabledProvider;
    private readonly Func<bool> _visionObbDecodingEnabledProvider;
    private readonly object _gate = new();
    private CameraBarcodeGeometry? _lastDecodedGeometry;

    internal CameraBarcodeVisionProcessor(
        CameraBarcodeFrameDecoder standardDecoder,
        Func<(bool Enabled, string Path)>? visionObbProvider,
        Func<bool>? cameraBarcodeDecodingEnabledProvider,
        Func<bool>? visionObbDecodingEnabledProvider,
        IVisionObbDetectorFactory? detectorFactory = null)
    {
        _decoder = new CameraBarcodeObbDecoder(
            standardDecoder,
            visionObbProvider,
            visionObbDecodingEnabledProvider,
            detectorFactory);
        _cameraBarcodeDecodingEnabledProvider =
            cameraBarcodeDecodingEnabledProvider ?? (() => true);
        _visionObbDecodingEnabledProvider =
            visionObbDecodingEnabledProvider ?? (() => false);
    }

    internal IReadOnlyList<VisionObbDetection> ObbDetections =>
        _decoder.LastObbDetections;
    internal VisionObbDetection? SelectedObbDetection =>
        _decoder.LastSelectedObbDetection;
    internal bool VisionObbAvailable => _decoder.IsDetectorAvailable;
    internal CameraBarcodeGeometry? DecodedGeometry => _lastDecodedGeometry;

    internal string? ProcessFrame(
        Mat frame,
        Func<string, bool> isValid,
        CameraBarcodeGuideGeometry geometry,
        bool updateVision,
        bool decodeBarcodes)
    {
        lock (_gate)
        {
            _lastDecodedGeometry = null;
            bool enhancedVisionEnabled = ReadProvider(
                _visionObbDecodingEnabledProvider);
            if (updateVision)
            {
                if (enhancedVisionEnabled)
                {
                    Rect guide = CameraBarcodeGuideLayout.ToFrameRect(
                        frame.Width,
                        frame.Height,
                        geometry);
                    using var roi = new Mat(frame, guide);
                    _decoder.DetectOnly(roi, guide.X, guide.Y);
                }
                else
                {
                    _decoder.DetectOnly(frame);
                }
            }

            if (!decodeBarcodes
                || !ReadProvider(_cameraBarcodeDecodingEnabledProvider))
            {
                return null;
            }

            string? decoded;
            if (enhancedVisionEnabled)
            {
                decoded = _decoder.DecodeCachedDetections(
                    frame,
                    isValid,
                    geometry);
            }
            else
            {
                decoded = _decoder.DecodeStandardGuideRegion(frame, isValid, geometry);
            }

            if (!string.IsNullOrWhiteSpace(decoded))
            {
                _lastDecodedGeometry = _decoder.LastGeometry;
                return decoded;
            }

            return null;
        }
    }

    internal void ReleaseDetector()
    {
        lock (_gate)
            _decoder.ReleaseDetector();
    }

    private static bool ReadProvider(Func<bool> provider)
    {
        try
        {
            return provider();
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _decoder.Dispose();
    }
}
