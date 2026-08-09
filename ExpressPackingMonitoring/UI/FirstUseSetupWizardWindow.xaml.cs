#nullable disable
using ExpressPackingMonitoring.Config;
using AForge.Video;
using AForge.Video.DirectShow;
using ExpressPackingMonitoring.ViewModels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Localization;
using Mat = OpenCvSharp.Mat;
using MatType = OpenCvSharp.MatType;

namespace ExpressPackingMonitoring.UI;

public partial class FirstUseSetupWizardWindow : Window
{
    private readonly AppConfig _config;
    private readonly List<TextBlock> _stepTexts;
    private int _stepIndex;
    private bool _isLoadingDevices;
    private VideoCaptureDevice _previewCamera;
    private NetworkCameraSource _networkPreviewSource;
    private Task _previewCameraForceStopTask;
    private DateTime _lastPreviewUpdateAt = DateTime.MinValue;
    private WasapiCapture _micCapture;
    private readonly string _testBarcodeValue = $"TEST{DateTime.Now:yyyyMMddHHmmss}";
    private bool _scannerDetectedEnter;
    private readonly CameraBarcodeRecognitionService _cameraBarcodeRecognition;
    private string _evaluatedCameraMoniker = "";
    private bool _isRecordingProfileDetectionRunning;
    private bool _isRecognitionPreview;

    public bool WasSkipped { get; private set; }
    public AppConfig ResultConfig => _config;

    public FirstUseSetupWizardWindow(AppConfig config, bool allowSkip = true)
    {
        InitializeComponent();
        _config = config;
        SkipButton.Visibility = allowSkip ? Visibility.Visible : Visibility.Collapsed;
        _cameraBarcodeRecognition = new CameraBarcodeRecognitionService(
            IsCameraBarcodeCandidate,
            _ => TimeSpan.FromSeconds(_config.CameraSameBarcodeConfirmationSeconds),
            reportVisibleCodes: true,
            guideIntervalProvider: () => CameraBarcodeSpeed.GuideIntervalFor(
                _config.CameraBarcodeRecognitionSpeed,
                _config.Fps),
            guideGeometryProvider: () => new CameraBarcodeGuideGeometry(
                _config.CameraBarcodeGuideWidthRatio,
                _config.CameraBarcodeGuideHeightRatio,
                _config.CameraBarcodeGuideOffsetX,
                _config.CameraBarcodeGuideOffsetY),
            confirmationHitsProvider: () => _config.CameraSameBarcodeConfirmationHits);
        _cameraBarcodeRecognition.StatusChanged += CameraBarcodeRecognition_StatusChanged;
        _stepTexts = new List<TextBlock>
        {
            StepModeText,
            StepCameraText,
            StepCameraRecognitionText,
            StepRecordingProfileText,
            StepMicText,
            StepScannerText,
            StepDoneText
        };

        ContinuousModeRadio.IsChecked = !_config.EnableSameBarcodeStopRecording;
        SameCodeModeRadio.IsChecked = _config.EnableSameBarcodeStopRecording;
        bool useCameraRecognition =
            GetInitialCameraRecognitionChoice(_config);
        UseCameraRecognitionRadio.IsChecked = useCameraRecognition;
        DisableCameraRecognitionRadio.IsChecked = !useCameraRecognition;
        RenderTestBarcode();

        Loaded += FirstUseSetupWizardWindow_Loaded;
        Closed += FirstUseSetupWizardWindow_Closed;
        CameraRecognitionPreviewImage.SizeChanged += (_, __) => UpdateCameraRecognitionGuide();
        ShowStep(0);
    }

    internal static bool GetInitialCameraRecognitionChoice(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return !config.FirstUseWizardCompleted
            || config.EnableCameraBarcodeRecognition;
    }

    internal static bool TryConfigureRecordingHost(
        AppConfig sourceConfig,
        Window owner,
        out AppConfig configuredConfig)
    {
        configuredConfig = JsonSerializer.Deserialize<AppConfig>(
            JsonSerializer.Serialize(sourceConfig)) ?? new AppConfig();
        string recordingPreset = string.Equals(
            sourceConfig.DeploymentPreset,
            DeploymentPresets.RecordingWorkstation,
            StringComparison.OrdinalIgnoreCase)
                ? DeploymentPresets.RecordingWorkstation
                : DeploymentPresets.RecordingHost;
        configuredConfig.DeploymentPreset = recordingPreset;
        configuredConfig.DeploymentSchemaVersion = DeploymentPresets.CurrentSchemaVersion;
        configuredConfig.WorkstationRole = WorkstationRoles.CameraMonitor;
        configuredConfig.EnableWebServer = DeploymentCapabilities
            .ForPreset(recordingPreset)
            .CanRunWebServer;

        var wizard = new FirstUseSetupWizardWindow(configuredConfig, allowSkip: false);
        if (owner != null)
            wizard.Owner = owner;
        if (wizard.ShowDialog() != true || wizard.WasSkipped)
            return false;

        configuredConfig = wizard.ResultConfig;
        AppConfig.ApplyFirstUseDefaults(configuredConfig);
        AppConfig.NormalizeAfterLoad(configuredConfig);
        return true;
    }

