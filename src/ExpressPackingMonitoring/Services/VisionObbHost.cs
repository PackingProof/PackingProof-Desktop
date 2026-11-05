using ExpressPackingMonitoring.Vision;
#if VISION_OBB
using ExpressPackingMonitoring.Vision.Obb;
#endif

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 可选 YOLO-OBB 实现的组合入口。主程序始终依赖通用契约；
/// 不含视觉功能包的构建会使用不可用实现，并继续走普通 ZXing 识别。
/// </summary>
internal static class VisionObbHost
{
    private static int _preferDirectMl = 1;
#if VISION_OBB
    private static readonly IVisionObbDetectorFactory Factory = VisionObbModule.Instance;
#else
    private static readonly IVisionObbDetectorFactory Factory = new UnavailableFactory();
#endif

    internal static bool IsAvailable => Factory.IsAvailable;

    internal static IVisionObbDetectorFactory DetectorFactory => Factory;

    internal static bool PreferDirectMl
    {
        get => Volatile.Read(ref _preferDirectMl) != 0;
        set => Volatile.Write(ref _preferDirectMl, value ? 1 : 0);
    }

    private sealed class UnavailableFactory : IVisionObbDetectorFactory
    {
        public bool IsAvailable => false;

        public IVisionObbDetector? TryLoad(
            string? modelPath,
            bool preferDirectMl = true) => null;

        public VisionModelMetadata? ReadModelMetadata(string modelPath) => null;
    }
}
