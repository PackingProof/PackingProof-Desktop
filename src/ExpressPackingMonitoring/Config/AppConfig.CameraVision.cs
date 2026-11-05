namespace ExpressPackingMonitoring.Config;

public partial class AppConfig
{
    public const int CurrentCameraVisionSchemaVersion = 1;

    /// <summary>
    /// 视觉行为版本。修改模型预处理、张量契约、OBB/ZXing 解码策略、benchmark
    /// 场景或性能门槛时必须递增，使所有已缓存的本机检测结果失效并重新 benchmark。
    /// </summary>
    public const int CurrentCameraVisionPolicyVersion = 2;

    public const string CameraVisionEnableSourceAuto = "Auto";
    public const string CameraVisionEnableSourceManualOn = "ManualOn";
    public const string CameraVisionEnableSourceManualOff = "ManualOff";

    /// <summary>
    /// 增强面单识别：使用随项目训练的 YOLO-OBB 模型定位倾斜面单。
    /// 发布包使用稳定的 vision-obb.onnx 角色文件，首次启动由 benchmark 决定是否开启。
    /// </summary>
    public bool EnableCameraBarcodeEnhancedRecognition { get; set; } = false;
    public int CameraVisionSchemaVersion { get; set; }
    public int CameraVisionPolicyVersion { get; set; }
    public string CameraVisionBenchmarkInput { get; set; } = "";
    public string CameraVisionEnableSource { get; set; } =
        CameraVisionEnableSourceAuto;
    public List<CameraVisionBenchmarkCacheEntry> CameraVisionBenchmarkCache { get; set; } = [];

    public string CameraVisionBenchmarkProvider { get; set; } = "";
    public double CameraVisionBenchmarkBaselineP95Ms { get; set; }
    public double CameraVisionBenchmarkEnhancedP95Ms { get; set; }
    public double CameraVisionBenchmarkAddedCpuMsPerFrame { get; set; }
    public bool CameraVisionBenchmarkRecommended { get; set; }

    internal static string NormalizeCameraVisionEnableSource(string? source) =>
        source switch
        {
            CameraVisionEnableSourceManualOn => CameraVisionEnableSourceManualOn,
            CameraVisionEnableSourceManualOff => CameraVisionEnableSourceManualOff,
            _ => CameraVisionEnableSourceAuto
        };

    internal static void MarkCameraVisionUserChoice(AppConfig config, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.EnableCameraBarcodeEnhancedRecognition = enabled;
        config.CameraVisionEnableSource = enabled
            ? CameraVisionEnableSourceManualOn
            : CameraVisionEnableSourceManualOff;
    }

    internal static bool NormalizeCameraVisionSettings(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        bool changed = false;
        string source = NormalizeCameraVisionEnableSource(
            config.CameraVisionEnableSource);

        if (config.CameraVisionSchemaVersion < CurrentCameraVisionSchemaVersion)
        {
            // 首个正式版本之前没有已发布配置，不需要猜测旧 bool 的来源。
            source = CameraVisionEnableSourceAuto;
            config.EnableCameraBarcodeEnhancedRecognition = false;
            config.CameraVisionBenchmarkInput = "";
            config.CameraVisionBenchmarkProvider = "";
            config.CameraVisionBenchmarkBaselineP95Ms = 0;
            config.CameraVisionBenchmarkEnhancedP95Ms = 0;
            config.CameraVisionBenchmarkAddedCpuMsPerFrame = 0;
            config.CameraVisionBenchmarkRecommended = false;
            config.CameraVisionBenchmarkCache = [];
            config.CameraVisionSchemaVersion = CurrentCameraVisionSchemaVersion;
            changed = true;
        }

        if (config.CameraVisionPolicyVersion != CurrentCameraVisionPolicyVersion)
        {
            config.CameraVisionPolicyVersion = CurrentCameraVisionPolicyVersion;
            changed = true;
        }

        if (!string.Equals(
                config.CameraVisionEnableSource,
                source,
                StringComparison.Ordinal))
        {
            config.CameraVisionEnableSource = source;
            changed = true;
        }

        bool? sourceEnabled = source switch
        {
            CameraVisionEnableSourceManualOn => true,
            CameraVisionEnableSourceManualOff => false,
            _ => null
        };
        if (sourceEnabled is { } enabled
            && config.EnableCameraBarcodeEnhancedRecognition != enabled)
        {
            config.EnableCameraBarcodeEnhancedRecognition = enabled;
            changed = true;
        }

        if (config.CameraVisionBenchmarkCache == null)
        {
            config.CameraVisionBenchmarkCache = [];
            changed = true;
        }

        return changed;
    }
}

public sealed class CameraVisionBenchmarkCacheEntry
{
    public int SchemaVersion { get; set; } = 1;
    public string Input { get; set; } = "";
    public int FrameWidth { get; set; }
    public int FrameHeight { get; set; }
    public string Provider { get; set; } = "";
    public double BaselineP95Ms { get; set; }
    public double EnhancedP95Ms { get; set; }
    public double AddedCpuMsPerFrame { get; set; }
    public bool Recommended { get; set; }
}