    private void RenderTestBarcode()
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.CODE_128,
            Options = new EncodingOptions
            {
                Width = 430,
                Height = 120,
                Margin = 12,
                PureBarcode = false
            }
        };

        var pixelData = writer.Write(_testBarcodeValue);
        var source = BitmapSource.Create(
            pixelData.Width,
            pixelData.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixelData.Pixels,
            pixelData.Width * 4);
        source.Freeze();

        TestBarcodeImage.Source = source;
        TestBarcodeText.Text = $"可选扫码枪测试条码：{_testBarcodeValue}，也可以扫描任意真实面单条码。";
    }

    private async void FirstUseSetupWizardWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoadingDevices = true;
        try
        {
            // 已配置网络摄像头时，在设备枚举前先显示面板，避免打开时闪一下。
            if (string.Equals(_config.CameraSourceKind, "network", StringComparison.OrdinalIgnoreCase))
                ShowNetworkCameraPanelUi();
            await LoadDevicesAsync();
        }
        finally
        {
            _isLoadingDevices = false;
            LoadSelectedCameraRotation();
        }
    }

    private async Task LoadDevicesAsync()
    {
        var result = await RunOnStaThread(() =>
        {
            var cameras = new List<CameraInfo>();
            var mics = new List<MicInfo>();

            try
            {
                var videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                for (int i = 0; i < videoDevices.Count; i++)
                {
                    cameras.Add(new CameraInfo
                    {
                        Index = i,
                        Name = $"[{i}] {videoDevices[i].Name}",
                        Moniker = videoDevices[i].MonikerString
                    });
                }
            }
            catch { }

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var audioDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                foreach (var device in audioDevices)
                {
                    mics.Add(new MicInfo { Name = device.FriendlyName, Moniker = device.ID });
                }
            }
            catch { }

            return (Cameras: cameras, Mics: mics);
        });

        var cameras = result.Cameras;
        if (cameras.Count == 0)
        {
            cameras.Add(new CameraInfo { Index = 0, Name = "[0] 未检测到摄像头", Moniker = "" });
        }
        cameras.Add(new CameraInfo
        {
            Index = -1,
            Name = AppLanguage.Get("网络摄像头（手动地址）"),
            Moniker = "network:"
        });

        CameraComboBox.ItemsSource = cameras;
        if (string.Equals(_config.CameraSourceKind, "network", StringComparison.OrdinalIgnoreCase))
        {
            CameraComboBox.SelectedItem = cameras.FirstOrDefault(c => c.Moniker == "network:");
        }
        else
        {
            CameraComboBox.SelectedItem = cameras.FirstOrDefault(c => !string.IsNullOrEmpty(_config.CameraMonikerString) && c.Moniker == _config.CameraMonikerString)
                ?? cameras.FirstOrDefault(c => c.Index == _config.CameraIndex)
                ?? cameras.FirstOrDefault();
        }

        var mics = result.Mics;
        if (mics.Count == 0)
        {
            mics.Add(new MicInfo { Name = "未检测到麦克风", Moniker = "" });
        }

        MicComboBox.ItemsSource = mics;
        MicComboBox.SelectedItem = mics.FirstOrDefault(m => !string.IsNullOrEmpty(_config.AudioDeviceMoniker) && m.Moniker == _config.AudioDeviceMoniker)
            ?? mics.FirstOrDefault(m => m.Name == _config.AudioDeviceName)
            ?? mics.FirstOrDefault(IsAvailableMic)
            ?? mics.FirstOrDefault();
    }

    private static Task<T> RunOnStaThread<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    private void ShowStep(int stepIndex)
    {
        _stepIndex = Math.Clamp(stepIndex, 0, 6);
        ModePage.Visibility = _stepIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        CameraPage.Visibility = _stepIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        CameraRecognitionPage.Visibility = _stepIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        RecordingProfilePage.Visibility = _stepIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        MicPage.Visibility = _stepIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        ScannerPage.Visibility = _stepIndex == 5 ? Visibility.Visible : Visibility.Collapsed;
        DonePage.Visibility = _stepIndex == 6 ? Visibility.Visible : Visibility.Collapsed;

        for (int i = 0; i < _stepTexts.Count; i++)
        {
            _stepTexts[i].Foreground = i == _stepIndex
                ? (System.Windows.Media.Brush)FindResource("AccentBlue")
                : (System.Windows.Media.Brush)FindResource("TextSecondary");
            _stepTexts[i].FontWeight = i == _stepIndex ? FontWeights.Black : FontWeights.SemiBold;
        }

        BackButton.IsEnabled = _stepIndex > 0;
        NextButton.Content = _stepIndex == 6 ? "完成" : "下一步";
        if (_stepIndex == 2)
            UpdateCameraRecognitionChoiceUi();

        if (_stepIndex == 1)
        {
            StartCameraPreviewFromSelection(enableRecognition: false);
        }
        else if (_stepIndex == 2 && UseCameraRecognitionRadio.IsChecked == true)
        {
            StartCameraPreviewFromSelection(enableRecognition: true);
        }
        else
        {
            StopCameraPreview();
        }

        if (_stepIndex == 4)
        {
            StartMicPreviewFromSelection();
        }
        else
        {
            StopMicPreview();
        }

        if (_stepIndex == 5)
        {
            ScannerIntroText.Text = UseCameraRecognitionRadio.IsChecked == true
                ? "没有扫码枪可直接进入下一步；如需使用，可在这里测试扫码枪，作为摄像头识别的后备方案"
                : "没有扫码枪也可手动输入面单号；如需使用，可在这里测试扫码枪";
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ScanTestTextBox.Focus();
                ScanTestTextBox.SelectAll();
            }));
        }

        if (_stepIndex == 6)
        {
            UpdateFlowText();
        }
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_stepIndex == 1)
        {
            if (IsNetworkCameraSelected)
            {
                if (!NetworkCameraUrlPolicy.TryNormalize(NetworkCameraUrlTextBox.Text, out _, out string networkError))
                {
                    CameraStatusText.Text = $"网络摄像头地址无效：{networkError}";
                    CameraStatusText.Visibility = Visibility.Visible;
                    return;
                }
                if (!TryLeaveCameraStep())
                    return;
                ShowStep(2);
                return;
            }

            if (CameraComboBox.SelectedItem is not CameraInfo camera
                || string.IsNullOrWhiteSpace(camera.Moniker))
            {
                CameraStatusText.Text = "未检测到可用摄像头";
                CameraStatusText.Visibility = Visibility.Visible;
                return;
            }
            if (!TryLeaveCameraStep())
                return;
            ShowStep(2);
            return;
        }

        if (_stepIndex == 2)
        {
            if (!TryLeaveCameraStep())
                return;
            ShowStep(3);
            await EnsureRecommendedCameraProfileAsync();
            return;
        }

        if (_stepIndex == 3)
        {
            if (!await EnsureRecommendedCameraProfileAsync())
                return;
        }

        if (_stepIndex == 6)
        {
            if (CameraComboBox.SelectedItem is not CameraInfo selectedCamera
                || string.IsNullOrWhiteSpace(selectedCamera.Moniker))
            {
                AppDialog.Warning(this, "录制主机必须先选择可用摄像头", "摄像头尚未配置");
                ShowStep(1);
                return;
            }
            ApplySelections();
            WasSkipped = false;
            DialogResult = true;
            Close();
            return;
        }

        ShowStep(_stepIndex + 1);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if ((_stepIndex == 1 || _stepIndex == 2) && !TryLeaveCameraStep())
            return;
        ShowStep(_stepIndex - 1);
    }

    private bool TryLeaveCameraStep()
    {
        if (StopCameraPreview())
            return true;

        SetCameraPreviewStatus("摄像头未能停止，请重新插拔后再继续");
        return false;
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        WasSkipped = true;
        DialogResult = true;
        Close();
    }

        private void CameraComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsNetworkCameraSelected)
            {
                // 切换到网络摄像头时先断开正在运行的 USB/网络预览，避免旧画面继续显示。
                if (!StopCameraPreview())
                {
                    SetCameraPreviewStatus("上一个摄像头未能停止，请重新插拔后重试");
                    return;
                }
                CameraPreviewImage.Source = null;
                CameraRecognitionPreviewImage.Source = null;
                CameraRecognitionGuide.Visibility = Visibility.Collapsed;
                _cameraBarcodeRecognition.Reset();
                ShowNetworkCameraPanelUi();
                if (_stepIndex == 1)
                {
                    CameraStatusText.Text = "请输入网络摄像头地址，然后点击测试连接";
                    CameraStatusText.Visibility = Visibility.Visible;
                }
                return;
            }

            if (_isLoadingDevices) return;
            NetworkCameraPanel.Visibility = Visibility.Collapsed;
            NetworkCameraTestButton.Visibility = Visibility.Collapsed;
            LoadSelectedCameraRotation();
            _evaluatedCameraMoniker = "";
            if (_stepIndex == 1)
        {
            StartCameraPreviewFromSelection(enableRecognition: false);
            }
        }

        private void ShowNetworkCameraPanelUi()
        {
            NetworkCameraPanel.Visibility = Visibility.Visible;
            NetworkCameraTestButton.Visibility = Visibility.Visible;
            if (string.IsNullOrWhiteSpace(NetworkCameraUrlTextBox.Text))
                NetworkCameraUrlTextBox.Text = _config.NetworkCameraUrl ?? "";
            LoadSelectedCameraRotation();
            _evaluatedCameraMoniker = "";
        }

        private void RotateCameraButton_Click(object sender, RoutedEventArgs e)
    {
        _config.CameraRotate180 = !_config.CameraRotate180;
        SaveSelectedCameraRotation();
        UpdateRotateCameraButtonText();
    }

    private void LoadSelectedCameraRotation()
    {
        string key = GetSelectedCameraConfigKey();
        _config.CameraRotate180 = !string.IsNullOrWhiteSpace(key)
            && _config.CameraConfigs.TryGetValue(key, out CameraSettings settings)
            && settings.Rotate180;
        UpdateRotateCameraButtonText();
    }

    private void SaveSelectedCameraRotation()
    {
        string key = GetSelectedCameraConfigKey();
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (!_config.CameraConfigs.TryGetValue(key, out CameraSettings settings))
        {
            settings = new CameraSettings
            {
                FrameWidth = _config.FrameWidth,
                FrameHeight = _config.FrameHeight,
                Fps = _config.Fps,
                AudioDeviceName = _config.AudioDeviceName,
                AudioDeviceMoniker = _config.AudioDeviceMoniker,
                AudioSyncOffsetMs = _config.AudioSyncOffsetMs
            };
            _config.CameraConfigs[key] = settings;
        }

        settings.Rotate180 = _config.CameraRotate180;
    }

    private string GetSelectedCameraConfigKey()
    {
        if (IsNetworkCameraSelected)
            return AppConfig.GetCameraConfigKey("network", NetworkCameraUrlTextBox.Text);
        return CameraComboBox.SelectedItem is CameraInfo camera ? camera.Moniker ?? "" : "";
    }

    private void UpdateRotateCameraButtonText()
    {
        if (RotateCameraButtonText != null)
            RotateCameraButtonText.Text = _config.CameraRotate180 ? "已旋转 180°" : "旋转 180°";
    }

    private void CameraRecognitionChoice_Changed(
        object sender,
        RoutedEventArgs e)
    {
        if (!IsInitialized)
            return;

        UpdateCameraRecognitionChoiceUi();
        if (_stepIndex != 2)
            return;

        if (UseCameraRecognitionRadio.IsChecked == true)
            StartCameraPreviewFromSelection(enableRecognition: true);
        else
            StopCameraPreview();
    }

    private void UpdateCameraRecognitionChoiceUi()
    {
        bool enabled = UseCameraRecognitionRadio.IsChecked == true;
        CameraRecognitionPreviewPanel.Visibility =
            enabled ? Visibility.Visible : Visibility.Collapsed;
        CameraRecognitionDisabledPanel.Visibility =
            enabled ? Visibility.Collapsed : Visibility.Visible;
        if (CameraComboBox.SelectedItem is CameraInfo camera)
            CameraRecognitionSelectedCameraText.Text = camera.Name;
    }

    private async Task<bool> EnsureRecommendedCameraProfileAsync(bool force = false)
    {
        if (_isRecordingProfileDetectionRunning)
            return false;

        if (CameraComboBox.SelectedItem is not CameraInfo camera
            || string.IsNullOrWhiteSpace(camera.Moniker))
        {
            RecordingProfileStatusText.Text = "未检测到可用摄像头，请返回上一步重新选择";
            RecordingProfileStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentOrange");
            RecordingProfileProgress.Visibility = Visibility.Collapsed;
            BackButton.IsEnabled = true;
            NextButton.IsEnabled = true;
            NextButton.Content = "下一步";
            return false;
        }

        if (!force
            && string.Equals(_evaluatedCameraMoniker, GetSelectedCameraConfigKey(), StringComparison.Ordinal))
            return true;

        _isRecordingProfileDetectionRunning = true;
        BackButton.IsEnabled = false;
        NextButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        CameraComboBox.IsEnabled = false;
        RecordingProfileStatusText.Text = IsNetworkCameraSelected
            ? "正在连接网络摄像头并测试实时编码能力，请稍候"
            : "正在测试 720P、1080P、2K 和 4K 的实时编码能力，请稍候";
        RecordingProfileStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentBlue");
        RecordingProfileProgress.Visibility = Visibility.Visible;
        RecordingProfileProgress.IsIndeterminate = true;
        RecordingProfileResultPanel.Visibility = Visibility.Collapsed;
        RecordingProfileRetryButton.Visibility = Visibility.Collapsed;

        IReadOnlyList<NativeCameraMode> nativeModes = [];
        try
        {
            if (IsNetworkCameraSelected)
            {
                string networkUrl = NetworkCameraUrlTextBox.Text?.Trim() ?? "";
                if (!NetworkCameraUrlPolicy.TryNormalize(networkUrl, out networkUrl, out string networkError))
                {
                    FailRecordingProfile($"网络摄像头地址无效：{networkError}");
                    return false;
                }

                using var probeSource = new NetworkCameraSource(
                    networkUrl,
                    AppConfig.NormalizeNetworkTransport(_config.NetworkCameraRtspTransport),
                    _config.Fps > 0 ? _config.Fps : 15);
                bool connected = await probeSource.StartAsync();
                if (!connected)
                {
                    FailRecordingProfile(
                        $"无法连接网络摄像头：{probeSource.LastError ?? "请检查地址和网络"}");
                    return false;
                }
                nativeModes = probeSource.NativeModes;
                probeSource.Stop();
            }
            else
            {
                nativeModes = await RunOnStaThread(() =>
                {
                    var device = new VideoCaptureDevice(camera.Moniker);
                    return RecordingProfileDetector.GetNativeModes(device.VideoCapabilities);
                });
            }

            var detection = await Task.Run(() =>
            {
                EncoderDetectionResult encoderDetection =
                    MainViewModel.DetectAvailableEncodersSync();
                if (!encoderDetection.Succeeded)
                {
                    return (
                        encoderDetection,
                        encoder: "",
                        videoCqp: RecordingProfileDetector.NormalizeVideoCqp(_config.VideoCqp),
                        ffmpegPath: AppPaths.FindFFmpeg(),
                        recommendation: new RecordingProfileRecommendation(
                            false,
                            null,
                            "未检测到可用的 FFmpeg 编码器",
                            []));
                }

                string codec = (_config.VideoCodec ?? "h264").Trim().ToLowerInvariant();
                if (codec is not ("h264" or "h265" or "av1"))
                    codec = "h264";
                string encoder = EncodingHelper.ResolveFallbackEncoder(
                    _config.GpuEncoder ?? "auto",
                    codec,
                    encoderDetection.ValidatedEncoders);
                if (!encoderDetection.ValidatedEncoders.Contains(encoder))
                    encoder = encoderDetection.ValidatedEncoders.First();
                int videoCqp = RecordingProfileDetector.NormalizeVideoCqp(_config.VideoCqp);
                string ffmpegPath = AppPaths.FindFFmpeg();
                RecordingProfileRecommendation recommendation = RecordingProfileDetector.Recommend(
                    nativeModes,
                    mode => RecordingProfileDetector.Benchmark(
                        ffmpegPath,
                        encoder,
                        videoCqp,
                        mode));
                return (encoderDetection, encoder, videoCqp, ffmpegPath, recommendation);
            });

            _config.EncoderOptionsCache = detection.encoderDetection.Options;
            _config.ValidatedEncodersCache =
                detection.encoderDetection.ValidatedEncoders.ToList();
            _config.IsEncoderDetected = detection.encoderDetection.Succeeded;
            RecordingProfileDetector.UpdateBenchmarkCache(
                _config,
                detection.encoder,
                detection.videoCqp,
                detection.recommendation.Benchmarks,
                DateTime.Now);
            RuntimeLog.Info(
                "RecordingProfile",
                $"wizard ffmpeg={detection.ffmpegPath}, encoder={detection.encoder}, cqp={detection.videoCqp}");
            foreach (RealtimeEncodingBenchmarkResult benchmark in detection.recommendation.Benchmarks)
            {
                RuntimeLog.Info(
                    "RecordingProfile",
                    $"wizard mode={benchmark.Mode.Width}x{benchmark.Mode.Height}@{benchmark.Mode.Fps}, encoder={detection.encoder}, stable={benchmark.Stable}, detail={benchmark.Detail}");
            }

            if (detection.recommendation.Success
                && detection.recommendation.Mode is NativeCameraMode mode)
            {
                _config.FrameWidth = mode.Width;
                _config.FrameHeight = mode.Height;
                _config.Fps = mode.Fps;
                _evaluatedCameraMoniker = GetSelectedCameraConfigKey();
                RealtimeEncodingBenchmarkResult benchmark =
                    RecordingProfileDetector.FindBenchmark(
                        detection.recommendation.Benchmarks,
                        mode);
                RecordingProfileStatusText.Text = "检测完成，已生成录制规格建议";
                RecordingProfileStatusText.Foreground =
                    (System.Windows.Media.Brush)FindResource("AccentGreen");
                RecordingProfileResultTitle.Text = "已自动选择录制配置";
                RecordingProfileResultTitle.Foreground =
                    (System.Windows.Media.Brush)FindResource("AccentGreen");
                RecordingProfileResultText.Text =
                    $"分辨率：{mode.Width}×{mode.Height}\n" +
                    $"原生帧率：{mode.Fps} FPS\n" +
                    $"编码器：{EncodingHelper.GetEncoderLabel(detection.encoder)}\n" +
                    $"实测最大编码速度：{benchmark?.MeasuredEncodingFps ?? 0:F1} FPS\n" +
                    "结论：已满足 20% 实时余量";
                RecordingProfileResultPanel.Visibility = Visibility.Visible;
                RuntimeLog.Info(
                    "RecordingProfile",
                    $"wizard recommended={mode.Width}x{mode.Height}@{mode.Fps}, encoder={detection.encoder}");
                return true;
            }

            NativeCameraMode? fallback =
                RecordingProfileDetector.SelectSafeFallback(nativeModes);
            if (fallback is NativeCameraMode fallbackMode)
            {
                _config.FrameWidth = fallbackMode.Width;
                _config.FrameHeight = fallbackMode.Height;
                _config.Fps = fallbackMode.Fps;
            }
            _evaluatedCameraMoniker = GetSelectedCameraConfigKey();
            RecordingProfileStatusText.Text = fallback is NativeCameraMode
                ? $"{detection.recommendation.Message}，已采用可用的原生配置，仍可继续"
                : $"{detection.recommendation.Message}，已保留程序默认配置，仍可继续";
            RecordingProfileStatusText.Foreground =
                (System.Windows.Media.Brush)FindResource("AccentOrange");
            RecordingProfileResultTitle.Text = "性能建议未完成";
            RecordingProfileResultTitle.Foreground =
                (System.Windows.Media.Brush)FindResource("AccentOrange");
            RecordingProfileResultText.Text = fallback is NativeCameraMode selectedFallback
                ? $"当前采用：{selectedFallback.Width}×{selectedFallback.Height} @ {selectedFallback.Fps} FPS\n" +
                  "该配置来自摄像头原生模式，仅作为安全兜底，尚未通过性能余量验证"
                : $"当前保留：{_config.FrameWidth}×{_config.FrameHeight} @ {_config.Fps} FPS\n" +
                  "摄像头未返回原生能力列表，已保留程序默认配置";
            RecordingProfileResultPanel.Visibility = Visibility.Visible;
            RecordingProfileRetryButton.Visibility = Visibility.Visible;
            RuntimeLog.Info(
                "RecordingProfile",
                $"wizard fallback={_config.FrameWidth}x{_config.FrameHeight}@{_config.Fps}, reason={detection.recommendation.Message}");
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("RecordingProfile", "Wizard recording profile detection failed", ex);
            NativeCameraMode? fallback =
                RecordingProfileDetector.SelectSafeFallback(nativeModes);
            if (fallback is NativeCameraMode fallbackMode)
            {
                _config.FrameWidth = fallbackMode.Width;
                _config.FrameHeight = fallbackMode.Height;
                _config.Fps = fallbackMode.Fps;
            }
            _evaluatedCameraMoniker = GetSelectedCameraConfigKey();
            RecordingProfileStatusText.Text = fallback is NativeCameraMode
                ? "录制性能检测失败，已采用可用的原生配置，仍可继续"
                : "录制性能检测失败，已保留程序默认配置，仍可继续";
            RecordingProfileStatusText.Foreground =
                (System.Windows.Media.Brush)FindResource("AccentOrange");
            RecordingProfileResultTitle.Text = "性能建议未完成";
            RecordingProfileResultTitle.Foreground =
                (System.Windows.Media.Brush)FindResource("AccentOrange");
            RecordingProfileResultText.Text = fallback is NativeCameraMode selectedFallback
                ? $"当前采用：{selectedFallback.Width}×{selectedFallback.Height} @ {selectedFallback.Fps} FPS\n" +
                  "可稍后重新检测，或继续使用并在录制设置中手动调整"
                : $"当前保留：{_config.FrameWidth}×{_config.FrameHeight} @ {_config.Fps} FPS\n" +
                  "摄像头未返回原生能力列表，已保留程序默认配置";
            RecordingProfileResultPanel.Visibility = Visibility.Visible;
            RecordingProfileRetryButton.Visibility = Visibility.Visible;
            return true;
        }
        finally
        {
            _isRecordingProfileDetectionRunning = false;
            RecordingProfileProgress.Visibility = Visibility.Collapsed;
            BackButton.IsEnabled = _stepIndex > 0;
            NextButton.IsEnabled = true;
            NextButton.Content = "下一步";
            SkipButton.IsEnabled = true;
            CameraComboBox.IsEnabled = true;
            RecordingProfileRetryButton.IsEnabled = true;
        }
    }

    private void FailRecordingProfile(string message)
    {
        _isRecordingProfileDetectionRunning = false;
        RecordingProfileStatusText.Text = message;
        RecordingProfileStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentRed");
        RecordingProfileProgress.Visibility = Visibility.Collapsed;
        RecordingProfileResultPanel.Visibility = Visibility.Visible;
        RecordingProfileRetryButton.Visibility = Visibility.Visible;
        RecordingProfileResultTitle.Text = "网络摄像头连接失败";
        RecordingProfileResultTitle.Foreground = (System.Windows.Media.Brush)FindResource("AccentRed");
        RecordingProfileResultText.Text = "请返回上一步检查地址后重试";
        BackButton.IsEnabled = _stepIndex > 0;
        NextButton.IsEnabled = true;
        SkipButton.IsEnabled = true;
        CameraComboBox.IsEnabled = true;
    }

    private async void RecordingProfileRetryButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        RecordingProfileRetryButton.IsEnabled = false;
        await EnsureRecommendedCameraProfileAsync(force: true);
    }

    private void StartCameraPreviewFromSelection(bool enableRecognition)
    {
        if (!StopCameraPreview())
        {
            SetCameraPreviewStatus("上一个摄像头未能停止，请重新插拔后重试");
            return;
        }
        CameraPreviewImage.Source = null;
        CameraRecognitionPreviewImage.Source = null;
        CameraRecognitionGuide.Visibility = Visibility.Collapsed;
        _cameraBarcodeRecognition.Reset();
        _isRecognitionPreview = enableRecognition;

        if (IsNetworkCameraSelected)
        {
            StartNetworkCameraPreview(enableRecognition);
            return;
        }

        if (CameraComboBox.SelectedItem is not CameraInfo camera || string.IsNullOrEmpty(camera.Moniker))
        {
            SetCameraPreviewStatus("未检测到可用摄像头");
            return;
        }

        try
        {
            if (enableRecognition)
                CameraRecognitionSelectedCameraText.Text = camera.Name;
            _previewCamera = new VideoCaptureDevice(camera.Moniker);
            if (_previewCamera.VideoCapabilities.Length > 0)
            {
                _previewCamera.VideoResolution = SelectBestCapability(_previewCamera.VideoCapabilities);
            }

            _previewCamera.NewFrame += PreviewCamera_NewFrame;
            _previewCamera.Start();
            SetCameraPreviewStatus(
                enableRecognition
                    ? "正在等待摄像头识别画面..."
                    : "正在等待摄像头画面...");
        }
        catch (Exception ex)
        {
            SetCameraPreviewStatus($"摄像头预览启动失败：{ex.Message}");
            StopCameraPreview();
        }
    }

    private async void StartNetworkCameraPreview(bool enableRecognition)
    {
        if (!NetworkCameraUrlPolicy.TryNormalize(NetworkCameraUrlTextBox.Text, out string url, out string error))
        {
            SetCameraPreviewStatus($"地址无效：{error}");
            return;
        }

        NetworkCameraTestButton.IsEnabled = false;
        SetCameraPreviewStatus("正在连接网络摄像头...");
        var source = new NetworkCameraSource(
            url,
            AppConfig.NormalizeNetworkTransport(_config.NetworkCameraRtspTransport),
            _config.Fps > 0 ? _config.Fps : 15);
        _networkPreviewSource = source;
        source.FrameReady += NetworkPreviewSource_FrameReady;

        bool connected = await source.StartAsync();
        NetworkCameraTestButton.IsEnabled = true;
        if (!connected)
        {
            NetworkPreviewSourceStop();
            SetCameraPreviewStatus($"连接失败：{source.LastError ?? "无法获取画面信息"}");
            return;
        }

        if (enableRecognition)
            CameraRecognitionSelectedCameraText.Text = AppLanguage.Get("网络摄像头（手动地址）");
        SetCameraPreviewStatus("正在等待网络摄像头画面...");
    }

    private void NetworkPreviewSource_FrameReady(object sender, NetworkCameraFrameEventArgs e)
    {
        try
        {
            if (DateTime.UtcNow - _lastPreviewUpdateAt < TimeSpan.FromMilliseconds(100))
            {
                e.Frame.Dispose();
                return;
            }
            _lastPreviewUpdateAt = DateTime.UtcNow;

            using Mat frame = e.Frame;
            if (_config.CameraRotate180)
                OpenCvSharp.Cv2.Flip(frame, frame, OpenCvSharp.FlipMode.XY);

            bool recognitionPreview = _isRecognitionPreview;
            if (recognitionPreview)
            {
                _cameraBarcodeRecognition.TrySubmitFrame(
                    frame.Clone());
            }

            int width = frame.Width;
            int height = frame.Height;
            byte[] pixels = new byte[width * height * 3];
            Marshal.Copy(frame.Data, pixels, 0, pixels.Length);
            BitmapSource source = BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Bgr24,
                null,
                pixels,
                width * 3);
            source.Freeze();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (recognitionPreview)
                {
                    CameraRecognitionPreviewImage.Source = source;
                    CameraRecognitionGuide.Visibility = Visibility.Visible;
                    UpdateCameraRecognitionGuide();
                    CameraRecognitionPreviewStatusText.Visibility = Visibility.Collapsed;
                }
                else
                {
                    CameraPreviewImage.Source = source;
                    CameraStatusText.Visibility = Visibility.Collapsed;
                }
            }));
        }
        catch
        {
            e.Frame.Dispose();
        }
    }

    private void NetworkPreviewSourceStop()
    {
        NetworkCameraSource source = _networkPreviewSource;
        if (source == null)
            return;
        source.FrameReady -= NetworkPreviewSource_FrameReady;
        try
        {
            source.Stop();
        }
        catch { }
        if (ReferenceEquals(_networkPreviewSource, source))
            _networkPreviewSource = null;
    }

    private void NetworkCameraTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (!StopCameraPreview())
        {
            SetCameraPreviewStatus("上一个摄像头未能停止，请稍后重试");
            return;
        }
        CameraPreviewImage.Source = null;
        CameraRecognitionPreviewImage.Source = null;
        _cameraBarcodeRecognition.Reset();
        _isRecognitionPreview = false;
        StartNetworkCameraPreview(enableRecognition: false);
    }

    private bool IsNetworkCameraSelected =>
        CameraComboBox.SelectedItem is CameraInfo camera && camera.Moniker == "network:";

    private void NetworkCameraUrlTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        NetworkCameraUrlPlaceholderText.Visibility = string.IsNullOrEmpty(NetworkCameraUrlTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SetCameraPreviewStatus(string text)
    {
        TextBlock statusText = _isRecognitionPreview
            ? CameraRecognitionPreviewStatusText
            : CameraStatusText;
        statusText.Text = text;
        statusText.Visibility = Visibility.Visible;
    }

    private VideoCapabilities SelectBestCapability(VideoCapabilities[] capabilities)
    {
        var best = capabilities[0];
        int bestScore = int.MaxValue;
        foreach (var capability in capabilities)
        {
            int resDiff = Math.Abs(capability.FrameSize.Width - _config.FrameWidth) + Math.Abs(capability.FrameSize.Height - _config.FrameHeight);
            int fpsDiff = Math.Abs(capability.AverageFrameRate - _config.Fps);
            int score = resDiff * 10 + fpsDiff;
            if (score < bestScore)
            {
                bestScore = score;
                best = capability;
            }
        }

        return best;
    }

    private void PreviewCamera_NewFrame(object sender, NewFrameEventArgs eventArgs)
    {
        if (DateTime.UtcNow - _lastPreviewUpdateAt < TimeSpan.FromMilliseconds(100)) return;
        _lastPreviewUpdateAt = DateTime.UtcNow;

        try
        {
            using var bitmap = (Bitmap)eventArgs.Frame.Clone();
            if (_config.CameraRotate180)
                bitmap.RotateFlip(RotateFlipType.Rotate180FlipNone);
            bool recognitionPreview = _isRecognitionPreview;
            if (recognitionPreview)
            {
                using Mat recognitionFrame = BitmapToMat(bitmap);
                _cameraBarcodeRecognition.TrySubmitFrame(
                    recognitionFrame);
            }
            BitmapSource source = ConvertBitmapToSource(bitmap);
            source.Freeze();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (recognitionPreview)
                {
                    CameraRecognitionPreviewImage.Source = source;
                    CameraRecognitionGuide.Visibility = Visibility.Visible;
                    UpdateCameraRecognitionGuide();
                    CameraRecognitionPreviewStatusText.Visibility = Visibility.Collapsed;
                }
                else
                {
                    CameraPreviewImage.Source = source;
                    CameraStatusText.Visibility = Visibility.Collapsed;
                }
            }));
        }
        catch { }
    }

    private bool IsCameraBarcodeCandidate(string value)
    {
        return CameraBarcodeCandidatePolicy.IsValid(value, _config.OrderIdRegex);
    }

    private void CameraBarcodeRecognition_StatusChanged(CameraBarcodeRecognitionStatus status)
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_stepIndex != 2 || UseCameraRecognitionRadio.IsChecked != true)
                return;

            if (status.State is CameraBarcodeRecognitionState.Confirmed
                or CameraBarcodeRecognitionState.Visible)
            {
                CameraRecognitionStatusText.Text = $"当前识别：{status.Code}";
                SetCameraRecognitionGuideColor("AccentGreen");
                return;
            }

            CameraRecognitionStatusText.Text = status.State == CameraBarcodeRecognitionState.Candidate
                ? $"当前识别：{status.Code}"
                : "将面单条形码放入框内";
            SetCameraRecognitionGuideColor(
                status.State == CameraBarcodeRecognitionState.Candidate
                    ? "AccentOrange"
                    : "AccentBlue");
        }));
    }

    private void SetCameraRecognitionGuideColor(string resourceKey)
    {
        if (FindResource(resourceKey) is not SolidColorBrush accentBrush)
            return;

        System.Windows.Media.Color color = accentBrush.Color;
        CameraRecognitionGuideBorder.Stroke = accentBrush;
        CameraRecognitionGuideBorder.Fill = new SolidColorBrush(
            System.Windows.Media.Color.FromArgb(0x08, color.R, color.G, color.B));
        CameraRecognitionStatusBorder.Background = new SolidColorBrush(
            System.Windows.Media.Color.FromArgb(0xDD, color.R, color.G, color.B));
    }

    private void UpdateCameraRecognitionGuide()
    {
        if (CameraRecognitionPreviewImage.Source is not BitmapSource source)
            return;

        double actualW = CameraRecognitionPreviewImage.ActualWidth;
        double actualH = CameraRecognitionPreviewImage.ActualHeight;
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0 || actualW <= 0 || actualH <= 0)
            return;

        var geometry = new CameraBarcodeGuideGeometry(
            _config.CameraBarcodeGuideWidthRatio,
            _config.CameraBarcodeGuideHeightRatio,
            _config.CameraBarcodeGuideOffsetX,
            _config.CameraBarcodeGuideOffsetY);
        double scale = Math.Min(actualW / source.PixelWidth, actualH / source.PixelHeight);
        CameraRecognitionGuide.Width = source.PixelWidth * geometry.WidthRatio * scale;
        CameraRecognitionGuide.Height = source.PixelHeight * geometry.HeightRatio * scale;
        double offsetXPx = (source.PixelWidth - source.PixelWidth * geometry.WidthRatio) / 2.0
            * geometry.OffsetX
            * scale;
        double offsetYPx = (source.PixelHeight - source.PixelHeight * geometry.HeightRatio) / 2.0
            * geometry.OffsetY
            * scale;
        CameraRecognitionGuide.RenderTransform = new TranslateTransform(offsetXPx, offsetYPx);
    }

    private static Mat BitmapToMat(Bitmap bitmap)
    {
        if (bitmap.PixelFormat != System.Drawing.Imaging.PixelFormat.Format24bppRgb)
        {
            using var converted = new Bitmap(bitmap.Width, bitmap.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(converted))
                graphics.DrawImage(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
            return BitmapToMat(converted);
        }

        var rect = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        try
        {
            return Mat.FromPixelData(bitmap.Height, bitmap.Width, MatType.CV_8UC3, data.Scan0, data.Stride).Clone();
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static BitmapSource ConvertBitmapToSource(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Bmp);
        stream.Position = 0;
        var source = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return source;
    }

    private bool StopCameraPreview()
    {
        NetworkCameraSource networkSource = _networkPreviewSource;
        if (networkSource != null)
        {
            networkSource.FrameReady -= NetworkPreviewSource_FrameReady;
            try
            {
                networkSource.Stop();
            }
            catch { }
            if (ReferenceEquals(_networkPreviewSource, networkSource))
                _networkPreviewSource = null;
            _isRecognitionPreview = false;
            _cameraBarcodeRecognition.Reset();
            return true;
        }

        VideoCaptureDevice camera = _previewCamera;
        if (camera == null)
        {
            _isRecognitionPreview = false;
            _cameraBarcodeRecognition.Reset();
            return true;
        }

        try { camera.NewFrame -= PreviewCamera_NewFrame; } catch { }
        try
        {
            if (camera.IsRunning)
            {
                camera.SignalToStop();
                for (int i = 0; i < 20 && camera.IsRunning; i++)
                {
                    Thread.Sleep(50);
                }
            }
        }
        catch { }

        if (camera.IsRunning)
        {
            if (_previewCameraForceStopTask == null || _previewCameraForceStopTask.IsCompleted)
                _previewCameraForceStopTask = Task.Run(() => camera.Stop());
            try { _previewCameraForceStopTask.Wait(2000); } catch { }
        }

        if (camera.IsRunning)
            return false;

        if (ReferenceEquals(_previewCamera, camera))
            _previewCamera = null;
        _previewCameraForceStopTask = null;
        _isRecognitionPreview = false;
        _cameraBarcodeRecognition.Reset();
        return true;
    }

    private void MicComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingDevices) return;
        if (_stepIndex == 4)
        {
            StartMicPreviewFromSelection();
        }
    }

    private void StartMicPreviewFromSelection()
    {
        StopMicPreview();
        MicLevelBar.Value = 0;

        if (MicComboBox.SelectedItem is not MicInfo mic || !IsAvailableMic(mic))
        {
            MicStatusText.Text = "未检测到可用麦克风";
            return;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = !string.IsNullOrEmpty(mic.Moniker)
                ? enumerator.GetDevice(mic.Moniker)
                : enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            _micCapture = new WasapiCapture(device);
            _micCapture.DataAvailable += MicCapture_DataAvailable;
            _micCapture.RecordingStopped += (_, __) => Dispatcher.BeginInvoke(new Action(() => MicLevelBar.Value = 0));
            _micCapture.StartRecording();
            MicStatusText.Text = "请对着麦克风说话，观察音量条";
        }
        catch (Exception ex)
        {
            MicStatusText.Text = $"麦克风启动失败：{ex.Message}";
            StopMicPreview();
        }
    }

    private void MicCapture_DataAvailable(object sender, WaveInEventArgs e)
    {
        double peak = CalculatePeak(e.Buffer, e.BytesRecorded, _micCapture.WaveFormat);
        double value = Math.Clamp(peak * 140.0, 0, 100);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            MicLevelBar.Value = value;
            if (value > 8)
            {
                MicStatusText.Text = "已检测到麦克风音量";
                MicStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreen");
            }
        }));
    }

    private static double CalculatePeak(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        double peak = 0;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (int i = 0; i + 4 <= bytesRecorded; i += 4)
            {
                peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer, i)));
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (int i = 0; i + 2 <= bytesRecorded; i += 2)
            {
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buffer, i) / 32768.0));
            }
        }
        else if (format.BitsPerSample == 24)
        {
            for (int i = 0; i + 3 <= bytesRecorded; i += 3)
            {
                int sample = (buffer[i + 2] << 24) | (buffer[i + 1] << 16) | (buffer[i] << 8);
                peak = Math.Max(peak, Math.Abs(sample / 2147483648.0));
            }
        }
        else if (format.BitsPerSample == 32)
        {
            for (int i = 0; i + 4 <= bytesRecorded; i += 4)
            {
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt32(buffer, i) / 2147483648.0));
            }
        }

        return peak;
    }

    private void StopMicPreview()
    {
        if (_micCapture == null) return;

        try { _micCapture.DataAvailable -= MicCapture_DataAvailable; } catch { }
        try { _micCapture.StopRecording(); } catch { }
        _micCapture.Dispose();
        _micCapture = null;
    }

    private void ScanTestTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_stepIndex != 5) return;
        string content = ScanTestTextBox.Text.Trim();
        if (string.IsNullOrEmpty(content))
        {
            _scannerDetectedEnter = false;
            ScanStatusText.Text = "没有扫码枪可直接进入下一步";
            ScanStatusText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary");
            return;
        }

        ScanStatusText.Text =
            "未检测到扫码枪自动回车，已准备切换为窗口内识别。\n" +
            "这种模式需要软件窗口在前台，避免影响其他输入。\n" +
            "建议按扫码枪说明书，或联系卖家开启“扫描后自动回车 / Enter 后缀”，体验会更稳定。";
        ScanStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentOrange");
    }

    private void ScanTestTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            _scannerDetectedEnter = true;
            ScanStatusText.Text = "已检测到自动回车，可支持后台扫码。";
            ScanStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreen");
            e.Handled = true;
        }
    }

    private void UpdateFlowText()
    {
        if (UseCameraRecognitionRadio.IsChecked != true)
        {
            FlowText.Text = SameCodeModeRadio.IsChecked == true
                ? "1. 使用扫码枪或在主页面输入面单号开始录制\n" +
                  "2. 完成当前包裹的打包\n" +
                  "3. 再次扫描或输入同一面单号停止录制\n" +
                  "4. 未连接扫码枪时也可直接使用主页面输入框"
                : "1. 使用扫码枪或在主页面输入面单号开始录制\n" +
                  "2. 完成当前包裹的打包\n" +
                  "3. 扫描或输入下一张面单号\n" +
                  "4. 软件自动保存上一单并开始下一单";
        }
        else if (SameCodeModeRadio.IsChecked == true)
        {
            FlowText.Text =
                "1. 将面单条形码放入识别框开始录制\n" +
                "2. 打包完成后将面单移出识别框至少 1.5 秒\n" +
                "3. 再次放入同一面单，软件停止录制并保存视频\n" +
                "4. 可选扫码枪仍可随时作为后备方案";
        }
        else
        {
            FlowText.Text =
                "1. 将面单条形码放入识别框开始录制\n" +
                "2. 完成当前包裹的打包\n" +
                "3. 将下一张面单放入识别框\n" +
                "4. 软件自动保存上一单并开始下一单\n" +
                "5. 可选扫码枪仍可随时作为后备方案";
        }
    }

    private void ApplySelections()
    {
        _config.EnableSameBarcodeStopRecording = SameCodeModeRadio.IsChecked == true;
        _config.EnableCameraBarcodeRecognition =
            UseCameraRecognitionRadio.IsChecked == true;
        _config.EnableAudioRecording = true;
        ApplyScannerModeFromTest();

        if (MicComboBox.SelectedItem is MicInfo mic && IsAvailableMic(mic))
        {
            _config.AudioDeviceName = mic.Name;
            _config.AudioDeviceMoniker = mic.Moniker ?? "";
        }
        else
        {
            _config.AudioDeviceName = "";
            _config.AudioDeviceMoniker = "";
        }

        if (IsNetworkCameraSelected)
        {
            if (NetworkCameraUrlPolicy.TryNormalize(NetworkCameraUrlTextBox.Text, out string networkUrl, out _))
            {
                _config.CameraSourceKind = "network";
                _config.NetworkCameraUrl = networkUrl;
                _config.CameraMonikerString = "";
                _config.CameraIndex = -1;
                _config.CameraConfigs[AppConfig.GetCameraConfigKey("network", networkUrl)] = new CameraSettings
                {
                    FrameWidth = _config.FrameWidth,
                    FrameHeight = _config.FrameHeight,
                    Fps = _config.Fps,
                    AudioDeviceName = _config.AudioDeviceName,
                    AudioDeviceMoniker = _config.AudioDeviceMoniker,
                    AudioSyncOffsetMs = _config.AudioSyncOffsetMs,
                    Rotate180 = _config.CameraRotate180
                };
            }
        }
        else if (CameraComboBox.SelectedItem is CameraInfo camera && !string.IsNullOrEmpty(camera.Moniker))
        {
            _config.CameraSourceKind = "usb";
            _config.CameraIndex = camera.Index;
            _config.CameraMonikerString = camera.Moniker;
        }

        if (!string.IsNullOrEmpty(_config.CameraMonikerString))
        {
            _config.CameraConfigs[_config.CameraMonikerString] = new CameraSettings
            {
                FrameWidth = _config.FrameWidth,
                FrameHeight = _config.FrameHeight,
                Fps = _config.Fps,
                AudioDeviceName = _config.AudioDeviceName,
                AudioDeviceMoniker = _config.AudioDeviceMoniker,
                AudioSyncOffsetMs = _config.AudioSyncOffsetMs,
                Rotate180 = _config.CameraRotate180
            };
        }
    }

    private void ApplyScannerModeFromTest()
    {
        string scannedText = ScanTestTextBox.Text?.Trim() ?? "";
        if (_scannerDetectedEnter)
        {
            _config.EnableGlobalKeyboard = true;
            _config.EnableScannerAutoSubmit = false;
            return;
        }

        if (!string.IsNullOrWhiteSpace(scannedText))
        {
            _config.EnableGlobalKeyboard = false;
            _config.EnableScannerAutoSubmit = true;
        }
    }

    private static bool IsAvailableMic(MicInfo mic)
    {
        return mic != null
            && !string.IsNullOrWhiteSpace(mic.Name)
            && mic.Name != "未检测到麦克风";
    }

    private void FirstUseSetupWizardWindow_Closed(object sender, EventArgs e)
    {
        StopCameraPreview();
        StopMicPreview();
        _cameraBarcodeRecognition.Dispose();
    }
}
