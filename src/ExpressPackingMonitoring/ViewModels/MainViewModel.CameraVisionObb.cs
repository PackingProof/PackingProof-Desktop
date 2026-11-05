using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Vision;

namespace ExpressPackingMonitoring.ViewModels;

public partial class MainViewModel
{
    private const int CameraVisionBenchmarkCacheLimit = 12;

    private IReadOnlyList<VisionObbDetection> _cameraVisionObbDetections = [];
    private VisionObbDetection? _cameraVisionSelectedObbDetection;
    private VisionObbDetection? _cameraVisionSmartZoomTarget;
    private bool _cameraVisionBenchmarkRunning;
    private bool _cameraVisionModelAvailable;

    public bool IsVisionObbAvailable => VisionObbHost.IsAvailable;

    /// <summary>
    /// 自动选择增强识别时，性能和模型结论出来前不画传统取景框；检测失败回退普通识别后
    /// 也保持隐藏，避免先闪出默认框再切换。用户手动关闭增强识别时仍显示传统取景框。
    /// </summary>
    public bool IsCameraVisionGuideSuppressed =>
        IsVisionObbAvailable
        && IsCameraBarcodeRecognitionEnabled
        && !ShouldUseOverlayChannelForBarcode
        && !IsPreviewGuideEditing
        && (Config?.CameraVisionEnableSource == AppConfig.CameraVisionEnableSourceAuto
            || Config?.EnableCameraBarcodeEnhancedRecognition == true)
        && (_cameraVisionBenchmarkRunning
            || !_cameraVisionModelAvailable
            || Config?.EnableCameraBarcodeEnhancedRecognition != true);

    /// <summary>最近一帧主摄像头视觉检测到的旋转框，坐标位于主摄输入帧。</summary>
    public IReadOnlyList<VisionObbDetection> CameraVisionObbDetections
    {
        get => _cameraVisionObbDetections;
        private set => SetProperty(ref _cameraVisionObbDetections, value);
    }

    public VisionObbDetection? CameraVisionSelectedObbDetection
    {
        get => _cameraVisionSelectedObbDetection;
        private set => SetProperty(ref _cameraVisionSelectedObbDetection, value);
    }

    public VisionObbDetection? CameraVisionSmartZoomTarget
    {
        get => _cameraVisionSmartZoomTarget;
        private set => SetProperty(ref _cameraVisionSmartZoomTarget, value);
    }

    internal bool ShouldRunVisionObb =>
        IsVisionObbAvailable
        && VisionObbRuntimePolicy.ShouldRunMainBarcodeVision(
            Config?.EnableCameraBarcodeRecognition == true
                && Config?.EnableCameraBarcodeEnhancedRecognition == true,
            VisionObbRuntimePolicy.ShouldRunSmartZoom(
                Config?.EnableSmartZoom == true,
                Config?.EnableVisionObbSmartZoom == true),
            !ShouldUseOverlayChannelForBarcode);

    internal bool ShouldRunSeparateCameraSmartZoom =>
        IsVisionObbAvailable
        && VisionObbRuntimePolicy.ShouldRunSeparateSmartZoom(
            VisionObbRuntimePolicy.ShouldRunSmartZoom(
                Config?.EnableSmartZoom == true,
                Config?.EnableVisionObbSmartZoom == true),
            Config?.EnableCameraBarcodeRecognition == true,
            ShouldUseOverlayChannelForBarcode);

    public bool IsCameraVisionFullFrameMode =>
        VisionObbRuntimePolicy.ShouldUseFullFrameMode(
            IsVisionObbAvailable,
            IsCameraBarcodeRecognitionEnabled,
            Config?.EnableCameraBarcodeEnhancedRecognition == true
                && !ShouldUseOverlayChannelForBarcode,
            _cameraVisionModelAvailable);

