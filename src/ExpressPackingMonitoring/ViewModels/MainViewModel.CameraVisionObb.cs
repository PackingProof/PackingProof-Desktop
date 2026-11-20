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
    private bool _cameraVisionConfirmedByObb;
    private bool _cameraVisionSupportsBarcodeDetection;
    private bool _cameraVisionBenchmarkRunning;
    private bool _cameraVisionModelAvailable;
    private bool _cameraVisionStartupReady;
    private CancellationTokenSource? _cameraVisionBenchmarkCts;
    private Task _cameraVisionBenchmarkCompletion = Task.CompletedTask;
    private int _cameraVisionBenchmarkRerunRequested;

    public bool IsVisionObbAvailable => VisionObbHost.IsAvailable;

    /// <summary>最近一帧主摄像头视觉检测到的旋转框，坐标位于主摄输入帧。</summary>
    public IReadOnlyList<VisionObbDetection> CameraVisionObbDetections
    {
        get => _cameraVisionObbDetections;
        private set
        {
            if (SetProperty(ref _cameraVisionObbDetections, value))
                OnPropertyChanged(nameof(CameraVisionGuideUsesRecognitionColor));
        }
    }

    public VisionObbDetection? CameraVisionSelectedObbDetection
    {
        get => _cameraVisionSelectedObbDetection;
        private set => SetProperty(ref _cameraVisionSelectedObbDetection, value);
    }

    /// <summary>
    /// 没有 Waybill 检测时由取景框承担识别颜色；
    /// 有 Waybill 但最终是取景框兜底确认的，也由取景框显示命中。
    /// 正常的 OBB 命中只给对应面单框上色。
    /// </summary>
    public bool CameraVisionGuideUsesRecognitionColor =>
        VisionObbRuntimePolicy.ShouldGuideFrameShowRecognitionColor(
            CameraVisionObbDetections,
            IsCameraBarcodeConfirmed,
            _cameraVisionConfirmedByObb);

    public VisionObbDetection? CameraVisionSmartZoomTarget
    {
        get => _cameraVisionSmartZoomTarget;
        private set => SetProperty(ref _cameraVisionSmartZoomTarget, value);
    }

    public bool CameraVisionSupportsBarcodeDetection
    {
        get => _cameraVisionSupportsBarcodeDetection;
        private set => SetProperty(
            ref _cameraVisionSupportsBarcodeDetection,
            value);
    }

    internal bool ShouldRunVisionObb =>
        _cameraVisionStartupReady
        && !_cameraVisionBenchmarkRunning
        && IsVisionObbAvailable
        && VisionObbRuntimePolicy.ShouldRunMainBarcodeVision(
            Config?.EnableCameraBarcodeRecognition == true
                && Config?.EnableCameraBarcodeEnhancedRecognition == true,
            VisionObbRuntimePolicy.ShouldRunSmartZoom(
                Config?.EnableSmartZoom == true,
                Config?.EnableVisionObbSmartZoom == true),
            !ShouldUseOverlayChannelForBarcode);

    internal bool ShouldRunSeparateCameraSmartZoom =>
        _cameraVisionStartupReady
        && IsVisionObbAvailable
        && VisionObbRuntimePolicy.ShouldRunSeparateSmartZoom(
            VisionObbRuntimePolicy.ShouldRunSmartZoom(
                Config?.EnableSmartZoom == true,
                Config?.EnableVisionObbSmartZoom == true),
            Config?.EnableCameraBarcodeRecognition == true,
            ShouldUseOverlayChannelForBarcode);

    /// <summary>
    /// 主窗口完成首次显示后才允许提交 OBB 推理。模型会话加载和自动性能检测会占用
    /// CPU/GPU，不能抢在首屏渲染之前；普通 ZXing 扫码不受这个门控影响。
    /// </summary>
    internal void ActivateCameraVisionAfterStartup()
    {
        if (_cameraVisionStartupReady)
            return;

        _cameraVisionStartupReady = true;
        RuntimeLog.Info(
            "CameraVision",
            ShouldRunVisionObb || ShouldRunSeparateCameraSmartZoom
                ? "主界面已显示，视觉推理启动门控已就绪"
                : "主界面已显示，视觉推理保持关闭");
    }

    public bool IsCameraVisionGuideMarkerMode =>
        !_cameraVisionBenchmarkRunning
        && VisionObbRuntimePolicy.ShouldUseGuideMarkerMode(
            IsVisionObbAvailable,
            IsCameraBarcodeRecognitionEnabled,
            Config?.EnableCameraBarcodeEnhancedRecognition == true
                && !ShouldUseOverlayChannelForBarcode,
            _cameraVisionModelAvailable);

    internal async Task RunCameraVisionBenchmarkIfNeededAsync()
    {
        if (_isDisposed || !IsVisionObbAvailable)
            return;
        if (_cameraVisionBenchmarkRunning)
        {
            Interlocked.Exchange(ref _cameraVisionBenchmarkRerunRequested, 1);
            return;
        }
        if (IsRecording)
        {
            Interlocked.Exchange(ref _cameraVisionBenchmarkRerunRequested, 1);
            RuntimeLog.Info(
                "CameraVision",
                "自动性能检测已延后：当前正在录像");
            return;
        }
        if (!CameraVisionAutoEnablePolicy.ShouldRunBenchmark(
                Config.EnableCameraBarcodeRecognition,
                Config.CameraBarcodeRecognitionChannel))
        {
            RuntimeLog.Info(
                "CameraVision",
                Config.EnableCameraBarcodeRecognition
                    ? "自动性能检测已跳过：当前使用副摄像头识别面单"
                    : "自动性能检测已跳过：摄像头自动识别面单未启用");
            return;
        }

        _cameraVisionBenchmarkRunning = true;
        _cameraVisionBenchmarkCts = new CancellationTokenSource();
        CancellationToken benchmarkToken = _cameraVisionBenchmarkCts.Token;
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _cameraVisionBenchmarkCompletion = completion.Task;
        NotifyCameraVisionGuideStateChanged();
        try
        {
            Interlocked.Exchange(ref _cameraVisionBenchmarkRerunRequested, 0);
            string modelPath = AppPaths.ResolveCameraVisionModelPath();
            string modelFingerprint = await Task.Run(() =>
                VisionObbPerformanceBenchmark.GetModelIdentity(modelPath));
            if (_isDisposed)
                return;
            benchmarkToken.ThrowIfCancellationRequested();
            if (IsRecording)
            {
                Interlocked.Exchange(ref _cameraVisionBenchmarkRerunRequested, 1);
                return;
            }
            if (!CameraVisionAutoEnablePolicy.ShouldRunBenchmark(
                    Config.EnableCameraBarcodeRecognition,
                    Config.CameraBarcodeRecognitionChannel))
            {
                return;
            }
            (int benchmarkWidth, int benchmarkHeight, CameraBarcodeGuideGeometry benchmarkGuide) =
                ResolveCameraVisionBenchmarkFrame();
            string benchmarkInput = CameraVisionAutoEnablePolicy.BuildBenchmarkInput(
                modelFingerprint,
                benchmarkWidth,
                benchmarkHeight,
                benchmarkGuide,
                Config.CameraRotationDegrees);
            if (!VisionObbPerformanceBenchmark.ShouldRunForModel(
                    benchmarkInput,
                    Config.CameraVisionBenchmarkInput))
            {
                return;
            }

            CameraVisionBenchmarkCacheEntry? cached =
                Config.CameraVisionBenchmarkCache.FirstOrDefault(entry =>
                    entry.SchemaVersion
                        == AppConfig.CurrentCameraVisionBenchmarkCacheSchemaVersion
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
                        + $"provider={cached.Provider}, recommended={cached.Recommended}, "
                        + $"allScenarios={cached.AllScenariosCompleted}");
                return;
            }

            VisionObbBenchmarkResult directMlResult =
                await Task.Run(() => VisionObbPerformanceBenchmark.Run(
                    modelPath,
                    preferDirectMl: true,
                    frameWidth: benchmarkWidth,
                    frameHeight: benchmarkHeight,
                    cancellationToken: benchmarkToken,
                    guideGeometry: benchmarkGuide));
            if (_isDisposed)
                return;
            benchmarkToken.ThrowIfCancellationRequested();
            VisionObbBenchmarkResult result = directMlResult;
            if (VisionObbPerformanceBenchmark.ShouldCompareCpuProvider(
                    directMlResult))
            {
                VisionObbBenchmarkResult cpuResult =
                    await Task.Run(() => VisionObbPerformanceBenchmark.Run(
                        modelPath,
                        preferDirectMl: false,
                        frameWidth: benchmarkWidth,
                        frameHeight: benchmarkHeight,
                        cancellationToken: benchmarkToken,
                        guideGeometry: benchmarkGuide));
                if (_isDisposed)
                    return;
                benchmarkToken.ThrowIfCancellationRequested();
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
            {
                RuntimeLog.Warn(
                    "CameraVision",
                    $"增强识别模型不可用，已回退普通扫码：{modelPath}");
                return;
            }
            if (!CameraVisionAutoEnablePolicy.ShouldRunBenchmark(
                    Config.EnableCameraBarcodeRecognition,
                    Config.CameraBarcodeRecognitionChannel))
            {
                RuntimeLog.Info(
                    "CameraVision",
                    Config.EnableCameraBarcodeRecognition
                        ? "自动性能检测结果已丢弃：运行期间切换到副摄像头识别面单"
                        : "自动性能检测结果已丢弃：运行期间关闭了摄像头自动识别面单");
                return;
            }
            (int currentWidth, int currentHeight, CameraBarcodeGuideGeometry currentGuide) =
                ResolveCameraVisionBenchmarkFrame();
            string currentBenchmarkInput =
                CameraVisionAutoEnablePolicy.BuildBenchmarkInput(
                    modelFingerprint,
                    currentWidth,
                    currentHeight,
                    currentGuide,
                    Config.CameraRotationDegrees);
            if (!string.Equals(
                    currentBenchmarkInput,
                    benchmarkInput,
                    StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _cameraVisionBenchmarkRerunRequested, 1);
                RuntimeLog.Info(
                    "CameraVision",
                    "自动性能检测结果已丢弃：运行期间相机尺寸或识别框发生变化");
                return;
            }

            benchmarkToken.ThrowIfCancellationRequested();
            StoreCameraVisionBenchmarkCache(
                benchmarkInput,
                benchmarkWidth,
                benchmarkHeight,
                result.Provider,
                result.BaselineP95Ms,
                result.EnhancedP95Ms,
                result.AddedCpuMsPerFrame,
                result.Recommended,
                result.AllScenariosCompleted);
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
                $"本机性能检测完成：resolution={benchmarkWidth}x{benchmarkHeight}, "
                    + $"provider={result.Provider}, "
                    + $"noObbP95={result.BaselineP95Ms:F2}ms, "
                    + $"enhancedP95={result.EnhancedP95Ms:F2}ms, "
                    + $"addedCpu={result.AddedCpuMsPerFrame:F2}ms/frame, "
                    + $"recommended={result.Recommended}, "
                    + $"allScenarios={result.AllScenariosCompleted}, "
                    + $"enableSource={applied.Source}, enabled={applied.Enabled}");
        }
        catch (OperationCanceledException)
            when (_isDisposed || benchmarkToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("CameraVision", $"本机性能检测失败：{ex.Message}");
        }
        finally
        {
            _cameraVisionBenchmarkRunning = false;
            Interlocked.Exchange(ref _cameraVisionBenchmarkCts, null)?.Dispose();
            completion.TrySetResult();
            NotifyCameraVisionGuideStateChanged();
            TryRunDeferredCameraVisionBenchmark();
        }
    }

    private (int Width, int Height, CameraBarcodeGuideGeometry Guide)
        ResolveCameraVisionBenchmarkFrame()
    {
        int width = _actualCameraWidth > 0
            ? _actualCameraWidth
            : Config.FrameWidth;
        int height = _actualCameraHeight > 0
            ? _actualCameraHeight
            : Config.FrameHeight;
        return (
            width,
            height,
            ResolveMainCameraBarcodeGuideGeometry(Config, width, height));
    }

    private void CancelCameraVisionBenchmarkIfRunning(string reason)
    {
        Interlocked.Exchange(ref _cameraVisionBenchmarkRerunRequested, 0);
        if (!_cameraVisionBenchmarkRunning)
            return;

        RuntimeLog.Info("CameraVision", $"取消自动性能检测：{reason}");
        try
        {
            _cameraVisionBenchmarkCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void DeferCameraVisionBenchmarkForRecording()
    {
        if (!_cameraVisionBenchmarkRunning)
            return;

        Interlocked.Exchange(ref _cameraVisionBenchmarkRerunRequested, 1);
        RuntimeLog.Info("CameraVision", "自动性能检测已暂停：开始录像");
        try
        {
            _cameraVisionBenchmarkCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void TryRunDeferredCameraVisionBenchmark()
    {
        if (_isDisposed
            || _cameraVisionBenchmarkRunning
            || IsRecording
            || Interlocked.Exchange(ref _cameraVisionBenchmarkRerunRequested, 0) == 0)
        {
            return;
        }

        _ = RunCameraVisionBenchmarkIfNeededAsync();
    }

    private void NotifyCameraVisionFrameGeometryChanged()
    {
        if (_isDisposed
            || !_cameraVisionStartupReady
            || Config?.EnableCameraBarcodeRecognition != true)
        {
            return;
        }

        Interlocked.Exchange(ref _cameraVisionBenchmarkRerunRequested, 1);
        if (_cameraVisionBenchmarkRunning)
            return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            TryRunDeferredCameraVisionBenchmark();
        else
            dispatcher.BeginInvoke(new Action(TryRunDeferredCameraVisionBenchmark));
    }

    private void StopCameraVisionBenchmarkForShutdown()
    {
        Interlocked.Exchange(ref _cameraVisionBenchmarkRerunRequested, 0);
        try
        {
            _cameraVisionBenchmarkCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        try
        {
            if (!_cameraVisionBenchmarkCompletion.Wait(TimeSpan.FromSeconds(30)))
                RuntimeLog.Warn(
                    "CameraVision",
                    "本机性能检测在退出等待 30 秒后仍未结束，保留资源等待进程退出");
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(
                "CameraVision",
                $"等待本机性能检测停止失败：{ex.Message}");
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
        bool qrCodeEnabled = Config.EnableCameraBarcodeQrCode;
        bool blockedByQrCode = decision.Enabled && qrCodeEnabled;
        Config.EnableCameraBarcodeEnhancedRecognition =
            decision.Enabled && !qrCodeEnabled;
        if (Config.EnableCameraBarcodeEnhancedRecognition)
            Config.EnableCameraBarcodeQrCode = false;
        Config.CameraVisionEnableSource = blockedByQrCode
            ? AppConfig.CameraVisionEnableSourceManualOff
            : decision.Source;
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
                blockedByQrCode
                    ? AppLanguage.Get("本机性能检测完成，倾斜面单识别保持关闭")
                    : decision.Kind switch
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
        return blockedByQrCode
            ? decision with
            {
                Enabled = false,
                Source = AppConfig.CameraVisionEnableSourceManualOff
            }
            : decision;
    }

    private void StoreCameraVisionBenchmarkCache(
        string benchmarkInput,
        int frameWidth,
        int frameHeight,
        string provider,
        double baselineP95Ms,
        double enhancedP95Ms,
        double addedCpuMsPerFrame,
        bool recommended,
        bool allScenariosCompleted)
    {
        CameraVisionBenchmarkCacheEntry entry = new()
        {
            Input = benchmarkInput,
            FrameWidth = frameWidth,
            FrameHeight = frameHeight,
            Provider = provider,
            BaselineP95Ms = baselineP95Ms,
            EnhancedP95Ms = enhancedP95Ms,
            AddedCpuMsPerFrame = addedCpuMsPerFrame,
            Recommended = recommended,
            AllScenariosCompleted = allScenariosCompleted
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
        OnPropertyChanged(nameof(IsCameraVisionGuideMarkerMode));
        NotifyCameraVisionGuideStateChanged();
    }

    private void NotifyCameraVisionGuideStateChanged()
    {
        OnPropertyChanged(nameof(IsCameraVisionGuideMarkerMode));
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
