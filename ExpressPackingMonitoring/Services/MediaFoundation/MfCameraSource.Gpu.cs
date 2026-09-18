using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services.Gpu;
using OpenCvSharp;

namespace ExpressPackingMonitoring.Services.MediaFoundation;

/// <summary>
/// 采集路径上的 GPU 转换接入。
///
/// 现有链路有多遍全帧 CPU 遍历，1080p@60 空闲预览实测吃掉 2.15 个核心；
/// <see cref="GpuFrameConverter"/> 一次 draw 就能把 YUV 解码与 BT.709 校正做完。
/// 本机实测（1080p YUY2）GPU 渲染加整帧回读 1.02 ms/帧，CPU 单是解码 2.28 ms/帧。
///
/// 这里刻意保留"全分辨率 Mat"这一对外契约：录像要全分辨率，改契约的波及面太大，
/// 而实测表明即使付出整帧回读的代价，GPU 路径仍便宜一倍多。
///
/// 无 GPU 的机器是明确要支持的场景，所以每一步失败都干净回退 CPU，
/// 且只要失败过一次就永久停用 GPU —— 不能让采集线程每帧都去试一个坏设备。
/// </summary>
public sealed partial class MfCameraSource
{
    private GpuFrameConverter? _gpuConverter;
    private bool _gpuDisabled;
    private int _loggedGpuFallback;

    /// <summary>GPU 转换当前是否生效，供诊断与日志使用。</summary>
    internal bool UsesGpuConversion
    {
        get
        {
            lock (_sync)
                return _gpuConverter != null;
        }
    }

    /// <summary>最近一次 GPU 停用的原因；空表示没停用过。</summary>
    internal string GpuDisableReason { get; private set; } = "";

    /// <summary>
    /// 按协商到的格式准备 GPU 转换器。只有 YUY2 与 NV12 值得上 GPU：
    /// RGB24/RGB32 是系统已经转好的，再上传一趟显存反而更贵。
    /// </summary>
    private void SetUpGpuConversion(MfNativeFormat format)
    {
        _gpuConverter?.Dispose();
        _gpuConverter = null;

        if (_gpuDisabled)
            return;

        bool isNv12 = format.Subtype == MfInterop.MFVideoFormat_NV12;
        if (!isNv12 && format.Subtype != MfInterop.MFVideoFormat_YUY2)
        {
            GpuDisableReason = $"{format.SubtypeName} 已由系统转换，无需上 GPU";
            return;
        }

        // 目标尺寸等于源尺寸：对外仍交出全分辨率帧。
        _gpuConverter = GpuFrameConverter.TryCreate(
            format.Width,
            format.Height,
            format.Width,
            format.Height,
            isNv12);

        if (_gpuConverter == null)
        {
            // 没有 GPU 或创建失败都走到这里，属于受支持的正常情况。
            _gpuDisabled = true;
            GpuDisableReason = GpuFrameConverter.LastCreateFailure.Length > 0
                ? GpuFrameConverter.LastCreateFailure
                : "GPU 转换器创建失败";
            RuntimeLog.Info("Camera", $"GPU 转换不可用，使用 CPU 转换：{GpuDisableReason}");
            return;
        }

        RuntimeLog.Info(
            "Camera",
            $"GPU 转换已启用：{format.SubtypeName} {format.Width}x{format.Height}"
                + $"，特性级别 {_gpuConverter.FeatureLevel}");
    }

    /// <summary>
    /// 在 GPU 上完成解码与校正并把结果回读进 <paramref name="buffer"/>。
    ///
    /// 返回 false 表示本帧要走 CPU。任何一次失败都永久停用 GPU：
    /// 设备丢失、驱动重置之后重试只会让每一帧都多付一次失败开销。
    /// </summary>
    private bool TryConvertOnGpu(
        IntPtr scanline0,
        int pitch,
        Mat buffer,
        bool useBt709,
        Guid subtype)
    {
        GpuFrameConverter? converter;
        lock (_sync)
        {
            if (_gpuDisabled)
                return false;

            converter = _gpuConverter;
        }

        if (converter == null)
            return false;
        if (subtype != MfInterop.MFVideoFormat_YUY2 && subtype != MfInterop.MFVideoFormat_NV12)
            return false;

        if (converter.TryRender(scanline0, pitch, useBt709)
            && converter.TryReadBackInto(buffer))
        {
            return true;
        }

        DisableGpuConversion("GPU 转换帧失败");
        return false;
    }

    /// <summary>永久停用 GPU 转换并回到 CPU。只记一次日志，避免每帧刷屏。</summary>
    private void DisableGpuConversion(string reason)
    {
        lock (_sync)
        {
            _gpuDisabled = true;
            GpuDisableReason = reason;
            _gpuConverter?.Dispose();
            _gpuConverter = null;
        }

        if (Interlocked.Exchange(ref _loggedGpuFallback, 1) == 0)
            RuntimeLog.Warn("Camera", $"已回退到 CPU 转换（只提示一次）：{reason}");
    }
}