    internal async Task RunCameraVisionBenchmarkIfNeededAsync()
    {
        if (_isDisposed || !IsVisionObbAvailable || _cameraVisionBenchmarkRunning)
            return;
        if (!CameraVisionAutoEnablePolicy.ShouldRunBenchmark(
                Config.CameraBarcodeRecognitionChannel))
        {
            RuntimeLog.Info(
                "CameraVision",
                "自动性能检测已跳过：当前使用副摄像头识别面单");
            return;
        }

        string modelPath = AppPaths.ResolveCameraVisionModelPath();
        string modelFingerprint = VisionObbPerformanceBenchmark.GetModelIdentity(modelPath);
        string benchmarkInput = CameraVisionAutoEnablePolicy.BuildBenchmarkInput(
            modelFingerprint,
            Config.FrameWidth,
            Config.FrameHeight);
        if (!VisionObbPerformanceBenchmark.ShouldRunForModel(
                benchmarkInput,
                Config.CameraVisionBenchmarkInput))
        {
            return;
        }

        CameraVisionBenchmarkCacheEntry? cached =
            Config.CameraVisionBenchmarkCache.FirstOrDefault(entry =>
                entry.SchemaVersion == 1
                && string.Equals(entry.Input, benchmarkInput, StringComparison.Ordinal));
        if (cached != null)
        {
            SetCameraVisionModelAvailable(true);
            CameraVisionEnableDecision? cachedDecision =
                ApplyCameraVisionBenchmarkOutcome(
                    benchmarkInput,
                    modelFingerprint,
                    cached.Provider,
                    cached.BaselineP95Ms,
                    cached.EnhancedP95Ms,
                    cached.AddedCpuMsPerFrame,
                    cached.Recommended,
                    showToast: false);
            if (cachedDecision == null)
                return;
            RuntimeLog.Info(
                "CameraVision",
                $"已复用本机性能检测缓存：{cached.FrameWidth}x{cached.FrameHeight}, "
                    + $"provider={cached.Provider}, recommended={cached.Recommended}");
            return;
        }

        _cameraVisionBenchmarkRunning = true;
        NotifyCameraVisionGuideStateChanged();
        try
        {
            VisionObbBenchmarkResult directMlResult =
                await Task.Run(() => VisionObbPerformanceBenchmark.Run(
                    modelPath,
                    preferDirectMl: true,
                    frameWidth: Config.FrameWidth,
                    frameHeight: Config.FrameHeight));
            if (_isDisposed)
                return;
            VisionObbBenchmarkResult result = directMlResult;
            if (directMlResult.ModelAvailable
                && string.Equals(
                    directMlResult.Provider,
                    "DirectML",
                    StringComparison.OrdinalIgnoreCase))
            {
                VisionObbBenchmarkResult cpuResult =
                    await Task.Run(() => VisionObbPerformanceBenchmark.Run(
                        modelPath,
                        preferDirectMl: false,
                        frameWidth: Config.FrameWidth,
                        frameHeight: Config.FrameHeight));
                if (_isDisposed)
                    return;
                result = VisionObbPerformanceBenchmark.SelectFasterProvider(
                    directMlResult,
                    cpuResult);
                RuntimeLog.Info(
                    "CameraVision",
                    $"CPU/GPU 性能比较：directMlP95={directMlResult.EnhancedP95Ms:F2}ms, "
                        + $"cpuP95={cpuResult.EnhancedP95Ms:F2}ms, "
                        + $"selected={result.Provider}");
            }
            SetCameraVisionModelAvailable(result.ModelAvailable);
            if (!result.ModelAvailable)
                return;
            if (!CameraVisionAutoEnablePolicy.ShouldRunBenchmark(
                    Config.CameraBarcodeRecognitionChannel))
            {
                RuntimeLog.Info(
                    "CameraVision",
                    "自动性能检测结果已丢弃：运行期间切换到副摄像头识别面单");
                return;
            }

            StoreCameraVisionBenchmarkCache(
                benchmarkInput,
                result.Provider,
                result.BaselineP95Ms,
                result.EnhancedP95Ms,
                result.AddedCpuMsPerFrame,
                result.Recommended);
            CameraVisionEnableDecision? decision = ApplyCameraVisionBenchmarkOutcome(
                benchmarkInput,
                modelFingerprint,
                result.Provider,
                result.BaselineP95Ms,
                result.EnhancedP95Ms,
                result.AddedCpuMsPerFrame,
                result.Recommended,
                showToast: true);
            if (decision == null)
                return;
            CameraVisionEnableDecision applied = decision.Value;
            RuntimeLog.Info(
                "CameraVision",
                $"本机性能检测完成：resolution={Config.FrameWidth}x{Config.FrameHeight}, "
                    + $"provider={result.Provider}, "
                    + $"baselineP95={result.BaselineP95Ms:F2}ms, "
                    + $"enhancedP95={result.EnhancedP95Ms:F2}ms, "
                    + $"addedCpu={result.AddedCpuMsPerFrame:F2}ms/frame, "
                    + $"recommended={result.Recommended}, "
                    + $"enableSource={applied.Source}, enabled={applied.Enabled}");
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("CameraVision", $"本机性能检测失败：{ex.Message}");
        }
        finally
        {
            _cameraVisionBenchmarkRunning = false;
            NotifyCameraVisionGuideStateChanged();
        }
    }

