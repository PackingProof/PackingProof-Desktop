#nullable disable
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Audio;
using System;
using System.Windows;
using System.Collections.Generic;
using ExpressPackingMonitoring.ViewModels;
using AForge.Video.DirectShow;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ExpressPackingMonitoring.Localization;
using System.Windows.Media.Imaging;
using ExpressPackingMonitoring.Services;
using NAudio.CoreAudioApi;
using System.Text.Json;
using System.Collections.ObjectModel;

namespace ExpressPackingMonitoring.UI
{
    public class CameraInfo { public int Index { get; set; } public string Name { get; set; } public string Moniker { get; set; } public override string ToString() => Name; }
    public class ResOption { public string Name { get; set; } public int Width { get; set; } public int Height { get; set; } public override string ToString() => Name; }
    public class MicInfo
    {
        public string Name { get; set; }
        public string Moniker { get; set; }
        public override string ToString() => Name;
    }
    public class FpsOption { public int Fps { get; set; } public string Label { get; set; } public override string ToString() => Label; }
    public class EdgeVoiceOption { public string ShortName { get; set; } public string DisplayName { get; set; } public override string ToString() => DisplayName; }

    public sealed class CqpToQualitySliderConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double cqp = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return Math.Clamp((51 - cqp) * 2.0, 0, 100);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double quality = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            int cqp = (int)Math.Round(51 - Math.Clamp(quality, 0, 100) / 2.0);
            return Math.Clamp(cqp, 1, 51);
        }
    }

    public sealed class IntSliderValueConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return 0d;
            return System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double sliderValue = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return (int)Math.Round(sliderValue);
        }
    }

    public sealed class VideoQualityLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double quality = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (quality < 34) return "更省空间";
            if (quality < 67) return "标准（推荐）";
            return "更清晰";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }

    public sealed class PreRecordBufferSizeLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int mb = Math.Max(0, (int)Math.Round(System.Convert.ToDouble(value, CultureInfo.InvariantCulture)));
            return mb >= 1024 && mb % 1024 == 0 ? $"{mb / 1024} GB" : $"{mb} MB";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public sealed class AnyTrueConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            return values.Any(value => value is bool boolean && boolean);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            targetTypes.Select(_ => Binding.DoNothing).ToArray();
    }

    public sealed class AdvancedModeTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            AppLanguage.Get(value is true ? "点击隐藏高级选项" : "点击显示高级选项");

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }

    public partial class SettingsWindow : Window
    {
        public SettingsContext Context { get; }
        public SettingsCapabilities Capabilities => Context.Capabilities;
        public string ConnectionAddress => Context.ConnectionAddressProvider?.Invoke() ?? "尚未准备";
        public AppConfig Config { get; set; }
        public double CurrentDiskUsagePercent { get; set; }
        public string CurrentDiskUsageText { get; set; }
        public string AppVersion { get; } = ExpressPackingMonitoring.Config.AppVersion.Current;
        public string AppBuildDate { get; } = ExpressPackingMonitoring.Config.AppVersion.BuildDateText;
        public string AppCommitText { get; } = GetAppCommitText();
        public string AppCommitToolTip { get; } = GetAppCommitToolTip();
        public ImageSource AppIconImage { get; } = GetLargestAppIconImage();
        public List<EdgeVoiceOption> EdgeVoiceOptions { get; } = new()
        {
            new EdgeVoiceOption { ShortName = "zh-CN-XiaoxiaoNeural", DisplayName = "晓晓 - 女声" },
            new EdgeVoiceOption { ShortName = "zh-CN-XiaoyiNeural", DisplayName = "晓伊 - 女声" },
            new EdgeVoiceOption { ShortName = "zh-CN-YunjianNeural", DisplayName = "云健 - 男声" },
            new EdgeVoiceOption { ShortName = "zh-CN-YunxiNeural", DisplayName = "云希 - 男声" },
            new EdgeVoiceOption { ShortName = "zh-CN-YunxiaNeural", DisplayName = "云夏 - 男声" },
            new EdgeVoiceOption { ShortName = "zh-CN-YunyangNeural", DisplayName = "云扬 - 男声" },
            new EdgeVoiceOption { ShortName = "zh-CN-liaoning-XiaobeiNeural", DisplayName = "辽宁晓北 - 女声" },
            new EdgeVoiceOption { ShortName = "zh-CN-shaanxi-XiaoniNeural", DisplayName = "陕西晓妮 - 女声" },
            new EdgeVoiceOption { ShortName = "zh-HK-HiuGaaiNeural", DisplayName = "粤语 HiuGaai - 女声" },
            new EdgeVoiceOption { ShortName = "zh-HK-WanLungNeural", DisplayName = "粤语 WanLung - 男声" },
            new EdgeVoiceOption { ShortName = "zh-TW-HsiaoChenNeural", DisplayName = "台湾晓臻 - 女声" },
            new EdgeVoiceOption { ShortName = "zh-TW-YunJheNeural", DisplayName = "台湾云哲 - 男声" },
            new EdgeVoiceOption { ShortName = "en-US-JennyNeural", DisplayName = "Jenny - Female (US)" },
            new EdgeVoiceOption { ShortName = "en-US-AriaNeural", DisplayName = "Aria - Female (US)" },
            new EdgeVoiceOption { ShortName = "en-US-GuyNeural", DisplayName = "Guy - Male (US)" },
            new EdgeVoiceOption { ShortName = "en-US-DavisNeural", DisplayName = "Davis - Male (US)" }
        };

        private string _originalTheme;
        private string _originalLanguage;
        private readonly string _originalDeploymentPreset;

        private static string GetAppCommitText()
        {
            string shortId = ExpressPackingMonitoring.Config.AppVersion.CommitShortId;
            return shortId.Length == 0
                ? AppLanguage.Get("Commit 未知")
                : AppLanguage.Format("Commit {0}", shortId);
        }

        private static string GetAppCommitToolTip()
        {
            string commitId = ExpressPackingMonitoring.Config.AppVersion.CommitId;
            return commitId.Length == 0
                ? AppLanguage.Get("Commit 未知")
                : AppLanguage.Format("完整 Commit ID：{0}", commitId);
        }
        private string _originalNodeName;
        private string _originalWebAccessKey;
        private bool _isRecording;
        private CollectionViewSource _localStorageView;
        private CollectionViewSource _backupStorageView;
        private bool _isLoadingDevices;
        private bool _isSyncingVoiceEngine;
        private bool _isSyncingScannerModes;
        private bool _isApplyingDirectAacRecordingChoice;
        private bool _recordingCacheLimitExplained;
        public ObservableCollection<UserscriptDescriptor> CustomUserscripts { get; } = new();
        public ObservableCollection<ExtensionAuthorizationDisplayItem> ExtensionAuthorizations { get; } = new();
        public ObservableCollection<OrderIntegrationDeviceDisplayItem> OrderIntegrationDevices { get; } = new();
        private readonly System.Windows.Threading.DispatcherTimer _integrationStatusTimer = new()
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        private const string FeedbackEmail = "PackingProof@outlook.com";

        public SettingsWindow(MainViewModel mainVM, AppConfig clonedConfig, double diskUsagePercent, string diskUsageText, bool isRecording = false)
            : this(SettingsContext.ForCameraWorkstation(mainVM), clonedConfig, diskUsagePercent, diskUsageText, isRecording)
        {
        }

        public SettingsWindow(SettingsContext context, AppConfig clonedConfig, double diskUsagePercent, string diskUsageText, bool isRecording = false)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            _originalTheme = clonedConfig.Theme;
            _originalLanguage = clonedConfig.Language;
            _originalDeploymentPreset = DeploymentPresets.Normalize(clonedConfig.DeploymentPreset);
            _isRecording = isRecording;
            Config = clonedConfig;
            AppConfig.NormalizeAfterLoad(Config);
            Title = $"设置 {ExpressPackingMonitoring.Config.AppVersion.Current} ({ExpressPackingMonitoring.Config.AppVersion.CommitShortId})";
            _originalNodeName = Config.NodeName;
            _originalWebAccessKey = Config.WebAccessKey;
            InitializeComponent();

            CurrentDiskUsagePercent = diskUsagePercent;
            CurrentDiskUsageText = diskUsageText;

            this.DataContext = this;
            RefreshCustomUserscripts();
            RefreshExtensionAuthorizations();
            RefreshOrderIntegrationDevices();
            _integrationStatusTimer.Tick += IntegrationStatusTimer_Tick;
            if (Capabilities.CanRunWebServer)
                _integrationStatusTimer.Start();
            if (Capabilities.IsRecordingDevice)
                SyncVoiceEngineComboBoxFromConfig();
            if (Capabilities.CanUseScanner)
                SyncPackingModeComboBoxFromConfig();
            UpdateEventRecordingBufferControlState();
            UpdatePreRecordEstimateText();

            if (Capabilities.CanRecordPcVideo)
            {
                // GPU编码器使用缓存，可立即加载
                LoadGpuEncoders();
                LoadVideoCodecs();
                if (Config.ZoomScale < 1.2 || Config.ZoomScale > 4.0) Config.ZoomScale = 1.5;
            }

            if (Capabilities.CanConfigureStorage)
            {
                EnsurePrimaryStorageLocationExists();
                // 如果没有数据项，构造1个默认项，UI DataGrid 绑定后自动显示
                if (Config.StorageLocations.Count == 0)
                {
                    Config.StorageLocations.Add(new StorageLocation());
                }
                SortStorageLocationsByPriority();
                RefreshStoragePriorities();
                UpdateStorageButtonStates();
                _localStorageView = new CollectionViewSource { Source = Config.StorageLocations };
                _localStorageView.Filter += LocalStorageView_Filter;
                StorageDataGrid.ItemsSource = _localStorageView.View;
                _backupStorageView = new CollectionViewSource { Source = Config.StorageLocations };
                _backupStorageView.Filter += BackupStorageView_Filter;
                BackupStorageDataGrid.ItemsSource = _backupStorageView.View;
                RefreshStorageViews();
                UpdateBackupStorageButtonStates();
            }
            if (Capabilities.CanConfigureRecordingCache)
            {
                EnsureRecordingCacheLocationExists();
                Config.RecordingCachePolicy = "KeepWithinSize";
                RefreshRecordingCacheStorageSummary();
            }

            // 从注册表读取实际的开机自启动状态
            Config.AutoStartOnBoot = AutoStartService.IsEnabled();

            // 窗口加载后异步枚举设备，避免阻塞UI线程
            this.Loaded += SettingsWindow_Loaded;
        }

        private void GlobalKeyboardCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_isSyncingScannerModes) return;

            try
            {
                _isSyncingScannerModes = true;
                Config.EnableGlobalKeyboard = true;
                Config.EnableScannerAutoSubmit = false;
                if (ScannerAutoSubmitCheckBox != null)
                {
                    ScannerAutoSubmitCheckBox.IsChecked = false;
                }
            }
            finally
            {
                _isSyncingScannerModes = false;
            }
        }

        private void ScannerAutoSubmitCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_isSyncingScannerModes) return;

            try
            {
                _isSyncingScannerModes = true;
                Config.EnableScannerAutoSubmit = true;
                Config.EnableGlobalKeyboard = false;
                if (GlobalKeyboardCheckBox != null)
                {
                    GlobalKeyboardCheckBox.IsChecked = false;
                }
            }
            finally
            {
                _isSyncingScannerModes = false;
            }
        }

        private void DirectAacRecordingCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _isApplyingDirectAacRecordingChoice || sender is not CheckBox checkBox)
                return;

            ApplyDirectAacRecordingChoice(checkBox, ConfirmDirectAacRecordingRisk());
        }

        private void DirectAacRecordingCheckBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not CheckBox { IsChecked: false } checkBox)
                return;

            e.Handled = true;
            checkBox.Focus();
            ApplyDirectAacRecordingChoice(checkBox, ConfirmDirectAacRecordingRisk());
        }

        private void DirectAacRecordingCheckBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space || sender is not CheckBox { IsChecked: false } checkBox)
                return;

            e.Handled = true;
            ApplyDirectAacRecordingChoice(checkBox, ConfirmDirectAacRecordingRisk());
        }

        private bool ConfirmDirectAacRecordingRisk()
        {
            return AppDialog.Confirm(
                this,
                AppLanguage.Get("实时封装时如果麦克风断开或音频设备异常被占用，可能导致 FFmpeg 录制中断，从而造成视频异常或录制失败"),
                AppLanguage.Get("开启音频直接写入 MKV？"),
                AppDialogSeverity.Warning,
                confirmText: AppLanguage.Get("了解风险并开启"),
                cancelText: AppLanguage.Get("保持关闭"));
        }

        private void ApplyDirectAacRecordingChoice(CheckBox checkBox, bool enabled)
        {
            _isApplyingDirectAacRecordingChoice = true;
            try
            {
                Config.EnableDirectAacRecording = enabled;
                checkBox.SetCurrentValue(
                    System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                    enabled);
                checkBox.GetBindingExpression(
                    System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateSource();
            }
            finally
            {
                _isApplyingDirectAacRecordingChoice = false;
            }
        }

        private void AdvancedModeButton_Unchecked(object sender, RoutedEventArgs e)
        {
            Keyboard.ClearFocus();
            if (sender is System.Windows.Controls.Primitives.ToggleButton button)
                button.Focus();
        }

        private void SyncScannerModeControlsFromConfig()
        {
            if (GlobalKeyboardCheckBox == null || ScannerAutoSubmitCheckBox == null)
                return;

            try
            {
                _isSyncingScannerModes = true;
                GlobalKeyboardCheckBox.IsChecked = Config.EnableGlobalKeyboard;
                ScannerAutoSubmitCheckBox.IsChecked = Config.EnableScannerAutoSubmit;
            }
            finally
            {
                _isSyncingScannerModes = false;
            }
        }

        private void EnsurePrimaryStorageLocationExists()
        {
            if (Config.StorageLocations == null) Config.StorageLocations = new List<StorageLocation>();
            if (Config.StorageLocations.Count == 0)
            {
                Config.StorageLocations.Add(new StorageLocation());
            }
        }

        private void EnsureRecordingCacheLocationExists()
        {
            if (RecordingWorkstationCachePolicy.GetConfiguredLocation(Config) != null)
                return;
            RecordingWorkstationCachePolicy.ConfigureInitialLocation(
                Config,
                preserveExistingLocation: false);
            if (RecordingWorkstationCachePolicy.GetConfiguredLocation(Config) == null)
            {
                Config.StorageLocations =
                [
                    new StorageLocation
                    {
                        Path = Path.Combine(
                            string.IsNullOrWhiteSpace(
                                Environment.GetFolderPath(
                                    Environment.SpecialFolder.MyVideos))
                                ? AppPaths.UserDataDir
                                : Environment.GetFolderPath(
                                    Environment.SpecialFolder.MyVideos),
                            "快递打包视频"),
                        Priority = 0
                    }
                ];
            }
        }

        public void SelectRecordingCacheTab()
        {
            if (Capabilities.CanConfigureRecordingCache)
                SettingsTabControl.SelectedItem = RecordingCacheTabItem;
        }

        private void SoundSettingsPanel_Loaded(object sender, RoutedEventArgs e)
        {
            int voiceOptionsIndex = SoundSettingsPanel.Children.IndexOf(VoiceOptionsCard);
            int orderAnnounceIndex = SoundSettingsPanel.Children.IndexOf(OrderAnnounceCard);
            if (voiceOptionsIndex < 0 || orderAnnounceIndex < 0 || orderAnnounceIndex < voiceOptionsIndex)
                return;

            SoundSettingsPanel.Children.RemoveAt(orderAnnounceIndex);
            SoundSettingsPanel.Children.Insert(voiceOptionsIndex, OrderAnnounceCard);
        }

        private async void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (Capabilities.CanUseCamera || Capabilities.CanRecordAudio)
            {
                // 已配置网络摄像头时，在设备枚举前先显示面板，避免重新打开时闪一下。
                if (Capabilities.CanUseCamera
                    && string.Equals(Config.CameraSourceKind, "network", StringComparison.OrdinalIgnoreCase))
                {
                    ShowNetworkCameraPanelUi();
                }

                _isLoadingDevices = true;
                try
                {
                    await LoadAllDevicesAsync();
                }
                finally
                {
                    _isLoadingDevices = false;
                }

                // 加载断句关键词到文本框
                if (Config.TtsBreakWords != null && Config.TtsBreakWords.Count > 0)
                    TtsBreakWordsTextBox.Text = string.Join("\n", Config.TtsBreakWords);

                if (_isRecording)
                {
                    CameraComboBox.IsEnabled = false;
                    ResComboBox.IsEnabled = false;
                    FpsComboBox.IsEnabled = false;
                    DetectRecordingProfileButton.IsEnabled = false;
                    CameraComboBox.ToolTip = "录制中不可修改，停止录制后再更改";
                    ResComboBox.ToolTip = "录制中不可修改，停止录制后再更改";
                    FpsComboBox.ToolTip = "录制中不可修改，停止录制后再更改";
                    DetectRecordingProfileButton.ToolTip = "录制中不可检测，停止录制后再重试";
                }
            }
        }

        private void ThemeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ThemeComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem item && item.Tag != null)
            {
                string t = item.Tag.ToString();
                if (Enum.TryParse<ExpressPackingMonitoring.Themes.AppTheme>(t, out var themeEnum))
                {
                    ExpressPackingMonitoring.Themes.ThemeManager.ApplyTheme(themeEnum);
                }
            }
        }

        /// <summary>
        /// 在独立 STA 线程上运行 DirectShow COM 操作，避免与 AForge 摄像头线程冲突。
        /// </summary>
        private static System.Threading.Tasks.Task<T> RunOnStaThread<T>(Func<T> func)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<T>();
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

        private async System.Threading.Tasks.Task LoadAllDevicesAsync()
        {
            var config = Config;
            var result = await RunOnStaThread(() =>
            {
                var cams = new List<CameraInfo>();
                var micList = new List<MicInfo>();
                var resList = new List<ResOption>();
                var fpsList = new List<int>();

                try
                {
                    var videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                    for (int i = 0; i < videoDevices.Count; i++)
                        cams.Add(new CameraInfo { Index = i, Name = $"[{i}] {videoDevices[i].Name}", Moniker = videoDevices[i].MonikerString });

                    string targetMoniker = config.CameraMonikerString;
                    int targetIndex = -1;
                    if (!string.IsNullOrEmpty(targetMoniker))
                    {
                        for (int i = 0; i < videoDevices.Count; i++)
                        {
                            if (videoDevices[i].MonikerString == targetMoniker)
                            {
                                targetIndex = i;
                                break;
                            }
                        }
                    }

                    if (targetIndex == -1 && config.CameraIndex >= 0 && config.CameraIndex < videoDevices.Count)
                    {
                        targetIndex = config.CameraIndex;
                    }

                    if (targetIndex != -1)
                    {
                        var device = new VideoCaptureDevice(videoDevices[targetIndex].MonikerString);
                        resList = device.VideoCapabilities
                            .Select(c => new { c.FrameSize.Width, c.FrameSize.Height })
                            .Distinct()
                            .OrderByDescending(r => r.Width * r.Height)
                            .Select(r => new ResOption
                            {
                                Name = $"{r.Width}x{r.Height}{GetResLabel(r.Width, r.Height)}",
                                Width = r.Width,
                                Height = r.Height
                            })
                            .ToList();

                        fpsList = device.VideoCapabilities
                            .Select(c => c.AverageFrameRate)
                            .Where(f => f > 0)
                            .Distinct()
                            .OrderBy(f => f)
                            .ToList();
                    }
                }
                catch { }

                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    var audioDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                    for (int i = 0; i < audioDevices.Count; i++)
                        micList.Add(new MicInfo { Name = audioDevices[i].FriendlyName, Moniker = audioDevices[i].ID });
                }
                catch { }

                return (Cameras: cams, Mics: micList, Resolutions: resList, FpsValues: fpsList);
            });

            // 更新摄像头
            var cameras = result.Cameras;
            if (cameras.Count == 0)
                cameras.Add(new CameraInfo { Index = 0, Name = "[0] 未检测到摄像头" });
            cameras.Add(new CameraInfo
            {
                Index = -1,
                Name = AppLanguage.Get("网络摄像头（手动地址）"),
                Moniker = "network:"
            });
            CameraComboBox.ItemsSource = cameras;
            if (string.Equals(config.CameraSourceKind, "network", StringComparison.OrdinalIgnoreCase))
                CameraComboBox.SelectedItem = cameras.FirstOrDefault(IsNetworkCamera);
            else
                CameraComboBox.SelectedValue = config.CameraIndex;

            // 更新麦克风
            var mics = result.Mics;
            if (mics.Count == 0)
                mics.Add(new MicInfo { Name = "未检测到麦克风" });
            MicComboBox.ItemsSource = mics;
            var firstAvailableMic = mics.FirstOrDefault(IsAvailableMic);
            if (string.IsNullOrEmpty(config.AudioDeviceName) && firstAvailableMic != null)
            {
                config.AudioDeviceName = firstAvailableMic.Name;
                config.AudioDeviceMoniker = firstAvailableMic.Moniker ?? "";
            }
            SelectMicByConfig(mics);

            // 更新分辨率
            var resolutions = result.Resolutions;
            if (resolutions.Count == 0)
            {
                resolutions = new List<ResOption>
                {
                    new ResOption { Name = "720P - 省空间", Width = 1280, Height = 720 },
                    new ResOption { Name = "1080P - 高清", Width = 1920, Height = 1080 },
                    new ResOption { Name = "2K - 超清", Width = 2560, Height = 1440 },
                    new ResOption { Name = "4K - 极清", Width = 3840, Height = 2160 }
                };
            }
            ResComboBox.ItemsSource = resolutions;
            var resMatch = resolutions.FirstOrDefault(r => r.Width == config.FrameWidth && r.Height == config.FrameHeight);
            ResComboBox.SelectedItem = resMatch ?? resolutions.FirstOrDefault();

            // 更新帧率
            var fpsValues = result.FpsValues;
            var fpsCbiList = new List<ComboBoxItem>();
            if (fpsValues.Count == 0)
                fpsValues = new List<int> { 10, 15, 20, 25, 30 };
            foreach (var fps in fpsValues)
                fpsCbiList.Add(new ComboBoxItem { Content = $"{fps} FPS", Tag = fps });
            FpsComboBox.ItemsSource = fpsCbiList;
            var fpsMatch = fpsCbiList.FirstOrDefault(i => (int)i.Tag == config.Fps);
            FpsComboBox.SelectedItem = fpsMatch ?? fpsCbiList.FirstOrDefault();
        }

        private async System.Threading.Tasks.Task LoadCameraCapabilitiesAsync(
            int cameraIndex,
            int currentWidth,
            int currentHeight,
            int currentFps,
            bool preferCachedRecommendation = false)
        {
            var result = await RunOnStaThread(() =>
            {
                var resList = new List<ResOption>();
                var fpsList = new List<int>();
                IReadOnlyList<NativeCameraMode> nativeModes = [];
                try
                {
                    var videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                    if (cameraIndex >= 0 && cameraIndex < videoDevices.Count)
                    {
                        var device = new VideoCaptureDevice(videoDevices[cameraIndex].MonikerString);
                        nativeModes = RecordingProfileDetector.GetNativeModes(device.VideoCapabilities);
                        resList = device.VideoCapabilities
                            .Select(c => new { c.FrameSize.Width, c.FrameSize.Height })
                            .Distinct()
                            .OrderByDescending(r => r.Width * r.Height)
                            .Select(r => new ResOption
                            {
                                Name = $"{r.Width}x{r.Height}{GetResLabel(r.Width, r.Height)}",
                                Width = r.Width,
                                Height = r.Height
                            })
                            .ToList();

                        fpsList = device.VideoCapabilities
                            .Select(c => c.AverageFrameRate)
                            .Where(f => f > 0)
                            .Distinct()
                            .OrderBy(f => f)
                            .ToList();
                    }
                }
                catch { }
                return (Resolutions: resList, FpsValues: fpsList, NativeModes: nativeModes);
            });

            if (preferCachedRecommendation
                && TryGetCachedCameraRecommendation(result.NativeModes, out NativeCameraMode recommendedMode))
            {
                currentWidth = recommendedMode.Width;
                currentHeight = recommendedMode.Height;
                currentFps = recommendedMode.Fps;
            }

            var resolutions = result.Resolutions;
            if (resolutions.Count == 0)
            {
                resolutions = new List<ResOption>
                {
                    new ResOption { Name = "720P - 省空间", Width = 1280, Height = 720 },
                    new ResOption { Name = "1080P - 高清", Width = 1920, Height = 1080 },
                    new ResOption { Name = "2K - 超清", Width = 2560, Height = 1440 },
                    new ResOption { Name = "4K - 极清", Width = 3840, Height = 2160 }
                };
            }
            ResComboBox.ItemsSource = resolutions;
            var resMatch = resolutions.FirstOrDefault(r => r.Width == currentWidth && r.Height == currentHeight);
            ResComboBox.SelectedItem = resMatch ?? resolutions.FirstOrDefault();

            var fpsValues = result.FpsValues;
            var fpsCbiList = new List<ComboBoxItem>();
            if (fpsValues.Count == 0)
                fpsValues = new List<int> { 10, 15, 20, 25, 30 };
            foreach (var fps in fpsValues)
                fpsCbiList.Add(new ComboBoxItem { Content = $"{fps} FPS", Tag = fps });
            FpsComboBox.ItemsSource = fpsCbiList;
            var fpsMatch = fpsCbiList.FirstOrDefault(i => (int)i.Tag == currentFps);
            FpsComboBox.SelectedItem = fpsMatch ?? fpsCbiList.FirstOrDefault();
        }

        private static string GetResLabel(int w, int h)
        {
            if (w == 1280 && h == 720) return " (720P)";
            if (w == 1920 && h == 1080) return " (1080P)";
            if (w == 2560 && h == 1440) return " (2K)";
            if (w == 3840 && h == 2160) return " (4K)";
            return "";
        }

        private async void CameraComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CameraComboBox.SelectedItem is CameraInfo selectedCam && IsNetworkCamera(selectedCam))
            {
                ShowNetworkCameraPanelUi();
                return;
            }

            if (_isLoadingDevices) return;
            if (CameraComboBox.SelectedItem is CameraInfo cam)
            {
                NetworkCameraPanel.Visibility = Visibility.Collapsed;
                ResComboBox.IsEnabled = true;
                FpsComboBox.IsEnabled = true;

                // 加载该摄像头的独立配置（如果存在）
                int w = Config.FrameWidth;
                int h = Config.FrameHeight;
                int fps = Config.Fps;

                CameraSettings settings = null;
                bool hasSavedCameraConfig = !string.IsNullOrEmpty(cam.Moniker)
                    && Config.CameraConfigs.TryGetValue(cam.Moniker, out settings);
                if (hasSavedCameraConfig)
                {
                    w = settings.FrameWidth;
                    h = settings.FrameHeight;
                    fps = settings.Fps;
                    Config.AudioDeviceName = settings.AudioDeviceName ?? "";
                    Config.AudioDeviceMoniker = settings.AudioDeviceMoniker ?? "";
                    Config.AudioSyncOffsetMs = settings.AudioSyncOffsetMs;
                    Config.CameraRotate180 = settings.Rotate180;

                    // 切换麦克风 UI 选中项
                    if (MicComboBox.ItemsSource is List<MicInfo> mics)
                    {
                        SelectMicByConfig(mics);
                    }
                }

                await LoadCameraCapabilitiesAsync(
                    cam.Index,
                    w,
                    h,
                    fps,
                    preferCachedRecommendation: !hasSavedCameraConfig);
            }
        }

        private void ShowNetworkCameraPanelUi()
        {
            NetworkCameraPanel.Visibility = Visibility.Visible;
            if (string.IsNullOrWhiteSpace(NetworkCameraUrlTextBox.Text))
                NetworkCameraUrlTextBox.Text = Config.NetworkCameraUrl;

            string networkKey = AppConfig.GetCameraConfigKey("network", Config.NetworkCameraUrl);
            if (!string.IsNullOrWhiteSpace(Config.NetworkCameraUrl)
                && Config.CameraConfigs.TryGetValue(networkKey, out CameraSettings networkSettings))
            {
                Config.AudioDeviceName = networkSettings.AudioDeviceName ?? "";
                Config.AudioDeviceMoniker = networkSettings.AudioDeviceMoniker ?? "";
                Config.AudioSyncOffsetMs = networkSettings.AudioSyncOffsetMs;
                Config.CameraRotate180 = networkSettings.Rotate180;
            }

            ResComboBox.IsEnabled = false;
            FpsComboBox.IsEnabled = false;
            NetworkCameraStatusText.Text = "";
        }

        private static bool IsNetworkCamera(CameraInfo camera)
        {
            return string.Equals(camera?.Moniker, "network:", StringComparison.Ordinal);
        }

        private void NetworkCameraUrlTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            NetworkCameraUrlPlaceholderText.Visibility = string.IsNullOrEmpty(NetworkCameraUrlTextBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private async void NetworkCameraTestButton_Click(object sender, RoutedEventArgs e)
        {
            if (!NetworkCameraUrlPolicy.TryNormalize(
                    NetworkCameraUrlTextBox.Text,
                    out string url,
                    out string error))
            {
                NetworkCameraStatusText.Text = $"地址无效：{error}";
                return;
            }

            NetworkCameraTestButton.IsEnabled = false;
            NetworkCameraStatusText.Text = "正在连接网络摄像头...";
            try
            {
                using var probeSource = new NetworkCameraSource(
                    url,
                    AppConfig.NormalizeNetworkTransport(Config.NetworkCameraRtspTransport),
                    Config.Fps > 0 ? Config.Fps : 15);
                bool connected = await probeSource.StartAsync();
                NetworkCameraStatusText.Text = connected
                    ? $"连接成功：{probeSource.ActualWidth}×{probeSource.ActualHeight} @ {probeSource.ActualFps} FPS"
                    : $"连接失败：{probeSource.LastError ?? "无法获取画面信息"}";
            }
            finally
            {
                NetworkCameraTestButton.IsEnabled = true;
            }
        }

        private bool TryGetCachedCameraRecommendation(
            IReadOnlyList<NativeCameraMode> nativeModes,
            out NativeCameraMode recommendedMode)
        {
            string codec = VideoCodecComboBox.SelectedItem is GpuEncoderOption codecOption
                ? codecOption.Value
                : Config.VideoCodec ?? "h264";
            codec = codec.Trim().ToLowerInvariant();
            if (codec is not ("h264" or "h265" or "av1"))
                codec = "h264";
            string gpu = GpuEncoderComboBox.SelectedItem is GpuEncoderOption gpuOption
                ? gpuOption.Value
                : Config.GpuEncoder ?? "auto";
            if (!EncodingHelper.TryResolveEncoder(
                gpu,
                codec,
                MainViewModel.ValidatedEncoders ?? new HashSet<string>(),
                MainViewModel.EncoderPerformanceScores,
                out string encoder))
            {
                recommendedMode = default;
                return false;
            }
            return RecordingProfileDetector.TryRecommendFromCache(
                Config,
                encoder,
                RecordingProfileDetector.NormalizeVideoCqp(Config.VideoCqp),
                nativeModes,
                out recommendedMode);
        }

        private void LoadGpuEncoders()
        {
            var validated = MainViewModel.ValidatedEncoders ?? new HashSet<string>();
            var scores = MainViewModel.EncoderPerformanceScores ?? new Dictionary<string, double>();
            bool HasScoredHardware(string gpu) => EncodingHelper.HardwareEncodersForCodec("h264")
                .Concat(EncodingHelper.HardwareEncodersForCodec("h265"))
                .Concat(EncodingHelper.HardwareEncodersForCodec("av1"))
                .Where(encoder => EncodingHelper.NormalizeGpuSetting(encoder) == gpu)
                .Any(encoder => validated.Contains(encoder)
                    && scores.TryGetValue(encoder, out double score)
                    && score > 0);
            var available = MainViewModel.CachedEncoderOptions ?? [];
            var encoders = available
                .Where(option => option.Value == "auto"
                    ? scores.Any(pair => pair.Value > 0
                        && EncodingHelper.HardwareEncodersForCodec("h264").Concat(
                            EncodingHelper.HardwareEncodersForCodec("h265")).Concat(
                            EncodingHelper.HardwareEncodersForCodec("av1")).Contains(pair.Key))
                    : option.Value == "cpu"
                        ? validated.Any(encoder => encoder is "libx264" or "libx265" or "libsvtav1")
                        : HasScoredHardware(option.Value))
                .ToList();
            GpuEncoderComboBox.ItemsSource = encoders;
            string normalized = NormalizeGpuSetting(Config.GpuEncoder ?? "auto");
            var match = encoders.FirstOrDefault(e => e.Value == normalized)
                     ?? encoders.FirstOrDefault();
            GpuEncoderComboBox.SelectedItem = match;
        }

        private void LoadVideoCodecs()
        {
            string gpu = GpuEncoderComboBox.SelectedItem is GpuEncoderOption selectedGpu
                ? selectedGpu.Value
                : NormalizeGpuSetting(Config.GpuEncoder ?? "auto");
            var validated = MainViewModel.ValidatedEncoders ?? new HashSet<string>();
            var scores = MainViewModel.EncoderPerformanceScores ?? new Dictionary<string, double>();
            bool Available(string codec) => EncodingHelper.IsExactSelectionAvailable(
                gpu, codec, validated, scores);
            var items = new List<GpuEncoderOption>();
            if (Available("h264")) items.Add(new GpuEncoderOption { Value = "h264", DisplayName = "H.264 (兼容性好)" });
            if (Available("h265")) items.Add(new GpuEncoderOption { Value = "h265", DisplayName = "H.265 / HEVC (体积更小)" });
            if (Available("av1")) items.Add(new GpuEncoderOption { Value = "av1", DisplayName = "AV1 (极致压缩)" });
            VideoCodecComboBox.ItemsSource = items;
            string current = Config.VideoCodec?.ToLowerInvariant() ?? "h264";
            VideoCodecComboBox.SelectedItem = items.FirstOrDefault(i => i.Value == current)
                ?? items.FirstOrDefault(i => i.Value == "h265")
                ?? items.FirstOrDefault();
        }

        private void GpuEncoderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VideoCodecComboBox != null)
                LoadVideoCodecs();
        }

        private static string NormalizeGpuSetting(string setting) => EncodingHelper.NormalizeGpuSetting(setting);

        private void BtnBrowsePath_Click(object sender, RoutedEventArgs e)
        {
            EnsurePrimaryStorageLocationExists();
            var primary = Config.StorageLocations[0];

            string selectedPath = SelectDefaultStoragePathFromDrive();
            if (string.IsNullOrWhiteSpace(selectedPath)) return;

            if (!TryPrepareStoragePath(selectedPath, out string errorMessage))
            {
                AppDialog.Error(this, $"无法创建或写入目录：\n{selectedPath}\n\n原因：{errorMessage}", "存储错误");
                return;
            }
            if (!TryConfirmLocalStoragePath(selectedPath, out string localError))
            {
                AppDialog.Error(this, localError, "存储位置无效");
                return;
            }

            primary.Path = selectedPath;
        }

        private void BtnBrowseRecordingCache_Click(object sender, RoutedEventArgs e)
        {
            if (_isRecording)
            {
                AppDialog.Warning(
                    this,
                    "请先结束当前录像，再更改本地缓存位置",
                    "正在录像");
                return;
            }

            EnsureRecordingCacheLocationExists();
            var dialog = new DriveSelectionDialog(
                Array.Empty<string>(),
                fixedDrivesOnly: true)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true
                || string.IsNullOrWhiteSpace(dialog.SelectedRootPath))
            {
                return;
            }

            bool isSystemDrive =
                StorageSpacePolicy.IsSystemDrive(dialog.SelectedRootPath);
            string selectedPath =
                RecordingWorkstationCachePolicy.GetSuggestedPath(
                    dialog.SelectedRootPath,
                    isSystemDrive);
            if (!TryPrepareStoragePath(selectedPath, out string errorMessage))
            {
                AppDialog.Error(
                    this,
                    $"无法使用这个缓存位置：\n{selectedPath}\n\n{errorMessage}",
                    "更改缓存位置");
                return;
            }

            StorageLocation location =
                RecordingWorkstationCachePolicy.GetConfiguredLocation(Config)!;
            location.Path = selectedPath;
            location.ReserveGB =
                StorageSpacePolicy.GetMinimumReserveGB(selectedPath);
            location.Priority = 0;
            _recordingCacheLimitExplained = false;
            RefreshRecordingCacheStorageSummary();
        }

        private void RecordingCacheLimitEditor_ValueChanged(
            object sender,
            RoutedPropertyChangedEventArgs<object> e)
        {
            _recordingCacheLimitExplained = false;
            RefreshRecordingCacheStorageSummary();
        }

        private void RefreshRecordingCacheStorageSummary()
        {
            if (!Capabilities.CanConfigureRecordingCache
                || RecordingCacheUsageProgress == null
                || RecordingCacheUsageText == null
                || RecordingCacheSafeCapacityText == null
                || RecordingCacheDriveHintText == null)
            {
                return;
            }

            if (!TryGetRecordingCacheSnapshot(
                    out RecordingCacheSpaceSnapshot snapshot,
                    out string error))
            {
                RecordingCacheUsageProgress.Value = 100;
                RecordingCacheUsageText.Text = "本地缓存位置不可用";
                RecordingCacheSafeCapacityText.Text = error;
                RecordingCacheDriveHintText.Text =
                    "请选择健康、可写的本机固定磁盘";
                return;
            }

            RecordingCacheUsageProgress.Value = snapshot.UsagePercent;
            RecordingCacheUsageText.Text =
                $"已缓存 {FormatGb(snapshot.CacheBytes)} / 上限 {Config.RecordingCacheMaxGB} GB";
            RecordingCacheSafeCapacityText.Text =
                $"此磁盘当前建议最多 {FormatGb(snapshot.EffectiveLimitBytes)}，实际使用会随磁盘剩余空间动态调整";
            StorageLocation location =
                RecordingWorkstationCachePolicy.GetConfiguredLocation(Config)!;
            string root = Path.GetPathRoot(Path.GetFullPath(location.Path)) ?? "";
            RecordingCacheDriveHintText.Text =
                StorageSpacePolicy.IsSystemDrive(root)
                    ? $"当前使用系统盘，系统会保留至少 {FormatGb(snapshot.ReserveBytes)}，不会占满磁盘"
                    : $"系统会为此磁盘保留至少 {FormatGb(snapshot.ReserveBytes)}";
        }

        private bool TryGetRecordingCacheSnapshot(
            out RecordingCacheSpaceSnapshot snapshot,
            out string error)
        {
            snapshot = default;
            error = "";
            try
            {
                StorageLocation location =
                    RecordingWorkstationCachePolicy.GetConfiguredLocation(Config)
                    ?? throw new IOException("尚未设置本地缓存位置");
                string path = Path.GetFullPath(location.Path);
                if (!Directory.Exists(path))
                    throw new DirectoryNotFoundException("本地缓存位置不存在，请重新选择");
                string root = Path.GetPathRoot(path)
                    ?? throw new IOException("无法确定本地缓存所在磁盘");
                var drive = new DriveInfo(root);
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                    throw new IOException("请选择已连接的本机固定磁盘");
                long cacheBytes = Directory
                    .EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                    .Where(file =>
                        file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                        || file.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
                    .Sum(file =>
                    {
                        try { return new FileInfo(file).Length; }
                        catch { return 0L; }
                    });
                long reserveBytes =
                    StorageSpacePolicy.GetEffectiveReserveBytes(location, drive);
                snapshot = RecordingWorkstationCachePolicy.CalculateSpace(
                    cacheBytes,
                    Math.Max(1L, Config.RecordingCacheMaxGB)
                    * StorageSpacePolicy.BytesPerGiB,
                    drive.AvailableFreeSpace,
                    reserveBytes);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string FormatGb(long bytes) =>
            $"{Math.Max(0, bytes) / (double)StorageSpacePolicy.BytesPerGiB:F1} GB";

        private bool ValidateRecordingCacheSettings()
        {
            Config.RecordingCachePolicy = "KeepWithinSize";
            if (!TryGetRecordingCacheSnapshot(
                    out RecordingCacheSpaceSnapshot snapshot,
                    out string error))
            {
                AppDialog.Error(
                    this,
                    error,
                    "本地缓存位置不可用");
                return false;
            }

            if (snapshot.EffectiveLimitBytes
                < RecordingWorkstationCachePolicy
                    .RecordingAndPackagingHeadroomBytes)
            {
                AppDialog.Warning(
                    this,
                    "此磁盘当前安全可用空间不足以容纳一段录像及封装临时文件，请选择其他缓存位置",
                    "本地缓存空间不足");
                return false;
            }

            if (!_recordingCacheLimitExplained
                && snapshot.ConfiguredLimitBytes > snapshot.EffectiveLimitBytes)
            {
                _recordingCacheLimitExplained = true;
                AppDialog.Information(
                    this,
                    $"缓存上限设置为 {Config.RecordingCacheMaxGB} GB；此磁盘当前建议最多 {FormatGb(snapshot.EffectiveLimitBytes)}。系统会自动采用较小值，不会预占或强占磁盘空间",
                    "已按磁盘安全空间调整");
            }

            return true;
        }

        private bool IsPathWritable(string path)
        {
            return TryPrepareStoragePath(path, out _);
        }

        private void BtnAddStorage_Click(object sender, RoutedEventArgs e)
        {
            string selectedPath = SelectDefaultStoragePathFromDrive();
            if (string.IsNullOrWhiteSpace(selectedPath)) return;

            if (Config.StorageLocations.Any(x => string.Equals(x.Path, selectedPath, StringComparison.OrdinalIgnoreCase)))
            {
                AppDialog.Information(this, "该路径已在列表中", "提示");
                return;
            }

            string selectedRoot = GetStorageRoot(selectedPath);
            StorageLocation sameDisk = Config.StorageLocations.FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(x.Path) &&
                string.Equals(GetStorageRoot(x.Path), selectedRoot, StringComparison.OrdinalIgnoreCase));
            if (sameDisk != null)
            {
                AppDialog.Information(
                    this,
                    $"同一个磁盘已经添加过：\n{sameDisk.Path}\n\n请换一个磁盘，或直接调整已有路径的容量和列表顺序",
                    "磁盘已存在");
                return;
            }

            if (!TryPrepareStoragePath(selectedPath, out string errorMessage))
            {
                AppDialog.Error(this, $"无法创建或写入目录：\n{selectedPath}\n\n原因：{errorMessage}", "存储错误");
                return;
            }
            if (!TryConfirmLocalStoragePath(selectedPath, out string localError))
            {
                AppDialog.Error(this, localError, "存储位置无效");
                return;
            }

            var newLocation = new StorageLocation
            {
                Path = selectedPath,
                ReserveGB = StorageSpacePolicy.GetMinimumReserveGB(selectedPath),
                Priority = Config.StorageLocations.Count
            };
            Config.StorageLocations.Add(newLocation);

            RefreshStoragePriorities();
            RefreshStorageViews();
            StorageDataGrid.SelectedItem = newLocation;
            UpdateStorageButtonStates();
        }

        private void BtnAddNetworkStorage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new StoragePathSelectionDialog(
                title: "添加备份位置",
                hint: "录像会异步备份到此位置；可输入网络共享路径（如 \\\\192.168.1.100\\共享目录\\快递打包视频），也可选择网盘挂载成的本地磁盘（如 Z:\\快递打包视频）")
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.SelectedPath))
                return;

            string selectedPath = dialog.SelectedPath;
            BackupStorageDecision decision =
                BackupStorageLocationPolicy.Evaluate(selectedPath);
            if (decision == BackupStorageDecision.Accept)
            {
                if (StorageVolumeInfo.TryResolveUncPath(
                        selectedPath,
                        out string uncPath))
                {
                    selectedPath = uncPath;
                }
            }
            else if (decision == BackupStorageDecision.ConfirmVirtualDisk)
            {
                if (!AppDialog.Confirm(
                        this,
                        $"网盘挂载盘不建议作为备份位置，网盘客户端异常时归档会自动暂停\n\n{selectedPath}\n\n确定仍要添加吗？",
                        "备份位置确认",
                        AppDialogSeverity.Warning))
                {
                    return;
                }
            }
            else if (decision == BackupStorageDecision.ConfirmUnknown)
            {
                if (!AppDialog.Confirm(
                        this,
                        $"无法确认该路径是否为网盘挂载盘；网盘挂载盘不建议作为备份位置\n\n{selectedPath}\n\n确定仍要添加吗？",
                        "备份位置确认",
                        AppDialogSeverity.Warning))
                {
                    return;
                }
            }
            else
            {
                AppDialog.Error(
                    this,
                    "备份位置必须是网络共享路径或网盘挂载盘，例如 \\\\192.168.1.100\\共享目录\\快递打包视频；本地磁盘请添加到录像保存位置",
                    "备份位置无效");
                return;
            }
            if (Config.StorageLocations.Any(x => string.Equals(x.Path, selectedPath, StringComparison.OrdinalIgnoreCase)))
            {
                AppDialog.Information(this, "该路径已在列表中。", "提示");
                return;
            }

            if (StorageVolumeInfo.TryGetNetworkShareIdentity(
                    selectedPath,
                    out string selectedIdentity))
            {
                StorageLocation sameShare = Config.StorageLocations.FirstOrDefault(location =>
                    !string.IsNullOrWhiteSpace(location.Path)
                    && StorageVolumeInfo.TryGetNetworkShareIdentity(
                        location.Path,
                        out string existingIdentity)
                    && string.Equals(
                        selectedIdentity,
                        existingIdentity,
                        StringComparison.OrdinalIgnoreCase));
                if (sameShare != null)
                {
                    AppDialog.Information(
                        this,
                        $"该位置与已添加的网络位置属于同一磁盘/共享：\n{sameShare.Path}\n\n请换一个共享，或直接调整已有路径的容量和列表顺序。",
                        "网络位置已存在");
                    return;
                }
            }

            string selectedRoot = GetStorageRoot(selectedPath);
            StorageLocation sameDisk = Config.StorageLocations.FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(x.Path) &&
                string.Equals(GetStorageRoot(x.Path), selectedRoot, StringComparison.OrdinalIgnoreCase));
            if (sameDisk != null)
            {
                AppDialog.Information(
                    this,
                    $"同一个磁盘已经添加过：\n{sameDisk.Path}\n\n请换一个磁盘，或直接调整已有路径的容量和列表顺序。",
                    "磁盘已存在");
                return;
            }

            if (!TryPrepareStoragePath(selectedPath, out string errorMessage))
            {
                AppDialog.Error(this, $"无法创建或写入目录：\n{selectedPath}\n\n原因：{errorMessage}", "存储错误");
                return;
            }

            var location = new StorageLocation
            {
                Path = selectedPath,
                ReserveGB = StorageSpacePolicy.GetMinimumReserveGB(selectedPath),
                Priority = Config.StorageLocations.Count,
                IsBackupTarget = true
            };
            StorageLocationMetadata.RefreshVolumeId(location);
            Config.StorageLocations.Add(location);

            RefreshStoragePriorities();
            RefreshStorageViews();
            BackupStorageDataGrid.SelectedItem = location;
            UpdateStorageButtonStates();
            UpdateBackupStorageButtonStates();
        }

        private bool _manualCleanupRunning;

        private async void BtnManualCleanupByTime_Click(object sender, RoutedEventArgs e)
        {
            if (_manualCleanupRunning || Context.RunManualCleanupAsync == null)
                return;
            var dialog = new ManualCleanupDialog(ManualCleanupKind.ByTime) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedOptions == null)
                return;
            await RunManualCleanupAsync(dialog.SelectedOptions);
        }

        private async void BtnManualCleanupBySpace_Click(object sender, RoutedEventArgs e)
        {
            if (_manualCleanupRunning || Context.RunManualCleanupAsync == null)
                return;
            var dialog = new ManualCleanupDialog(ManualCleanupKind.BySpace) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedOptions == null)
                return;
            await RunManualCleanupAsync(dialog.SelectedOptions);
        }

        private async Task RunManualCleanupAsync(ManualCleanupOptions options)
        {
            if (_manualCleanupRunning
                || Context.PreviewManualCleanupAsync == null
                || Context.RunManualCleanupAsync == null)
            {
                return;
            }
            _manualCleanupRunning = true;
            UpdateManualCleanupButtons();
            try
            {
                ManualCleanupPreview preview =
                    await Context.PreviewManualCleanupAsync(options);
                if (preview.Count <= 0)
                {
                    AppDialog.Information(this, "没有符合条件的本地录像需要清理", "录像清理");
                    return;
                }
                if (options.Kind == ManualCleanupKind.BySpace
                    && options.TargetBytes > 0
                    && options.TargetBytes > preview.Bytes)
                {
                    AppDialog.Warning(
                        this,
                        $"输入的空间大于当前可清理总量（约 {FormatManualCleanupBytes(preview.Bytes)}），请调整后再试",
                        "录像清理");
                    return;
                }

                string confirmText =
                    $"将清理约 {preview.Count} 条本地录像（约 {FormatManualCleanupBytes(preview.Bytes)}）。仅清理电脑本地录像，NAS 中已备份的文件不会受到影响";
                if (preview.UnarchivedCount > 0)
                {
                    confirmText +=
                        $"\n其中 {preview.UnarchivedCount} 条尚未备份到 NAS，是否需要继续清理会在执行时再次询问";
                }
                if (!AppDialog.Confirm(
                        this,
                        confirmText,
                        "录像清理",
                        AppDialogSeverity.Warning,
                        confirmText: "开始清理",
                        cancelText: "取消",
                        isDangerous: true))
                {
                    return;
                }

                ManualCleanupResult result = await Context.RunManualCleanupAsync(
                    options,
                    prompt =>
                    {
                        bool confirmed = false;
                        Dispatcher.Invoke(() =>
                        {
                            confirmed = AppDialog.Confirm(
                                this,
                                $"有 {prompt.UnarchivedCount} 条录像尚未备份到 NAS，继续清理可能导致录像无法恢复。是否继续清理未备份的本地录像？",
                                "未备份录像",
                                AppDialogSeverity.Warning,
                                confirmText: "继续清理",
                                cancelText: "取消",
                                isDangerous: true);
                        });
                        return confirmed;
                    });

                string toast =
                    $"已清理 {result.CleanedCount} 条本地录像，释放 {FormatManualCleanupBytes(result.CleanedBytes)}";
                if (result.RepairedCount > 0)
                    toast += $"，修复 {result.RepairedCount} 条缺失文件记录";
                if (result.SkippedCount > 0)
                    toast += $"，跳过 {result.SkippedCount} 条";
                if (result.UnarchivedRemainingCount > 0)
                    toast += $"，仍有 {result.UnarchivedRemainingCount} 条未备份录像未处理";
                Context.ShowToast?.Invoke(toast, ToastSeverity.Information);
            }
            catch (Exception ex)
            {
                AppDialog.Error(this, ex.Message, "清理失败");
            }
            finally
            {
                _manualCleanupRunning = false;
                UpdateManualCleanupButtons();
            }
        }

        private void UpdateManualCleanupButtons()
        {
            if (BtnManualCleanupByTime == null || BtnManualCleanupBySpace == null)
                return;
            BtnManualCleanupByTime.IsEnabled = !_manualCleanupRunning;
            BtnManualCleanupBySpace.IsEnabled = !_manualCleanupRunning;
        }

        private static string FormatManualCleanupBytes(long bytes)
        {
            if (bytes <= 0)
                return "0 GB";
            return $"{bytes / (double)StorageSpacePolicy.BytesPerGiB:F1} GB";
        }

        private string SelectDefaultStoragePathFromDrive()
        {
            var dialog = new DriveSelectionDialog(Config.StorageLocations.Select(location => location.Path))
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.SelectedRootPath))
                return "";

            return Path.Combine(dialog.SelectedRootPath, "快递打包视频");
        }

        private bool TryPrepareStoragePath(string path, out string errorMessage)
        {
            try
            {
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                string testFile = Path.Combine(path, ".write_test_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                errorMessage = "";
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private void BtnRemoveStorage_Click(object sender, RoutedEventArgs e)
        {
            if (StorageDataGrid.SelectedItem is StorageLocation selected)
            {
                int localCount = Config.StorageLocations.Count(
                    location => StorageVolumeInfo.IsConfirmedLocal(location.Path));
                if (localCount <= 1)
                {
                    AppDialog.Warning(this, "至少需要保留一个存储路径", "警告");
                    return;
                }

                bool shouldRemove = AppDialog.Confirm(
                    this,
                    $"确定要移除路径: {selected.Path} 吗？\n注意：此操作不会删除物理文件，但系统将不再管理该目录",
                    "确认移除",
                    AppDialogSeverity.Warning,
                    confirmText: "移除",
                    cancelText: "取消",
                    isDangerous: true);
                if (shouldRemove)
                {
                    bool keepsLocalPath = Config.StorageLocations
                        .Where(location => !ReferenceEquals(location, selected))
                        .Any(location => StorageVolumeInfo.IsConfirmedLocal(location.Path));
                    if (!keepsLocalPath)
                    {
                        AppDialog.Warning(this, "至少需要一个本地保存位置，请先添加本地磁盘", "警告");
                        return;
                    }

                    int selectedIndex = StorageDataGrid.SelectedIndex;
                    Config.StorageLocations.Remove(selected);
                    RefreshStoragePriorities();
                    RefreshStorageViews();
                    int remainingLocalCount = Config.StorageLocations.Count(
                        location => StorageVolumeInfo.IsConfirmedLocal(location.Path));
                    if (remainingLocalCount > 0)
                    {
                        StorageDataGrid.SelectedIndex = Math.Min(
                            selectedIndex,
                            remainingLocalCount - 1);
                    }
                    UpdateStorageButtonStates();
                }
            }
            else
            {
                AppDialog.Warning(this, "请先在列表中选中要移除的行", "提示");
            }
        }

        private void StorageDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateStorageButtonStates();
        }

        private void StorageReserveEditor_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: StorageLocation location })
            {
                location.EffectiveReserveGB = location.EffectiveReserveGB;
                RefreshStorageViews();
            }
        }

        private void BtnMoveStorageUp_Click(object sender, RoutedEventArgs e)
        {
            MoveSelectedStorage(-1);
        }

        private void BtnMoveStorageDown_Click(object sender, RoutedEventArgs e)
        {
            MoveSelectedStorage(1);
        }

        private void MoveSelectedStorage(int direction)
        {
            if (StorageDataGrid?.SelectedItem is not StorageLocation selected) return;

            var locals = Config.StorageLocations
                .Where(location => StorageVolumeInfo.IsConfirmedLocal(location.Path))
                .ToList();
            int localIndex = locals.IndexOf(selected);
            int newLocalIndex = localIndex + direction;
            if (localIndex < 0 || newLocalIndex < 0 || newLocalIndex >= locals.Count) return;

            int from = Config.StorageLocations.IndexOf(selected);
            int to = Config.StorageLocations.IndexOf(locals[newLocalIndex]);
            if (from < 0 || to < 0 || from == to) return;

            Config.StorageLocations.RemoveAt(from);
            Config.StorageLocations.Insert(to, selected);
            RefreshStoragePriorities();
            RefreshStorageViews();
            StorageDataGrid.SelectedItem = selected;
            UpdateStorageButtonStates();
        }

        private void SortStorageLocationsByPriority()
        {
            if (Config.StorageLocations == null || Config.StorageLocations.Count <= 1) return;

            var ordered = Config.StorageLocations
                .Select((location, index) => new { Location = location, Index = index })
                .OrderBy(x => x.Location.Priority)
                .ThenBy(x => x.Index)
                .Select(x => x.Location)
                .ToList();

            Config.StorageLocations.Clear();
            Config.StorageLocations.AddRange(ordered);
        }

        private void RefreshStoragePriorities()
        {
            if (Config.StorageLocations == null) return;

            for (int i = 0; i < Config.StorageLocations.Count; i++)
            {
                Config.StorageLocations[i].Priority = i;
            }
        }

        private void UpdateStorageButtonStates()
        {
            if (RemoveStorageButton == null) return;

            bool hasSelection = StorageDataGrid?.SelectedItem is StorageLocation;
            int selectedIndex = StorageDataGrid?.SelectedIndex ?? -1;
            int localCount = Config.StorageLocations?
                .Count(location =>
                    StorageVolumeInfo.IsConfirmedLocal(location.Path)
                    && !StorageLocationResolver.IsBackupLocation(location)) ?? 0;

            RemoveStorageButton.IsEnabled = hasSelection;
            if (MoveStorageUpButton != null) MoveStorageUpButton.IsEnabled = hasSelection && selectedIndex > 0;
            if (MoveStorageDownButton != null)
            {
                MoveStorageDownButton.IsEnabled =
                    hasSelection && selectedIndex >= 0 && selectedIndex < localCount - 1;
            }
        }

        private void LocalStorageView_Filter(object sender, FilterEventArgs e)
        {
            e.Accepted = e.Item is StorageLocation location
                && !string.IsNullOrWhiteSpace(location.Path)
                && StorageVolumeInfo.IsConfirmedLocal(location.Path)
                && !StorageLocationResolver.IsBackupLocation(location);
        }

        private void BackupStorageView_Filter(object sender, FilterEventArgs e)
        {
            e.Accepted = e.Item is StorageLocation location
                && !string.IsNullOrWhiteSpace(location.Path)
                && StorageLocationResolver.IsBackupLocation(location);
        }

        private void RefreshStorageViews()
        {
            _localStorageView?.View.Refresh();
            _backupStorageView?.View.Refresh();
            if (BackupEmptyHint != null)
            {
                int networkCount = Config.StorageLocations?
                    .Count(StorageLocationResolver.IsBackupLocation) ?? 0;
                BackupEmptyHint.Visibility = networkCount == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private void BackupStorageDataGrid_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            UpdateBackupStorageButtonStates();
        }

        private void UpdateBackupStorageButtonStates()
        {
            if (BackupRemoveButton == null)
                return;
            bool hasSelection = BackupStorageDataGrid?.SelectedItem is StorageLocation;
            int selectedIndex = BackupStorageDataGrid?.SelectedIndex ?? -1;
            int networkCount = Config.StorageLocations?
                .Count(StorageLocationResolver.IsBackupLocation) ?? 0;

            BackupRemoveButton.IsEnabled = hasSelection;
            if (BackupMoveUpButton != null)
                BackupMoveUpButton.IsEnabled = hasSelection && selectedIndex > 0;
            if (BackupMoveDownButton != null)
            {
                BackupMoveDownButton.IsEnabled =
                    hasSelection && selectedIndex >= 0 && selectedIndex < networkCount - 1;
            }
        }

        private void BtnBackupMoveUp_Click(object sender, RoutedEventArgs e)
        {
            MoveSelectedBackupStorage(-1);
        }

        private void BtnBackupMoveDown_Click(object sender, RoutedEventArgs e)
        {
            MoveSelectedBackupStorage(1);
        }

        private void MoveSelectedBackupStorage(int direction)
        {
            if (BackupStorageDataGrid?.SelectedItem is not StorageLocation selected)
                return;

            var networks = Config.StorageLocations
                .Where(StorageLocationResolver.IsBackupLocation)
                .ToList();
            int networkIndex = networks.IndexOf(selected);
            int newNetworkIndex = networkIndex + direction;
            if (networkIndex < 0
                || newNetworkIndex < 0
                || newNetworkIndex >= networks.Count)
            {
                return;
            }

            int from = Config.StorageLocations.IndexOf(selected);
            int to = Config.StorageLocations.IndexOf(networks[newNetworkIndex]);
            if (from < 0 || to < 0 || from == to)
                return;

            Config.StorageLocations.RemoveAt(from);
            Config.StorageLocations.Insert(to, selected);
            RefreshStoragePriorities();
            RefreshStorageViews();
            BackupStorageDataGrid.SelectedItem = selected;
            UpdateBackupStorageButtonStates();
        }

        private void BtnRemoveBackupStorage_Click(object sender, RoutedEventArgs e)
        {
            if (BackupStorageDataGrid.SelectedItem is not StorageLocation selected)
            {
                AppDialog.Warning(this, "请先在列表中选中要移除的行", "提示");
                return;
            }

            bool shouldRemove = AppDialog.Confirm(
                this,
                $"确定要移除备份位置: {selected.Path} 吗？\n注意：此操作不会删除 NAS 上已备份的文件，程序也不再向该位置备份新录像",
                "确认移除",
                AppDialogSeverity.Warning,
                confirmText: "移除",
                cancelText: "取消",
                isDangerous: true);
            if (!shouldRemove)
                return;

            Config.StorageLocations.Remove(selected);
            RefreshStoragePriorities();
            RefreshStorageViews();
            BackupStorageDataGrid.SelectedItem = null;
            UpdateBackupStorageButtonStates();
        }

        private async void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (await SaveAndApplyAsync())
            {
                if (IsLoaded)
                    Close();
            }
        }

        private async void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            await SaveAndApplyAsync();
        }

        private async Task<bool> SaveAndApplyAsync()
        {
            Keyboard.ClearFocus();
            if (!string.Equals(Config.WebAccessKey, _originalWebAccessKey, StringComparison.Ordinal))
            {
                bool shouldChangeAccessKey = AppDialog.Confirm(
                    this,
                    "修改录像网页访问密钥后，所有包含旧密钥的访问链接都会立即失效，其他手机和电脑需要重新获取新链接\n\n是否确认修改并继续保存？",
                    "确认修改访问密钥",
                    AppDialogSeverity.Warning,
                    confirmText: "确认修改",
                    cancelText: "不修改",
                    isDangerous: true);
                if (!shouldChangeAccessKey)
                {
                    Config.WebAccessKey = _originalWebAccessKey;
                    WebAccessKeyTextBox?.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
                    return false;
                }
            }
            if (Capabilities.CanRecordAudio)
                SyncSelectedMicToConfig();

            if (Capabilities.CanUseCamera && !ValidateCameraIdleNoSleepPeriods())
                return false;

            if (Capabilities.CanRecordPcVideo && !ConfirmCachedRecordingProfileRisk())
                return false;

            if (Capabilities.CanRecordPcVideo)
            {
                if (!TryNormalizeComputerNickname(Config.NodeName, out string normalizedNickname))
                {
                    AppDialog.Warning(
                        this,
                        "电脑昵称需要填写 1 到 20 个字符，不能包含换行或其他控制字符",
                        "电脑昵称不正确");
                    ComputerNicknameTextBox?.Focus();
                    return false;
                }
                Config.NodeName = normalizedNickname;
                if (!string.Equals(Config.NodeName, _originalNodeName, StringComparison.Ordinal))
                    Config.NodeNameCustomized = true;
            }

            if (Capabilities.CanConfigureRecordingCache
                && !ValidateRecordingCacheSettings())
            {
                return false;
            }

            // 0. 验证音频
            if (Capabilities.CanRecordAudio &&
                Config.EnableAudioRecording &&
                string.IsNullOrEmpty(Config.AudioDeviceName))
            {
                bool shouldContinue = AppDialog.Confirm(
                    this,
                    "已开启录制声音，但未选择麦克风。录制可能会失败或没有声音。\n\n是否继续保存？",
                    "音频提醒",
                    AppDialogSeverity.Warning,
                    confirmText: "继续保存",
                    cancelText: "返回设置",
                    isDangerous: false);
                if (!shouldContinue) return false;
            }

            // 1. 强制提交 DataGrid 中的未完成编辑
            if (Capabilities.CanConfigureStorage)
            {
                StorageDataGrid.CommitEdit(DataGridEditingUnit.Row, true);
                StorageDataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
                RefreshStoragePriorities();
            }

            // 2. 手动同步部分控件（防止可焦点未切换时绑定未更新）
            if (Capabilities.CanUseCamera && CameraComboBox.SelectedItem is CameraInfo cam)
            {
                if (IsNetworkCamera(cam))
                {
                    if (!NetworkCameraUrlPolicy.TryNormalize(
                            NetworkCameraUrlTextBox.Text,
                            out string networkUrl,
                            out string networkError))
                    {
                        Context.ShowToast?.Invoke($"网络摄像头地址无效：{networkError}", ToastSeverity.Error);
                        return false;
                    }

                    Config.CameraSourceKind = "network";
                    Config.NetworkCameraUrl = networkUrl;
                    Config.CameraMonikerString = "";
                    Config.CameraIndex = -1;
                    Config.CameraConfigs[AppConfig.GetCameraConfigKey("network", networkUrl)] = new CameraSettings
                    {
                        FrameWidth = Config.FrameWidth,
                        FrameHeight = Config.FrameHeight,
                        Fps = Config.Fps,
                        AudioDeviceName = Config.AudioDeviceName,
                        AudioDeviceMoniker = Config.AudioDeviceMoniker,
                        AudioSyncOffsetMs = Config.AudioSyncOffsetMs,
                        Rotate180 = Config.CameraRotate180
                    };
                }
                else
                {
                    Config.CameraSourceKind = "usb";
                    Config.CameraMonikerString = cam.Moniker;
                    Config.CameraIndex = cam.Index;

                    if (ResComboBox.SelectedItem is ResOption selectedRes)
                    {
                        Config.FrameWidth = selectedRes.Width;
                        Config.FrameHeight = selectedRes.Height;
                    }

                    if (FpsComboBox.SelectedItem is ComboBoxItem fpsItem && fpsItem.Tag is int fps)
                    {
                        Config.Fps = fps;
                    }

                    // 更新此摄像头的独立配置
                    if (!string.IsNullOrEmpty(cam.Moniker))
                    {
                        Config.CameraConfigs[cam.Moniker] = new CameraSettings
                        {
                            FrameWidth = Config.FrameWidth,
                            FrameHeight = Config.FrameHeight,
                            Fps = Config.Fps,
                            AudioDeviceName = Config.AudioDeviceName,
                            AudioDeviceMoniker = Config.AudioDeviceMoniker,
                            AudioSyncOffsetMs = Config.AudioSyncOffsetMs,
                            Rotate180 = Config.CameraRotate180
                        };
                    }
                }
            }

            if (Capabilities.CanRecordPcVideo && GpuEncoderComboBox.SelectedItem is GpuEncoderOption gpuOpt)
            {
                Config.GpuEncoder = gpuOpt.Value;
            }

            if (Capabilities.CanRecordPcVideo && VideoCodecComboBox.SelectedItem is GpuEncoderOption codecOpt)
            {
                Config.VideoCodec = codecOpt.Value;
            }

            // 保存断句关键词
            if (Capabilities.IsRecordingDevice)
            {
                Config.TtsBreakWords = TtsBreakWordsTextBox.Text
                    .Split(new[] { '\r', '\n', '，', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.Trim())
                    .Where(w => w.Length > 0)
                    .Distinct()
                    .ToList();
            }

            // 3. 校验并保存
            if (Capabilities.IsRecordingDevice &&
                AppLanguage.Resolve(Config.Language) == AppLanguage.Chinese)
            {
                Config.EdgeTtsVoiceZhHans = Config.EdgeTtsVoice;
                Config.EdgeTtsWarningVoiceZhHans = Config.EdgeTtsWarningVoice;
            }
            else if (Capabilities.IsRecordingDevice)
            {
                Config.EdgeTtsVoiceEnUs = Config.EdgeTtsVoice;
                Config.EdgeTtsWarningVoiceEnUs = Config.EdgeTtsWarningVoice;
            }
            ApplyDeploymentPurposeBeforeSave(
                Config,
                _originalDeploymentPreset,
                DateTime.UtcNow);
            AppConfig.NormalizeAfterLoad(Config);

            if (Capabilities.CanRecordPcVideo && !ValidateEncoderSelectionBeforeSave())
                return false;

            if (!ValidateOrderIdRegexBeforeSave())
                return false;

            AutoStartService.Apply(Config.AutoStartOnBoot);
            Context.SetPreviewZoomScale?.Invoke(null);
            Context.SetPreviewGuideGeometry?.Invoke(null);
            var appliedConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(Config)) ?? new AppConfig();
            bool applied = await Context.ApplyAsync(appliedConfig);
            if (applied)
            {
                _originalTheme = Config.Theme;
                _originalNodeName = Config.NodeName;
                _originalWebAccessKey = Config.WebAccessKey;
                if (_originalLanguage != Config.Language)
                {
                    AppDialog.Information(
                        this,
                        AppLanguage.Get("RestartSaved"),
                        AppLanguage.Get("RestartRequired"));
                    _originalLanguage = Config.Language;
                }
            }
            return applied;
        }

        internal static bool TryNormalizeComputerNickname(string value, out string normalized)
        {
            normalized = value?.Trim() ?? "";
            return normalized.Length is >= 1 and <= 20
                && !normalized.Any(char.IsControl);
        }

        internal static void ApplyDeploymentPurposeBeforeSave(
            AppConfig config,
            string previousPreset,
            DateTime activatedAtUtc)
        {
            ArgumentNullException.ThrowIfNull(config);

            string normalizedPreset = DeploymentPresets.Normalize(config.DeploymentPreset);
            if (!DeploymentPresets.IsKnown(normalizedPreset)
                || string.Equals(
                    normalizedPreset,
                    DeploymentPresets.Normalize(previousPreset),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            DeploymentCapabilities capabilities =
                DeploymentCapabilities.ForPreset(normalizedPreset);
            config.DeploymentPreset = normalizedPreset;
            config.DeploymentSchemaVersion = DeploymentPresets.CurrentSchemaVersion;
            config.WorkstationRole = capabilities.IsRecordingDevice
                ? WorkstationRoles.CameraMonitor
                : normalizedPreset == DeploymentPresets.MobileBackupHost
                    ? WorkstationRoles.PrintStation
                    : "";
            config.EnableWebServer = capabilities.CanRunWebServer;

            if (normalizedPreset == DeploymentPresets.RecordingWorkstation)
            {
                config.RecordingWorkstationActivatedAtUtc = activatedAtUtc;
                RecordingWorkstationCachePolicy.ConfigureInitialLocation(
                    config,
                    preserveExistingLocation: true);
            }
        }

        private bool ValidateCameraIdleNoSleepPeriods()
        {
            if (!TryNormalizeCameraIdleNoSleepPeriod(
                    1,
                    Config.CameraIdleNoSleepStart1,
                    Config.CameraIdleNoSleepEnd1,
                    out string start1,
                    out string end1))
            {
                return false;
            }

            if (!TryNormalizeCameraIdleNoSleepPeriod(
                    2,
                    Config.CameraIdleNoSleepStart2,
                    Config.CameraIdleNoSleepEnd2,
                    out string start2,
                    out string end2))
            {
                return false;
            }

            Config.CameraIdleNoSleepStart1 = start1;
            Config.CameraIdleNoSleepEnd1 = end1;
            Config.CameraIdleNoSleepStart2 = start2;
            Config.CameraIdleNoSleepEnd2 = end2;
            return true;
        }

        /// <summary>本地主存储 fail-closed：只有明确本地才放行，网络/未知一律拒绝。</summary>
        private bool TryConfirmLocalStoragePath(string path, out string errorMessage)
        {
            StorageVolumeInfo.StorageLocationKind kind =
                StorageVolumeInfo.ClassifyStorageLocation(path);
            if (StorageVolumeInfo.IsBackupTargetPath(path))
            {
                errorMessage =
                    "该路径是网络位置或网盘挂载盘，不能作为本地录像保存位置；如需备份到该位置，请添加到备份位置";
                return false;
            }
            if (kind == StorageVolumeInfo.StorageLocationKind.Unknown)
            {
                errorMessage = "无法确认存储位置类型，请确认该路径是本地磁盘后重试";
                return false;
            }
            errorMessage = "";
            return true;
        }

        private bool TryNormalizeCameraIdleNoSleepPeriod(
            int periodNumber,
            string start,
            string end,
            out string normalizedStart,
            out string normalizedEnd)
        {
            if (AppConfig.TryNormalizeCameraIdlePeriod(start, end, out normalizedStart, out normalizedEnd))
                return true;

            string message = string.Format(
                CultureInfo.CurrentCulture,
                AppLanguage.Translate("不休眠时段 {0} 请填写完整的 HH:mm 开始和结束时间，或全部留空"),
                periodNumber);
            AppDialog.Warning(
                this,
                message,
                AppLanguage.Translate("时间格式错误"));
            return false;
        }

        private async void RunSetupWizard_Click(object sender, RoutedEventArgs e)
        {
            if (!Capabilities.CanUseCamera ||
                Context.SuspendCameraForSetupWizard == null ||
                Context.ResumeCameraAfterSetupWizard == null)
                return;

            Keyboard.ClearFocus();
            SyncSelectedMicToConfig();

            bool pausedCamera = false;
            try
            {
                if (!_isRecording)
                {
                    pausedCamera = Context.SuspendCameraForSetupWizard();
                    if (!pausedCamera)
                        return;
                }

                var wizard = new FirstUseSetupWizardWindow(Config) { Owner = this };
                if (wizard.ShowDialog() == true && !wizard.WasSkipped)
                {
                    Config.FirstUseWizardCompleted = true;
                    AppConfig.NormalizeAfterLoad(Config);
                    SyncScannerModeControlsFromConfig();
                    _isLoadingDevices = true;
                    try
                    {
                        await LoadAllDevicesAsync();
                    }
                    finally
                    {
                        _isLoadingDevices = false;
                    }
                }
            }
            finally
            {
                if (pausedCamera)
                    Context.ResumeCameraAfterSetupWizard();
            }
        }

        private bool ConfirmCachedRecordingProfileRisk()
        {
            if (ResComboBox.SelectedItem is not ResOption resolution
                || FpsComboBox.SelectedItem is not ComboBoxItem fpsItem
                || fpsItem.Tag is not int fps)
            {
                return true;
            }

            string codec = VideoCodecComboBox.SelectedItem is GpuEncoderOption codecOption
                ? codecOption.Value
                : Config.VideoCodec ?? "h264";
            codec = codec.Trim().ToLowerInvariant();
            if (codec is not ("h264" or "h265" or "av1"))
                codec = "h264";
            string gpu = GpuEncoderComboBox.SelectedItem is GpuEncoderOption gpuOption
                ? gpuOption.Value
                : Config.GpuEncoder ?? "auto";
            if (!EncodingHelper.TryResolveEncoder(
                gpu,
                codec,
                MainViewModel.ValidatedEncoders ?? new HashSet<string>(),
                MainViewModel.EncoderPerformanceScores,
                out string encoder))
                return true;
            var selectedMode = new NativeCameraMode(resolution.Width, resolution.Height, fps);
            int videoCqp = RecordingProfileDetector.NormalizeVideoCqp(Config.VideoCqp);
            if (!RecordingProfileDetector.TryGetCachedBenchmark(
                    Config,
                    encoder,
                    videoCqp,
                    selectedMode,
                    out RecordingBenchmarkCacheEntry cached)
                || RecordingProfileDetector.CachedBenchmarkSupportsFrameRate(cached, fps))
            {
                return true;
            }

            return AppDialog.Confirm(
                this,
                $"缓存的性能检测结果显示，{resolution.Width}×{resolution.Height} @ {fps} FPS " +
                $"可能无法稳定实时录制。\n\n实测最大编码速度：{cached.MeasuredEncodingFps:F1} FPS，" +
                $"未达到保留 20% 余量所需的 {fps * RecordingProfileDetector.RequiredEncodingSpeed:F1} FPS。\n\n是否仍然应用此配置？",
                "录制性能提醒",
                AppDialogSeverity.Warning,
                confirmText: "仍然应用",
                cancelText: "返回调整",
                isDangerous: false);
        }

        private async void DetectRecordingProfile_Click(object sender, RoutedEventArgs e)
        {
            if (Context.DetectRecordingProfileAsync == null
                || DetectRecordingProfileButton == null
                || CameraComboBox.SelectedItem is not CameraInfo camera
                || string.IsNullOrWhiteSpace(camera.Moniker))
            {
                Context.ShowToast?.Invoke("请先选择可用摄像头", ToastSeverity.Warning);
                return;
            }

            if (GpuEncoderComboBox.SelectedItem is GpuEncoderOption gpuOption)
                Config.GpuEncoder = gpuOption.Value;
            if (VideoCodecComboBox.SelectedItem is GpuEncoderOption codecOption)
                Config.VideoCodec = codecOption.Value;

            DetectRecordingProfileButton.IsEnabled = false;
            DetectRecordingProfileButton.Content = AppLanguage.Translate("正在检测，请稍候");
            try
            {
                IReadOnlyList<NativeCameraMode> nativeModes;
                if (IsNetworkCamera(camera))
                {
                    if (!NetworkCameraUrlPolicy.TryNormalize(
                            NetworkCameraUrlTextBox.Text,
                            out string networkUrl,
                            out string networkError))
                    {
                        Context.ShowToast?.Invoke("请先填写网络摄像头地址", ToastSeverity.Warning);
                        return;
                    }

                    using var probeSource = new NetworkCameraSource(
                        networkUrl,
                        AppConfig.NormalizeNetworkTransport(Config.NetworkCameraRtspTransport),
                        Config.Fps > 0 ? Config.Fps : 15);
                    bool connected = await probeSource.StartAsync();
                    if (!connected)
                    {
                        Context.ShowToast?.Invoke(
                            $"网络摄像头连接失败：{probeSource.LastError ?? "请检查地址和网络"}",
                            ToastSeverity.Error);
                        return;
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
                RecordingProfileRecommendation recommendation =
                    await Context.DetectRecordingProfileAsync(Config, nativeModes);
                if (recommendation?.Success != true
                    || recommendation.Mode is not NativeCameraMode recommendedMode)
                {
                    Context.ShowToast?.Invoke(
                        recommendation?.Message ?? "录制性能检测失败，已保留当前配置",
                        ToastSeverity.Error);
                    return;
                }

                if (!RecordingProfileDetector.IsRecommendationDifferent(Config, recommendedMode))
                {
                    Context.ShowToast?.Invoke("检测完成，当前录制规格已是推荐配置", ToastSeverity.Success);
                    return;
                }

                bool applyRecommendation = AppDialog.Confirm(
                    this,
                    $"当前配置：{Config.FrameWidth}×{Config.FrameHeight} @ {Config.Fps} FPS\n" +
                    $"推荐配置：{recommendedMode.Width}×{recommendedMode.Height} @ {recommendedMode.Fps} FPS",
                    "录制规格推荐",
                    AppDialogSeverity.Information,
                    confirmText: "应用推荐配置",
                    cancelText: "保持当前配置",
                    isDangerous: false);
                if (!applyRecommendation)
                {
                    Context.ShowToast?.Invoke("已保持当前录制配置", ToastSeverity.Success);
                    return;
                }

                RecordingProfileDetector.ApplyRecommendation(
                    Config,
                    recommendedMode,
                    IsNetworkCamera(camera)
                        ? AppConfig.GetCameraConfigKey("network", Config.NetworkCameraUrl)
                        : camera.Moniker);
                if (!IsNetworkCamera(camera))
                {
                    await LoadCameraCapabilitiesAsync(
                        camera.Index,
                        recommendedMode.Width,
                        recommendedMode.Height,
                        recommendedMode.Fps);
                }
                Context.ShowToast?.Invoke("已填入推荐录制规格，保存设置后生效", ToastSeverity.Success);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("RecordingProfile", "Settings recording profile detection failed", ex);
                Context.ShowToast?.Invoke("录制性能检测失败，已保留当前配置", ToastSeverity.Error);
            }
            finally
            {
                DetectRecordingProfileButton.IsEnabled = true;
                DetectRecordingProfileButton.Content = AppLanguage.Translate("开始检测");
            }
        }

        private void ZoomScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ShouldPreviewZoomScale(IsLoaded, Context))
                Context.SetPreviewZoomScale?.Invoke(e.NewValue);
        }

        internal static bool ShouldPreviewZoomScale(bool isLoaded, SettingsContext context) =>
            isLoaded && context?.Capabilities.CanRecordPcVideo == true;

        private void CameraBarcodeGuideSlider_ValueChanged(
            object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            if (!ShouldPreviewGuideGeometry())
                return;

            Context.SetPreviewGuideGeometry?.Invoke(new CameraBarcodeGuideGeometry(
                CameraBarcodeGuideWidthSlider?.Value ?? CameraBarcodeGuideGeometry.Default.WidthRatio,
                CameraBarcodeGuideHeightSlider?.Value ?? CameraBarcodeGuideGeometry.Default.HeightRatio,
                CameraBarcodeGuideOffsetXSlider?.Value ?? 0,
                CameraBarcodeGuideOffsetYSlider?.Value ?? 0));
        }

        private bool ShouldPreviewGuideGeometry() =>
            IsLoaded && Context?.Capabilities.CanUseCameraBarcode == true;

        private void SyncVoiceEngineComboBoxFromConfig()
        {
            if (VoiceEngineComboBox == null) return;

            _isSyncingVoiceEngine = true;
            VoiceEngineComboBox.SelectedValue = Config.EnableAiTts
                ? NormalizeVoiceEngine(Config.AiTtsEngine)
                : "System";
            _isSyncingVoiceEngine = false;
        }

        private void VoiceEngineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingVoiceEngine || Config == null) return;

            string engine = VoiceEngineComboBox.SelectedValue?.ToString() ?? "System";
            if (string.Equals(engine, "System", StringComparison.OrdinalIgnoreCase))
            {
                Config.EnableAiTts = false;
                return;
            }

            Config.EnableAiTts = true;
            Config.AiTtsEngine = NormalizeVoiceEngine(engine);
        }

        private static string NormalizeVoiceEngine(string engine)
        {
            return string.Equals(engine, "Kokoro", StringComparison.OrdinalIgnoreCase) ? "Kokoro" : "Edge";
        }

        private void SyncPackingModeComboBoxFromConfig()
        {
            if (PackingModeComboBox == null) return;

            string tag = Config.EnableSameBarcodeStopRecording ? "SameCode" : "Continuous";
            PackingModeComboBox.SelectedItem = PackingModeComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal));
        }

        private void PackingModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Config == null || PackingModeComboBox.SelectedItem is not ComboBoxItem item) return;

            bool sameCodeStop = string.Equals(item.Tag?.ToString(), "SameCode", StringComparison.Ordinal);
            Config.EnableSameBarcodeStopRecording = sameCodeStop;
            if (PackingModeHintText != null)
            {
                PackingModeHintText.Text = sameCodeStop
                    ? "识别或扫描面单条码开始录制，再次识别同一条码停止录制"
                    : "推荐连续打包模式，识别或扫描下一张面单时自动保存上一单";
            }
            UpdateEventRecordingBufferControlState();
        }

        private void EventRecordingBufferToggle_Changed(object sender, RoutedEventArgs e)
        {
            UpdateEventRecordingBufferControlState();
        }

        private void PreRecordBufferSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdatePreRecordEstimateText();
        }

        private void VideoFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdatePreRecordEstimateText();
        }

        private void UpdatePreRecordEstimateText()
        {
            if (PreRecordEstimateText == null || PreRecordBufferSlider == null)
                return;

            int width = Config?.FrameWidth ?? 0;
            int height = Config?.FrameHeight ?? 0;
            if (ResComboBox?.SelectedItem is ResOption resolution)
            {
                width = resolution.Width;
                height = resolution.Height;
            }

            int fps = Config?.Fps ?? 0;
            if (FpsComboBox?.SelectedItem is ComboBoxItem fpsItem && fpsItem.Tag is int selectedFps)
                fps = selectedFps;

            double bufferMb = Math.Max(0, PreRecordBufferSlider.Value);
            int maximumMb = PreRecordBufferPolicy.GetMaximumMb(
                width,
                height,
                fps,
                PreRecordBufferPolicy.GetPhysicalMemoryBytes());
            PreRecordBufferSlider.Maximum = maximumMb;
            if (bufferMb > maximumMb)
            {
                bufferMb = maximumMb;
                PreRecordBufferSlider.Value = maximumMb;
                Config.PreRecordBufferMB = maximumMb;
            }
            double seconds = width > 0 && height > 0 && fps > 0
                ? bufferMb * 1024d * 1024d / (width * height * 3d * fps)
                : 0;
            string size = bufferMb >= 1024 ? $"{bufferMb / 1024d:0.#} GB" : $"{bufferMb:0} MB";
            PreRecordEstimateText.Text = $"约 {seconds:0.0} 秒 / {size} 内存";
        }

        private void UpdateEventRecordingBufferControlState()
        {
            if (Config == null) return;

            bool bufferEnabled = Config.EnableEventRecordingBuffer;
            bool sameCodeMode = Config.EnableSameBarcodeStopRecording;
            if (PreRecordBufferRow != null)
                PreRecordBufferRow.IsEnabled = bufferEnabled;
            if (SameCodePostRecordRow != null)
                SameCodePostRecordRow.IsEnabled = bufferEnabled && sameCodeMode;
        }

        private void InstallTool_Click(object sender, RoutedEventArgs e)
        {
            Context.OpenUserscriptGuide?.Invoke();
        }

        private void StorageDataGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            DependencyObject current = sender as DependencyObject;
            while (current != null && current is not ScrollViewer)
                current = VisualTreeHelper.GetParent(current);

            if (current is not ScrollViewer settingsScrollViewer) return;

            e.Handled = true;
            settingsScrollViewer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = MouseWheelEvent,
                Source = settingsScrollViewer
            });
        }

        private void OpenExtensionMarket_Click(object sender, RoutedEventArgs e)
        {
            Context.OpenExtensionMarket?.Invoke(this);
            RefreshCustomUserscripts();
        }

        private void RemoveUserscript_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { CommandParameter: UserscriptDescriptor script }) return;
            if (!AppDialog.Confirm(
                    this,
                    $"确定删除自定义脚本“{script.Name}”吗？删除后将无法再从安装向导安装该脚本",
                    "删除自定义脚本",
                    AppDialogSeverity.Warning,
                    "删除"))
                return;

            if (new UserscriptCatalog().Remove(script.Id))
            {
                RefreshCustomUserscripts();
                Context.ShowToast?.Invoke("自定义脚本已删除", ToastSeverity.Success);
            }
        }

        private void RefreshCustomUserscripts()
        {
            CustomUserscripts.Clear();
            foreach (UserscriptDescriptor script in new UserscriptCatalog().GetCustomScripts())
                CustomUserscripts.Add(script);
        }

        private void RefreshExtensionAuthorizations()
        {
            ExtensionAuthorizations.Clear();
            foreach (ExtensionAuthorizationDisplayItem item in
                     Context.GetExtensionAuthorizations?.Invoke() ?? [])
                ExtensionAuthorizations.Add(item);
        }

        private void IntegrationStatusTimer_Tick(object sender, EventArgs e)
        {
            RefreshExtensionAuthorizations();
            RefreshOrderIntegrationDevices();
        }

        private void RefreshOrderIntegrationDevices()
        {
            OrderIntegrationDevices.Clear();
            foreach (OrderIntegrationDeviceDisplayItem item in
                     Context.GetOrderIntegrationDevices?.Invoke() ?? [])
                OrderIntegrationDevices.Add(item);
        }

        private void RevokeExtensionAuthorization_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { CommandParameter: ExtensionAuthorizationDisplayItem item }) return;
            if (!AppDialog.Confirm(
                    this,
                    $"确定撤销“{item.DisplayName}”的授权吗？撤销后该扩展会立即停止接收新任务",
                    "撤销扩展授权",
                    AppDialogSeverity.Warning,
                    "撤销"))
                return;
            if (Context.RevokeExtensionAuthorization?.Invoke(item.ExtensionInstanceId) == true)
            {
                RefreshExtensionAuthorizations();
                Context.ShowToast?.Invoke("扩展授权已撤销", ToastSeverity.Success);
            }
        }

        private void ShowMobileConnection_Click(object sender, RoutedEventArgs e)
        {
            Context.ShowMobileConnection?.Invoke(this);
        }

        private void CopyMobileConnectionUrl_Click(object sender, RoutedEventArgs e)
        {
            Context.CopyMobileConnectionUrl?.Invoke();
        }

        private void SelectMicByConfig(List<MicInfo> mics)
        {
            var micMatch = mics.FirstOrDefault(m => !string.IsNullOrEmpty(Config.AudioDeviceMoniker)
                                                    && m.Moniker == Config.AudioDeviceMoniker)
                        ?? mics.FirstOrDefault(m => m.Name == Config.AudioDeviceName);
            if (micMatch != null)
            {
                MicComboBox.SelectedItem = micMatch;
                if (IsAvailableMic(micMatch))
                {
                    Config.AudioDeviceName = micMatch.Name;
                    Config.AudioDeviceMoniker = micMatch.Moniker ?? "";
                }
            }
        }

        private void SyncSelectedMicToConfig()
        {
            if (MicComboBox.SelectedItem is MicInfo mic && IsAvailableMic(mic))
            {
                Config.AudioDeviceName = mic.Name;
                Config.AudioDeviceMoniker = mic.Moniker ?? "";
            }
            else
            {
                Config.AudioDeviceName = "";
                Config.AudioDeviceMoniker = "";
            }
        }

        private static bool IsAvailableMic(MicInfo mic)
        {
            return mic != null
                && !string.IsNullOrWhiteSpace(mic.Name)
                && mic.Name != "未检测到麦克风";
        }

        protected override void OnClosed(EventArgs e)
        {
            _isClosing = true;
            _integrationStatusTimer.Stop();
            _integrationStatusTimer.Tick -= IntegrationStatusTimer_Tick;
            var migrationCts = Interlocked.Exchange(ref _migrationCts, null);
            try { migrationCts?.Cancel(); } catch (ObjectDisposedException) { }
            Context.SetPreviewZoomScale?.Invoke(null);
            Context.SetPreviewGuideGeometry?.Invoke(null);
            _previewSpeechService?.Stop();
            _previewSpeechService?.Dispose();
            _previewSpeechService = null;
            base.OnClosed(e);
        }

        private bool ValidateEncoderSelectionBeforeSave()
        {
            string codec = (Config.VideoCodec ?? "h264").Trim().ToLowerInvariant();
            string gpu = NormalizeGpuSetting(Config.GpuEncoder ?? "auto");
            var validated = MainViewModel.ValidatedEncoders ?? new HashSet<string>();

            if (EncodingHelper.TryResolveEncoder(gpu, codec, validated,
                    MainViewModel.EncoderPerformanceScores, out _))
                return true;

            AppDialog.Error(this, "当前选择未通过编码器检测，请重新检测或选择列表中的可用编码器", "编码器不可用");
            return false;
        }

        private bool ValidateOrderIdRegexBeforeSave()
        {
            if (CameraBarcodeCandidatePolicy.IsValidPattern(Config.OrderIdRegex))
                return true;

            AppDialog.Error(
                this,
                "单号判断规则写错了，无法保存，请检查后重试",
                "单号判断规则错误");
            return false;
        }

        private void OpenRepository_Click(object sender, RoutedEventArgs e)
        {
            OpenExternalUrl("https://github.com/PackingProof/PackingProof-Desktop");
        }

        private async void ExportDiagnosticLogs_Click(object sender, RoutedEventArgs e)
        {
            Button diagnosticButton = sender as Button;
            if (diagnosticButton != null) diagnosticButton.IsEnabled = false;
            try
            {
                bool confirmed = AppDialog.Confirm(
                    this,
                    $"将打包运行日志、配置和完整录像数据库（含订单明细、买家留言等隐私数据）到本地压缩包。\n确认继续吗？打包完成后可发送到反馈邮箱 {FeedbackEmail}",
                    "导出诊断日志",
                    AppDialogSeverity.Warning,
                    confirmText: "开始打包");
                if (!confirmed) return;

                IReadOnlyList<string> warnings = Array.Empty<string>();
                string zipPath = "";
                string emlPath = "";
                await Task.Run(() =>
                {
                    var service = new FeedbackPackageService(AppPaths.UserDataDir);
                    zipPath = service.CreatePackage(out IReadOnlyList<string> packageWarnings);
                    warnings = packageWarnings;
                    emlPath = service.CreateFeedbackEml(zipPath, FeedbackEmail);
                });

                try { Clipboard.SetText(zipPath); } catch { }
                // 直接打开文件位置；邮件客户端不自动打开，避免窗口互相覆盖，
                // 由用户在结果弹窗里点击“发送邮件”后再打开。
                try
                {
                    Process.Start(new ProcessStartInfo(
                        "explorer.exe",
                        $"/select,\"{zipPath}\"") { UseShellExecute = true });
                }
                catch { }

                var info = new FileInfo(zipPath);
                string message =
                    $"诊断包已生成：\n{zipPath}\n\n" +
                    $"大小：{FormatBytes(info.Length)}\n" +
                    "已复制路径并打开所在文件夹。\n\n" +
                    $"点击“发送邮件”会尝试直接打开一封已带反馈模板和压缩包附件的新邮件（收件人 {FeedbackEmail}），填写问题后发送即可；若本机没有经典 Outlook，会退回邮件草稿或普通邮件（可能需要手动添加附件）。\n\n" +
                    "注意：包内含完整订单数据库与本地配置，请勿转发给无关人员";
                if (warnings.Count > 0)
                    message += "\n\n提示：\n" + string.Join("\n", warnings.Take(10));

                bool sendMail = AppDialog.Confirm(
                    this,
                    message,
                    "导出诊断日志",
                    AppDialogSeverity.Information,
                    confirmText: "发送邮件",
                    cancelText: "关闭",
                    isDangerous: false);
                if (sendMail)
                {
                    string subject =
                        $"PackingProof 反馈（{ExpressPackingMonitoring.Config.AppVersion.Current}）";
                    string body = FeedbackPackageService.BuildFeedbackBody(
                        zipPath,
                        ExpressPackingMonitoring.Config.AppVersion.Current,
                        ExpressPackingMonitoring.Config.AppVersion.CommitShortId);
                    bool opened =
                        FeedbackMailLauncher.TryOpenOutlookDraft(
                            FeedbackEmail, subject, body, zipPath)
                        || FeedbackMailLauncher.TryOpenEmlDraft(emlPath)
                        || FeedbackMailLauncher.TryOpenMailto(FeedbackEmail, subject, body);
                    if (!opened)
                    {
                        AppDialog.Error(
                            this,
                            $"未能打开邮件客户端，请手动发送到 {FeedbackEmail}（压缩包路径已复制到剪贴板，请作为附件添加）",
                            "导出诊断日志");
                    }
                }
            }
            catch (Exception ex)
            {
                AppDialog.Error(this, $"打包失败：{ex.Message}", "导出诊断日志");
            }
            finally
            {
                if (diagnosticButton != null) diagnosticButton.IsEnabled = true;
            }
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return $"{value:0.##} {units[unit]}";
        }

        private void OpenLicense_Click(object sender, RoutedEventArgs e)
        {
            OpenExternalUrl("https://github.com/PackingProof/PackingProof-Desktop/blob/main/LICENSE");
        }

        private static string GetStorageRoot(string path)
        {
            try
            {
                string fullPath = Path.GetFullPath(path.Trim());
                return Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? fullPath;
            }
            catch
            {
                return path?.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? "";
            }
        }

        private static ImageSource GetLargestAppIconImage()
        {
            var decoder = BitmapDecoder.Create(
                new Uri("pack://application:,,,/app.ico", UriKind.Absolute),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            BitmapFrame frame = decoder.Frames
                .OrderByDescending(x => x.PixelWidth * x.PixelHeight)
                .First();
            frame.Freeze();
            return frame;
        }

        private static void OpenExternalUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppDialog.Error(null, $"无法打开链接：{ex.Message}", "打开链接失败");
            }
        }

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdateButton.IsEnabled = false;
            CheckUpdateButton.Content = "正在检查...";

            try
            {
                var service = new UpdateCheckService();
                UpdateCheckResult result = await service.CheckManualAsync();
                if (result.HasUpdate)
                    ShowUpdateDialog(result);

                CheckUpdateButton.Content = result.HasUpdate
                    ? "发现新版本"
                    : "已为最新";
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Update", "Manual update check failed", ex);
                CheckUpdateButton.Content = "检查失败";
                CheckUpdateButton.IsEnabled = true;
            }
        }

        private void ShowUpdateDialog(UpdateCheckResult result)
        {
            var dialog = new UpdateAvailableDialog(
                result,
                new AppPatchDownloadService(),
                () => Context.ToastSource is MainViewModel viewModel
                    ? viewModel.IsRecording
                    : _isRecording)
            {
                Owner = this
            };

            dialog.ShowDialog();
            if (dialog.RestartRequested)
            {
                RestartForPreparedUpdate();
                return;
            }

            if (dialog.OpenFullDownloadPageRequested)
            {
                try
                {
                    UpdateCheckService.OpenDownloadPage(dialog.DownloadUrl);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("Update", "Open download page failed", ex);
                    if (Context.ShowToast != null)
                        Context.ShowToast("打开下载页面失败", ToastSeverity.Error);
                    else
                        AppDialog.Error(this, "打开下载页面失败", "检查更新");
                }
            }
        }

        private void RestartForPreparedUpdate()
        {
            if (!WorkstationNetwork.TryScheduleRootLauncherRestart("manual-app-update"))
            {
                AppDialog.Error(
                    this,
                    "无法定位支持更新的根目录启动器。补丁已经保留，可完全退出后手动从软件根目录启动",
                    "无法立即重启");
                return;
            }

            DialogResult = false;
            Close();
            _ = Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                Window mainWindow = Application.Current.MainWindow;
                if (mainWindow != null)
                    mainWindow.Close();
                else
                    Application.Current.Shutdown();
            }));
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (Config.Theme != _originalTheme)
            {
                if (Enum.TryParse<ExpressPackingMonitoring.Themes.AppTheme>(_originalTheme, out var themeEnum))
                {
                    ExpressPackingMonitoring.Themes.ThemeManager.ApplyTheme(themeEnum);
                }
            }
            this.DialogResult = false;
            this.Close();
        }

        private SpeechService _previewSpeechService;

        private void BtnTtsPreview_Click(object sender, RoutedEventArgs e)
        {
            Keyboard.ClearFocus();

            string text = TtsPreviewTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                TtsPreviewStatus.Text = "请输入预览文本";
                return;
            }

            // 显示预处理后的文本
            string processed = SpeechService.PreprocessTextForTts(text);
            TtsPreviewStatus.Text = $"断句: {processed}";

            // 初始化或复用预览用 SpeechService
            if (_previewSpeechService == null)
            {
                _previewSpeechService = new SpeechService
                {
                    EnableSoundPrompt = true,
                    MaximizeVolumeForSpeech = Config.MaximizeVolumeForSpeech,
                    EnableAiTts = Config.EnableAiTts,
                    AiTtsEngine = Config.AiTtsEngine,
                    AiTtsSpeakerId = Config.AiTtsSpeakerId,
                    AiTtsWarningSpeakerId = Config.AiTtsWarningSpeakerId,
                    AiTtsSpeed = Config.AiTtsSpeed,
                    EdgeTtsVoice = Config.EdgeTtsVoice,
                    EdgeTtsWarningVoice = Config.EdgeTtsWarningVoice,
                };
                _previewSpeechService.PlaybackError += OnPreviewSpeechError;
                // 同步当前编辑中的断句关键词
                var words = TtsBreakWordsTextBox.Text
                    .Split(new[] { '\r', '\n', '，', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.Trim()).Where(w => w.Length > 0);
                _previewSpeechService.UpdateBreakWords(words);
                if (Config.EnableAiTts)
                    _previewSpeechService.InitAiTts();
            }
            else
            {
                // 更新参数
                _previewSpeechService.EnableAiTts = Config.EnableAiTts;
                _previewSpeechService.MaximizeVolumeForSpeech = Config.MaximizeVolumeForSpeech;
                _previewSpeechService.AiTtsEngine = Config.AiTtsEngine;
                _previewSpeechService.AiTtsSpeakerId = Config.AiTtsSpeakerId;
                _previewSpeechService.AiTtsWarningSpeakerId = Config.AiTtsWarningSpeakerId;
                _previewSpeechService.AiTtsSpeed = Config.AiTtsSpeed;
                _previewSpeechService.EdgeTtsVoice = Config.EdgeTtsVoice;
                _previewSpeechService.EdgeTtsWarningVoice = Config.EdgeTtsWarningVoice;
                var words = TtsBreakWordsTextBox.Text
                    .Split(new[] { '\r', '\n', '，', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.Trim()).Where(w => w.Length > 0);
                _previewSpeechService.UpdateBreakWords(words);
            }

            _previewSpeechService.Preview(text);
        }

        private void BtnTtsStop_Click(object sender, RoutedEventArgs e)
        {
            _previewSpeechService?.Stop();
            TtsPreviewStatus.Text = "已停止";
        }

        private void OnPreviewSpeechError(string message)
        {
            Dispatcher.InvokeAsync(() => TtsPreviewStatus.Text = $"试听失败：{message}");
        }

        private CancellationTokenSource _migrationCts;
        private bool _isClosing;

        private async void BtnMigrateMkv_Click(object sender, RoutedEventArgs e)
        {
            if (!Capabilities.CanRecordPcVideo || Context.BatchConvertMkvToMp4Async == null)
                return;

            var runningMigration = _migrationCts;
            if (runningMigration != null)
            {
                // 正在迁移中，点击取消
                runningMigration.Cancel();
                return;
            }

            var migrationCts = new CancellationTokenSource();
            if (Interlocked.CompareExchange(ref _migrationCts, migrationCts, null) != null)
            {
                migrationCts.Dispose();
                return;
            }

            BtnMigrateMkv.Content = "取消合并";
            MigrationProgress.Visibility = Visibility.Visible;
            MigrationStatusText.Text = "正在扫描 MKV 记录...";

            var progress = new Progress<string>(msg =>
            {
                if (!_isClosing)
                    MigrationStatusText.Text = msg;
            });

            try
            {
                MkvBatchConversionResult result =
                    await Context.BatchConvertMkvToMp4Async(progress, migrationCts.Token);
                if (!_isClosing)
                {
                    MigrationStatusText.Text = FormatMkvMigrationSummary(result);
                }
            }
            catch (OperationCanceledException)
            {
                if (!_isClosing)
                    MigrationStatusText.Text = "合并已取消";
            }
            catch (Exception ex)
            {
                if (!_isClosing)
                    MigrationStatusText.Text = $"合并出错：{ex.Message}";
            }
            finally
            {
                Interlocked.CompareExchange(ref _migrationCts, null, migrationCts);
                migrationCts.Dispose();
                if (!_isClosing)
                {
                    BtnMigrateMkv.Content = "开始合并";
                    MigrationProgress.Visibility = Visibility.Collapsed;
                }
            }
        }

        internal static string FormatMkvMigrationSummary(MkvBatchConversionResult result)
        {
            if (result.SuccessCount == 0 && result.FailureCount == 0)
                return "处理完成：没有需要转换的 MKV";

            var parts = new List<string>
            {
                $"已生成兼容 MP4 {result.SuccessCount} 个"
            };
            if (result.FailureCount > 0)
                parts.Add($"未生成 {result.FailureCount} 个，原始录像已保留");
            return $"处理完成：{string.Join("；", parts)}";
        }
    }
}
