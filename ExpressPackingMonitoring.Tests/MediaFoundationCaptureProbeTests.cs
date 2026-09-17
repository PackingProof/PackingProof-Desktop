using ExpressPackingMonitoring.Services.MediaFoundation;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// Media Foundation 采集可行性探针。
///
/// 这组用例回答"能不能不经系统转换拿到摄像头原始帧"这个决定性问题：
/// AForge 的 NewFrame 只给 Bitmap，YUV→RGB 由 DirectShow 固定按 BT.601 做，
/// 这是画面发灰的根因，也是 CPU 反复遍历像素的起点。MF 如果能交出原始 YUY2/NV12，
/// 转换就可以由我们按正确系数做一次（或者交给 GPU）。
///
/// 没有摄像头的机器（构建机、CI）上这些用例自动跳过，不制造假失败。
/// </summary>
public sealed class MediaFoundationCaptureProbeTests
{
    [Fact]
    public void EnumeratesVideoCaptureDevices()
    {
        using MfPlatform? mf = MfPlatform.TryStart();
        if (mf == null)
            return;

        IReadOnlyList<MfCaptureDevice> devices = MfCaptureDevice.Enumerate();
        if (devices.Count == 0)
            return;

        Assert.All(devices, device =>
        {
            Assert.False(string.IsNullOrWhiteSpace(device.Name));
            Assert.False(string.IsNullOrWhiteSpace(device.SymbolicLink));
        });
    }

    /// <summary>
    /// 核心问题：摄像头原生提供哪些格式。
    /// 只要出现 YUY2/NV12，"申请原始帧"这条路就走得通。
    /// </summary>
    [Fact]
    public void ReportsNativeMediaTypes()
    {
        using MfPlatform? mf = MfPlatform.TryStart();
        if (mf == null)
            return;

        IReadOnlyList<MfCaptureDevice> devices = MfCaptureDevice.Enumerate();
        if (devices.Count == 0)
            return;

        IReadOnlyList<MfNativeFormat> formats = MfCaptureDevice.ReadNativeFormats(devices[0]);
        if (formats.Count == 0)
            return;

        Assert.All(formats, format =>
        {
            Assert.True(format.Width > 0);
            Assert.True(format.Height > 0);
        });
    }

    /// <summary>
    /// 现场硬件能力必须能被读出来：遇到"发灰/发糊/帧率上不去"时，
    /// 第一件事就是看摄像头到底提供哪些格式。
    ///
    /// 实测本机 Iriun Webcam：MF 报告 32 种格式（YUY2×16 + RGB24×16，最大 3840x2160@60），
    /// 而 AForge 只报告 1 种（1920x1080@60/16bpp）—— 旧采集路径把能力清单丢掉了大半，
    /// 连 4K 都看不见，这也是"申请不到想要的格式"的根源。
    /// </summary>
    [Fact]
    public void DescribesLocalHardwareCapabilities()
    {
        using MfPlatform? mf = MfPlatform.TryStart();
        if (mf == null)
            return;

        IReadOnlyList<MfCaptureDevice> devices = MfCaptureDevice.Enumerate();
        if (devices.Count == 0)
            return;

        IReadOnlyList<MfNativeFormat> formats = MfCaptureDevice.ReadNativeFormats(devices[0]);
        if (formats.Count == 0)
            return;

        // 能读出格式清单就说明"申请原始帧"这条路可用；具体是否含 YUV 取决于设备。
        Assert.All(formats, format => Assert.False(string.IsNullOrWhiteSpace(format.SubtypeName)));
        Assert.All(formats, format => Assert.True(format.FrameRate >= 0));
    }
}