    private CameraVisionEnableDecision? ApplyCameraVisionBenchmarkOutcome(
        string benchmarkInput,
        string modelFingerprint,
        string provider,
        double baselineP95Ms,
        double enhancedP95Ms,
        double addedCpuMsPerFrame,
        bool recommended,
        bool showToast)
    {
        Config.CameraVisionBenchmarkInput = benchmarkInput;
        Config.CameraVisionBenchmarkProvider = provider;
        Config.CameraVisionBenchmarkBaselineP95Ms = baselineP95Ms;
        Config.CameraVisionBenchmarkEnhancedP95Ms = enhancedP95Ms;
        Config.CameraVisionBenchmarkAddedCpuMsPerFrame = addedCpuMsPerFrame;
        Config.CameraVisionBenchmarkRecommended = recommended;
        CameraVisionEnableDecision decision =
            CameraVisionAutoEnablePolicy.ResolveEnableDecision(
                Config.CameraVisionEnableSource,
                recommended);
        Config.EnableCameraBarcodeEnhancedRecognition = decision.Enabled;
        Config.CameraVisionEnableSource = decision.Source;
        VisionObbHost.PreferDirectMl = !string.Equals(
            provider,
            "CPU",
            StringComparison.OrdinalIgnoreCase);
        if (!SaveConfig(notifyUser: showToast))
            return null;

        ResetCameraBarcodeRecognition(forceReleaseVisionRuntime: true);
        NotifyCameraVisionGuideStateChanged();
        if (showToast)
        {
            ShowToast(
                decision.Kind switch
                {
                    CameraVisionEnableDecisionKind.AutoEnabled =>
                        AppLanguage.Get("本机性能检测通过，已自动开启倾斜面单识别"),
                    CameraVisionEnableDecisionKind.AutoDisabled =>
                        AppLanguage.Get("本机性能检测未通过，已自动关闭倾斜面单识别"),
                    CameraVisionEnableDecisionKind.ManualOnRetained =>
                        AppLanguage.Get("本机性能检测完成，已保持你的手动开启设置"),
                    _ => AppLanguage.Get("本机性能检测完成，已保持你的手动关闭设置")
                },
                ToastSeverity.Information);
        }
        return decision;
    }

    private void StoreCameraVisionBenchmarkCache(
        string benchmarkInput,
        string provider,
        double baselineP95Ms,
        double enhancedP95Ms,
        double addedCpuMsPerFrame,
        bool recommended)
    {
        CameraVisionBenchmarkCacheEntry entry = new()
        {
            Input = benchmarkInput,
            FrameWidth = Config.FrameWidth,
            FrameHeight = Config.FrameHeight,
            Provider = provider,
            BaselineP95Ms = baselineP95Ms,
            EnhancedP95Ms = enhancedP95Ms,
            AddedCpuMsPerFrame = addedCpuMsPerFrame,
            Recommended = recommended
        };
        int existingIndex = Config.CameraVisionBenchmarkCache.FindIndex(candidate =>
            string.Equals(candidate.Input, benchmarkInput, StringComparison.Ordinal));
        if (existingIndex >= 0)
            Config.CameraVisionBenchmarkCache[existingIndex] = entry;
        else
            Config.CameraVisionBenchmarkCache.Add(entry);
        if (Config.CameraVisionBenchmarkCache.Count > CameraVisionBenchmarkCacheLimit)
        {
            Config.CameraVisionBenchmarkCache.RemoveRange(
                0,
                Config.CameraVisionBenchmarkCache.Count - CameraVisionBenchmarkCacheLimit);
        }
    }

    private void SetCameraVisionModelAvailable(bool available)
    {
        if (_cameraVisionModelAvailable == available)
            return;
        _cameraVisionModelAvailable = available;
        OnPropertyChanged(nameof(IsCameraVisionFullFrameMode));
        NotifyCameraVisionGuideStateChanged();
    }

    private void NotifyCameraVisionGuideStateChanged()
    {
        OnPropertyChanged(nameof(IsCameraVisionGuideSuppressed));
    }

    private void UpdateCameraVisionSmartZoomTarget(
        VisionObbDetection? decodedSelection = null)
    {
        bool smartZoomEnabled = Config is
        {
            EnableSmartZoom: true,
            EnableVisionObbSmartZoom: true
        };
        CameraVisionSmartZoomTarget = smartZoomEnabled
            ? VisionSmartZoomPolicy.SelectTarget(
                CameraVisionObbDetections,
                decodedSelection,
                _actualCameraWidth,
                _actualCameraHeight,
                CameraVisionSmartZoomTarget)
            : null;
        if (smartZoomEnabled)
            CameraVisionSelectedObbDetection = CameraVisionSmartZoomTarget;
    }
}
