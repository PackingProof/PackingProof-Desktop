namespace ExpressPackingMonitoring.Config;

/// <summary>
/// AppConfig 里"面单定向"相关字段的分部。
///
/// 为什么单独一个文件：<see cref="AppConfig"/> 已经贴着源码规模预算的边，
/// 继续往里加字段会直接把守卫测试顶穿。这两个字段和镜头那批一样是运行时可改的
/// 独立开关，挂在同一份配置上、跟着一起序列化，但代码上分开放。
/// </summary>
public partial class AppConfig
{
    /// <summary>
    /// 面单定向（可选）：用 YOLO-OBB 模型给出面单倾角，比全画面估计梯度方向更准。
    /// 模型文件和 ONNX Runtime 原生库都由使用方自备，缺失时自动退回纯图像处理的摆正方案。
    /// </summary>
    public bool EnableCameraBarcodeYoloDetection { get; set; } = false;

    /// <summary>YOLO-OBB 模型（.onnx）的完整路径；留空表示不启用。</summary>
    public string CameraBarcodeYoloModelPath { get; set; } = "";
}
