using OpenCvSharp;

namespace ExpressPackingMonitoring.Services.MediaFoundation;

/// <summary>
/// 色彩空间判断与 BT.601→BT.709 校正，新旧两条采集路径共用。
///
/// 判断优先级：
/// 1. 用户在设置里强制指定（bt601/bt709/off）—— 自动判断不准时的出口
/// 2. 设备在媒体类型里声明的色彩空间 —— Media Foundation 才拿得到
/// 3. 按分辨率推断（宽度 ≥ 1280 当作 BT.709）—— 只有这条是猜的
///
/// 第 3 条对"720p 的 BT.601 源"和"640x480 的 BT.709 源"都会判错，
/// 所以能读到设备声明时一定优先用它。旧的 AForge 路径拿不到声明，只能一直靠猜。
/// </summary>
internal static class CameraColorSpacePolicy
{
    /// <summary>720p 宽度。达到这个宽度即按高清惯例当作 BT.709。</summary>
    internal const int HdMinimumWidth = 1280;

    /// <summary>
    /// 在没有设备声明时按配置与分辨率推断是否用 BT.709。
    /// 与 <c>CameraColorMatrixPolicy.ShouldApply</c> 保持同一套判断，避免两条路径表现不一致。
    /// </summary>
    internal static bool InferBt709(string? mode, int frameWidth) =>
        ViewModels.CameraColorMatrixPolicy.ShouldApply(mode, frameWidth);

    /// <summary>
    /// 把 BT.601 解码的结果校正到 BT.709（就地修改）。
    ///
    /// 解码本身一律交给 OpenCV 的 SIMD 原语，校正也用 <c>Cv2.Transform</c>：
    /// 实测手写逐像素定点实现 3.9ms/帧，比这条路线的 2.1ms/帧更慢。
    /// </summary>
    internal static void ApplyBt709Correction(Mat frame) =>
        ViewModels.CameraColorMatrixPolicy.ApplyIfNeeded(
            frame,
            ViewModels.CameraColorMatrixPolicy.ModeBt709);
}
