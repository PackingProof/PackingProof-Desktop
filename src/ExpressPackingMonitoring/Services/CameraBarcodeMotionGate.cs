using OpenCvSharp;

namespace ExpressPackingMonitoring.Services;

internal sealed class CameraBarcodeMotionGate : IDisposable
{
    internal static readonly TimeSpan DecodeHoldDuration = TimeSpan.FromSeconds(1);
    internal const int SampleWidth = 160;
    internal const int SampleHeight = 90;
    internal const double MeanDifferenceThreshold = 6.0;
    internal const double ChangedPixelRatioThreshold = 0.01;
    internal const double PixelDifferenceThreshold = 18;

    private static readonly Size SampleSize = new(SampleWidth, SampleHeight);
    private readonly Mat _sampled = new();
    private readonly Mat _currentGray = new();
    private readonly Mat _previousGray = new();
    private readonly Mat _difference = new();
    private readonly Mat _changedPixels = new();
    private bool _hasBaseline;
    private DateTimeOffset _decodeUntil;
    private bool _disposed;

    public bool ShouldDecode(Mat frame, DateTimeOffset now, bool forceDecode = false)
    {
        if (_disposed || frame == null || frame.IsDisposed || frame.Empty())
            return false;

        switch (frame.Channels())
        {
            case 1:
                Cv2.Resize(frame, _currentGray, SampleSize, interpolation: InterpolationFlags.Area);
                break;
            case 3:
                Cv2.Resize(frame, _sampled, SampleSize, interpolation: InterpolationFlags.Area);
                Cv2.CvtColor(_sampled, _currentGray, ColorConversionCodes.BGR2GRAY);
                break;
            case 4:
                Cv2.Resize(frame, _sampled, SampleSize, interpolation: InterpolationFlags.Area);
                Cv2.CvtColor(_sampled, _currentGray, ColorConversionCodes.BGRA2GRAY);
                break;
            default:
                return false;
        }

        if (!_hasBaseline)
        {
            _currentGray.CopyTo(_previousGray);
            _hasBaseline = true;
            _decodeUntil = now + DecodeHoldDuration;
            return true;
        }

        Cv2.Absdiff(_currentGray, _previousGray, _difference);
        double meanDifference = Cv2.Mean(_difference).Val0;
        Cv2.Threshold(
            _difference,
            _changedPixels,
            PixelDifferenceThreshold,
            255,
            ThresholdTypes.Binary);
        double changedPixelRatio = (double)Cv2.CountNonZero(_changedPixels) / (SampleWidth * SampleHeight);
        _currentGray.CopyTo(_previousGray);

        bool changed = meanDifference >= MeanDifferenceThreshold
            || changedPixelRatio >= ChangedPixelRatioThreshold;
        if (changed)
            _decodeUntil = now + DecodeHoldDuration;

        return forceDecode || now <= _decodeUntil;
    }

    public void Reset()
    {
        if (_disposed)
            return;

        _hasBaseline = false;
        _decodeUntil = default;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _sampled.Dispose();
        _currentGray.Dispose();
        _previousGray.Dispose();
        _difference.Dispose();
        _changedPixels.Dispose();
    }
}
