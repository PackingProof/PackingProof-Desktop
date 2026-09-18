using System.Diagnostics;
using ExpressPackingMonitoring.Logging;
using OpenCvSharp;

namespace ExpressPackingMonitoring.Services.Gpu;

/// <summary>由视频处理循环独占，尺寸稳定后复用 GPU 资源，失败时整段会话回退 CPU。</summary>
internal sealed class GpuPreviewResizer : IDisposable
{
    internal static InterpolationFlags ResolveInterpolation(int width, int height, int targetWidth, int targetHeight) =>
        (long)targetWidth * 3 > width && (long)targetHeight * 3 > height
            ? InterpolationFlags.Cubic : InterpolationFlags.Area;

    private GpuFrameConverter? _converter;
    private (int Width, int Height, int TargetWidth, int TargetHeight) _size;
    private long _sizeChangedAt;
    private bool _disabled;

    internal bool TryResize(Mat source, Mat destination, int width, int height)
    {
        if (_disabled || source.Type() != MatType.CV_8UC3)
            return false;

        var size = (source.Width, source.Height, width, height);
        if (_size != size)
        {
            _converter?.Dispose();
            _converter = null;
            _size = size;
            _sizeChangedAt = Stopwatch.GetTimestamp();
        }

        // 连续拖动窗口时先用现有 CPU 路径，避免每帧创建设备和编译着色器。
        if (_converter == null && Stopwatch.GetElapsedTime(_sizeChangedAt).TotalMilliseconds < 250)
            return false;

        _converter ??= GpuFrameConverter.TryCreate(source.Width, source.Height, width, height,
            isNv12: false, isBgr24: true);
        if (_converter != null)
        {
            destination.Create(height, width, MatType.CV_8UC3);
            if (_converter.TryRender(source.Data, checked((int)source.Step()), useBt709: false)
                && _converter.TryReadBackInto(destination))
                return true;
        }

        _disabled = true;
        _converter?.Dispose();
        _converter = null;
        RuntimeLog.Info("Camera", "GPU 预览缩放不可用，本次视频处理会话使用 CPU 缩放");
        return false;
    }

    public void Dispose()
    {
        _disabled = true;
        _converter?.Dispose();
        _converter = null;
    }
}
