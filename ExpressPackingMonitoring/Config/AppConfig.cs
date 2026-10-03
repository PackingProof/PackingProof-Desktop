using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using ExpressPackingMonitoring.Localization;

namespace ExpressPackingMonitoring.Config
{
    public static class WindowCloseBehaviors
    {
        public const string Ask = "Ask";
        public const string MinimizeToTray = "MinimizeToTray";
        public const string Exit = "Exit";

        public static string Normalize(string? value) =>
            value is MinimizeToTray or Exit ? value : Ask;
    }

    public static class TrayKeyboardListeningBehaviors
    {
        public const string Ask = "Ask";
        public const string Continue = "Continue";
        public const string Pause = "Pause";

        public static string Normalize(string? value) =>
            value is Continue or Pause ? value : Ask;
    }

    public partial class ScanRecord : ObservableObject
    {
        [ObservableProperty] private string _orderId;
        [ObservableProperty] private string _duration;
        [ObservableProperty] private string _dateStr;
        [ObservableProperty] private string _mode;

        // 新增活跃状态，用于前端变色
        [ObservableProperty] private bool _isActive;

        public ScanRecord(string orderId, string duration, string dateStr, string mode, bool isActive = false)
        { 
            OrderId = orderId; 
            Duration = duration; 
            DateStr = dateStr; 
            Mode = mode; 
            IsActive = isActive; 
        }
    }

    public class GpuEncoderOption
    {
        public string Value { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }

    // 存储节点模型
    public class StorageLocation
    {
        public string Path { get; set; } = "D:\\快递打包视频";
        public double ReserveGB { get; set; } = 0.0;
        public int Priority { get; set; } = 1; // 数字越小越优先
        /// <summary>用户显式添加的备份目标；网盘挂载盘未挂载时仍据此保留备份角色。</summary>
        public bool IsBackupTarget { get; set; }
        // 卷标识与最后验证时间，为未来盘符变化自动重定位预留数据（本版本不实现重映射）。
        public string VolumeId { get; set; } = "";
        public DateTime? LastVerifiedAt { get; set; }

        [JsonIgnore]
        public double EffectiveReserveGB
        {
            get => StorageSpacePolicy.GetEffectiveReserveGB(this);
            set => ReserveGB = StorageSpacePolicy.NormalizeReserveGB(Path, value);
        }
    }

    internal readonly record struct StorageDriveCandidate(string RootPath, bool IsReady, DriveType DriveType);

    // 摄像头独立配置模型
    public class CameraSettings
    {
        public int FrameWidth { get; set; } = 1280;
        public int FrameHeight { get; set; } = 720;
        public int Fps { get; set; } = 15;
        public string AudioDeviceName { get; set; } = "";
        public string AudioDeviceMoniker { get; set; } = "";
        public int AudioSyncOffsetMs { get; set; } = 0;
        public bool Rotate180 { get; set; }
        /// <summary>每台设备记住的旋转角度；-1 表示旧配置里只有 Rotate180 开关。</summary>
        public int RotationDegrees { get; set; } = AppConfig.UnsetRotationDegrees;
    }

    public sealed class RecordingBenchmarkCacheEntry
    {
        public int SchemaVersion { get; set; }
        public string Encoder { get; set; } = "";
        public int VideoCqp { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool CompletedSuccessfully { get; set; }
        public int EncodedFrames { get; set; }
        public double ElapsedSeconds { get; set; }
        public double MeasuredEncodingFps { get; set; }
        public DateTime TestedAt { get; set; }
    }

    public sealed class EncoderPerformanceCacheEntry
    {
        public int SchemaVersion { get; set; }
        public string Encoder { get; set; } = "";
        public string Gpu { get; set; } = "";
        public string Codec { get; set; } = "";
        public int VideoCqp { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool CompletedSuccessfully { get; set; }
        public double MeasuredEncodingFps { get; set; }
        public DateTime TestedAt { get; set; }
        public string Detail { get; set; } = "";
    }

    public class AppConfig
    {
        public const int HighestQualityVideoCqp = 18;

        /// <summary>
        /// 本实例加载时的原始 JSON。保存时按字段做三方合并需要它作为基线，
        /// 否则长时间持有配置的进程会把别人期间的改动整份覆盖（不是配置项，不序列化）。
        /// </summary>
        [JsonIgnore]
        internal string LoadedJson { get; set; } = "";

        public const int LowestQualityVideoCqp = 36;
        public const int DefaultVideoCqp = 30;

        public const int CurrentVoiceSettingsVersion = 2;
        public const int CurrentCameraBarcodeSetupVersion = 1;
        public const int CurrentMobileConnectionSetupVersion = 1;
        public const int CurrentDeploymentSetupVersion = 1;
        public const int CurrentRecordingSetupVersion = 2;
        public const int CurrentBackupConnectionSchemaVersion = 1;
        public const int CurrentWebProtectionSetupVersion = 1;
        public const int CurrentDeletedVideoVisibilitySetupVersion = 1;
        public const int CurrentStorageReserveSchemaVersion = 1;

        /// <summary>智能特写停留时间的当前默认值（秒）</summary>
        public const double DefaultZoomDurationSeconds = 2.5;

        /// <summary>叠加画面（画中画）默认采集规格。面单特写是静物，720p 足够看清字样。</summary>
        public const string DefaultOverlayResolutionPreset = "720p";

        /// <summary>
        /// 叠加画面默认帧率。取 30：摄像头普遍支持，Media Foundation 会就近协商到设备实际支持的档位，
        /// 画面跟手；叠加画面本来就是静物特写，再高只会白吃带宽和解码。
        /// </summary>
        public const int DefaultOverlayFrameFps = 30;

        public const int MinimumOverlayFrameFps = 1;
        public const int MaximumOverlayFrameFps = 60;

        /// <summary>叠加画面默认旋转角度：手机/竖装摄像头常见是竖装，默认转 90° 摆正。</summary>
        public const int DefaultOverlayRotationDegrees = 90;

        /// <summary>叠加画面识别框默认尺寸，与主摄同一套口径：占画面宽高的比例，偏移 0 表示居中。</summary>
        public const double DefaultOverlayGuideRatio = 0.85;
        public const double MinimumOverlayGuideRatio = 0.1;
        public const double MaximumOverlayGuideRatio = 1.0;

        /// <summary>叠加画面默认几路：主摄之外再叠两路。</summary>
        public const int DefaultOverlayChannelCount = 2;

        /// <summary>
        /// 叠加画面可配置的路数区间。上限是硬约束：每多一路就多一份采集与解码，
        /// 两路 USB 摄像头已经吃满多数机器的带宽，再多画面也看不清。
        /// </summary>
        public const int MinimumOverlayChannelCount = 1;

        public const int MaximumOverlayChannelCount = 4;

        /// <summary>叠加画面来源为"不接"的取值：设置页据此收起这一路的其余选项。</summary>
        public const string OverlayChannelSourceNone = "none";

        /// <summary>
        /// 旋转角度未设置：老配置里只有"旋转 180°"开关，加载时按它推导出实际角度，
        /// 之后一律以角度字段为准。
        /// </summary>
        public const int UnsetRotationDegrees = -1;

        /// <summary>叠加画面宽度占主画面的默认比例</summary>
        public const double DefaultOverlayWidthRatio = 0.25;

        public const double MinimumOverlayWidthRatio = 0.1;
        public const double MaximumOverlayWidthRatio = 0.5;

        /// <summary>叠加画面距主画面右下角的默认留白（像素）</summary>
        public const int DefaultOverlayMargin = 16;

        public const int MaximumOverlayMargin = 200;

        /// <summary>叠加画面位置未自定义的哨兵值：小于 0 一律按右下角自动摆放</summary>
        public const double UnsetOverlayPosition = -1;

        /// <summary>历史默认值：老版本写过 3 秒，中间版本写过 1 秒，都会落进用户配置</summary>
        private static readonly double[] LegacyZoomDurationSeconds = [3.0, 1.0];

        // 语音提醒设置迁移版本。旧配置没有该字段，加载后会从 0 迁移到当前版本。
        public int VoiceSettingsVersion { get; set; } = 0;

        // 摄像头识别升级引导版本。旧配置缺少该字段时会提示用户选择是否启用。
        public int CameraBarcodeSetupVersion { get; set; } = 0;

        // 手机扫码连接升级引导版本。旧配置缺少该字段时会在局域网服务就绪后提示一次。
        public int MobileConnectionSetupVersion { get; set; } = 0;

        // 部署场景使用稳定字符串持久化，避免枚举顺序变化破坏配置兼容性。
        public string DeploymentPreset { get; set; } = "";
        public int DeploymentSchemaVersion { get; set; } = 0;
        public string NodeId { get; set; } = "";
        public string NodeName { get; set; } = "";
        public bool NodeNameCustomized { get; set; }
        public string LastKnownHostNodeId { get; set; } = "";
        public string LastKnownHostNodeName { get; set; } = "";
        public string LastKnownHostAddress { get; set; } = "";
        public string LastKnownHostAccessKey { get; set; } = "";
        // 查看端保存主机的网页访问密钥；与录制工位使用的设备令牌 LastKnownHostAccessKey 区分。
        public string LastKnownHostWebAccessKey { get; set; } = "";
        public int LastKnownHostBackupAuthVersion { get; set; }
        public int BackupConnectionSchemaVersion { get; set; }
        public string RecordingCachePolicy { get; set; } = "KeepWithinSize";
        public int RecordingCacheKeepDays { get; set; } = 3;
        public int RecordingCacheMaxGB { get; set; } = 100;
        public DateTime? RecordingWorkstationActivatedAtUtc { get; set; }
        public string LastVideoImportFolder { get; set; } = "";
        public string LastUserscriptTargetSignature { get; set; } = "";
        public int DeploymentSetupVersion { get; set; } = 0;
        public int RecordingSetupVersion { get; set; } = 0;
        public int WebProtectionSetupVersion { get; set; }
        public int DeletedVideoVisibilitySetupVersion { get; set; }
        /// <summary>磁盘预留口径版本：1 起不再沿用历史"容量上限"反推出来的预留值</summary>
        public int StorageReserveSchemaVersion { get; set; }

        // 录像方式："CameraMonitor"=使用电脑摄像头录像，"PrintStation"=不使用电脑摄像头（兼容旧配置），空值表示首次启动需要选择。
        public string WorkstationRole { get; set; } = "";
        // 主程序实际运行目录。发布包中指向 app 目录，供手动增量更新包定位安装目标。
        public string AppRootDirectory { get; set; } = "";
        public string PrintStationMonitorAddress { get; set; } = "";
        public bool FirstUseWizardCompleted { get; set; } = false;

        // 当前打包模式："发货" 或 "退货"，用于重启后恢复手动/指令码切换结果。
        public string RecordingMode { get; set; } = "发货";

        // 核心：多磁盘配置列表
        public List<StorageLocation> StorageLocations { get; set; } = CreateDefaultStorageLocations();

        public string CameraMonikerString { get; set; } = "";
        public int CameraIndex { get; set; } = 0; // 保留作为回退
        public bool CameraRotate180 { get; set; }
        // 主摄旋转角度（0/90/180/270）。历史字段 CameraRotate180 继续保留：
        // 加载时按它推导角度，保存时同步写回，降级回旧版本时至少不会把画面转丢。
        public int CameraRotationDegrees { get; set; } = UnsetRotationDegrees;
        // 摄像头来源："usb"=本地 USB/内置摄像头，"network"=网络摄像头（RTSP/RTMP/HTTP 流）。
        public string CameraSourceKind { get; set; } = "usb";
        public string NetworkCameraUrl { get; set; } = "";
        public string NetworkCameraRtspTransport { get; set; } = "tcp";

        // 叠加画面路数：高级设置里可以改（默认两路）。不改路数时永远按这个值补齐/截断，
        // 设置页与运行时都按它循环，加第三、第四路不需要另写一套逻辑。
        public int OverlayChannelCount { get; set; } = DefaultOverlayChannelCount;

        // 叠加画面（画中画）：每个元素是一路，设置页一张卡。
        // 没接的那一路来源就是"无"（来源即开关），不用的那一路不需要删掉。
        // 必须是**另一台**物理设备：同一台 USB 摄像头被两路同时打开时设备是独占的，
        // 会有一路拿不到画面甚至被判掉线。
        // 主摄像头不在这里：它是录像主链路，用的仍是上面的主摄字段。
        public List<CameraChannelConfig> CameraChannels { get; set; } = CreateDefaultCameraChannels();

        /// <summary>默认给满 DefaultOverlayChannelCount 路空通道：设置页显示这几张卡，都是"无"。</summary>
        private static List<CameraChannelConfig> CreateDefaultCameraChannels()
        {
            var channels = new List<CameraChannelConfig>(DefaultOverlayChannelCount);
            for (int i = 0; i < DefaultOverlayChannelCount; i++)
                channels.Add(new CameraChannelConfig());
            return channels;
        }

        // 「摄像头自动识别面单」读哪一路画面：0 = 主摄像头（默认，行为与从前一致），
        // 1..n = 第 n 路叠加画面（专门对准面单的那台机位）。
        // 叠加画面没出帧时会自动回退主画面，避免选了那一路又连不上就完全无法识别。
        public int CameraBarcodeRecognitionChannel { get; set; }

        // 存储不同摄像头的配置：Key 为 MonikerString
        public Dictionary<string, CameraSettings> CameraConfigs { get; set; } = new();

        public int FrameWidth { get; set; } = 1280;
        public int FrameHeight { get; set; } = 720;
        public int Fps { get; set; } = 15;
        // 高清源色度矩阵校正。DirectShow 固定按 BT.601 解码，而 720p 及以上的源普遍是 BT.709，
        // 不校正会整体偏灰。auto=宽度达到 1280 自动校正，bt709=强制校正，bt601=不校正，off=关闭。
        public string CameraColorMatrix { get; set; } = "auto";

        /// <summary>
        /// 摄像头采集后端。
        ///
        /// auto（默认）：优先 Media Foundation，探测不到首帧时自动回退 AForge/DirectShow。
        /// mediafoundation：强制新后端，失败时仍会回退（绝不能因为后端问题录不了像）。
        /// directshow：强制旧后端，用于新后端在某台设备上表现异常时的出口。
        ///
        /// 新后端直接申请摄像头原生格式（YUY2/NV12）自己转 BGR，不经由 DirectShow
        /// 固定按 BT.601 的系统转换器（高清源发灰的根因），还能读出设备声明的色彩空间
        /// 而不必按分辨率猜；实测同一台设备 MF 能看到 32 种格式、AForge 只报 1 种。
        /// </summary>
        public string CameraBackend { get; set; } = "auto";
        public bool EnableSmartZoom { get; set; } = false;
        public double MaxZoomScale { get; set; } = 1.5;
        public double ZoomDelaySeconds { get; set; } = 0.0;
        public double ZoomDurationSeconds { get; set; } = DefaultZoomDurationSeconds;
        public bool EnableZoomAnimation { get; set; } = true;
        public double ZoomAnimationDurationMs { get; set; } = 200.0;
        public bool EnableAutoStop { get; set; } = true;
        public double AutoStopMinutes { get; set; } = 1.0;
        public bool EnableMaxDuration { get; set; } = false;
        public double MaxDurationMinutes { get; set; } = 5.0;
        public double MinRecordingSeconds { get; set; } = 3.0;
        public int MinVideoFileSizeKB { get; set; } = 50;
        public bool EnableCameraIdle { get; set; } = false;
        // 关闭实时预览：只停预览发布，录像、条码识别与运动检测照常，用于省 GPU/CPU
        public bool DisableLivePreview { get; set; } = false;
        public bool EnableCameraBarcodeRecognition { get; set; } = false;
        public bool EnableSameBarcodeStopRecording { get; set; } = false;
        public bool EnableEventRecordingBuffer { get; set; } = false;
        // 原始帧预录容量，按当前摄像头规格换算为预计秒数。0 表示不预录，只保留同码收尾
        public int PreRecordBufferMB { get; set; } = -1;
        // 旧版本兼容字段，不再用于运行时设置
        public double PreRecordSeconds { get; set; }
        public double SameCodePostRecordSeconds { get; set; } = 1.5;
        public string CameraBarcodeRecognitionSpeed { get; set; } = CameraBarcodeSpeed.Standard;
        public double CameraBarcodeGuideWidthRatio { get; set; } = 0.85;
        public double CameraBarcodeGuideHeightRatio { get; set; } = 0.85;
        public double CameraBarcodeGuideOffsetX { get; set; } = 0;
        public double CameraBarcodeGuideOffsetY { get; set; } = 0;
        // 识别框默认锁住，避免在主页面上被误拖动
        public bool CameraBarcodeGuideLocked { get; set; } = true;
        public double CameraBarcodeRearmSeconds { get; set; } = 3.0;
        public double CameraSameBarcodeConfirmationSeconds { get; set; } = 2.0;
        public int CameraSameBarcodeConfirmationHits { get; set; } = 2;
        public double CameraIdleMinutes { get; set; } = 5.0;
        public string CameraIdleNoSleepStart1 { get; set; } = "";
        public string CameraIdleNoSleepEnd1 { get; set; } = "";
        public string CameraIdleNoSleepStart2 { get; set; } = "";
        public string CameraIdleNoSleepEnd2 { get; set; } = "";

        public double MotionDetectThreshold { get; set; } = 15.0;
        public string OrderIdRegex { get; set; } = "^[a-zA-Z0-9-]{12,25}$";
        public bool EnableSoundPrompt { get; set; } = true;
        public bool MaximizeVolumeForSpeech { get; set; } = true;
        public double TimeoutWarningSeconds { get; set; } = 10.0;
        public string Theme { get; set; } = "Auto";
        public string Language { get; set; } = AppLanguage.Auto;
        public string WindowCloseBehavior { get; set; } = WindowCloseBehaviors.Ask;
        public bool ShowAdvancedSettings { get; set; } = false;
        public bool ShowDeletedVideos { get; set; } = false;
        public bool AutoStartOnBoot { get; set; } = true;
        public bool EnableAutoCheckUpdate { get; set; } = true;
        // 仅用于显卡或虚拟显示驱动导致窗口全白/全黑的机器；进程级设置，改动后需重启程序。
        public bool ForceSoftwareRendering { get; set; } = false;
        public bool EnableAudioRecording { get; set; } = true;
        public bool EnableDirectAacRecording { get; set; } = false;
        public string AudioDeviceName { get; set; } = "";
        public string AudioDeviceMoniker { get; set; } = "";
        // 播报输出端点。留空表示跟随系统默认扬声器，与历史行为一致。
        public string PlaybackDeviceName { get; set; } = "";
        public string PlaybackDeviceMoniker { get; set; } = "";
        public int AudioSyncOffsetMs { get; set; } = 0;
        // 悬浮小窗上次停靠的角落名，只记角落不记坐标，换分辨率或换显示器也不会跑到屏幕外。
        public string FloatingPreviewCorner { get; set; } = "BottomRight";
        public double BarcodeCooldownSeconds { get; set; } = 2.0;
        public string GpuEncoder { get; set; } = "auto";
        public string VideoCodec { get; set; } = "h265"; // "h264" or "h265"
        public int VideoCqp { get; set; } = DefaultVideoCqp;

        // 全局键盘监听（后台接收扫码枪）
        public bool EnableGlobalKeyboard { get; set; } = true;
        public string TrayKeyboardListeningBehavior { get; set; } = TrayKeyboardListeningBehaviors.Ask;
        public bool EnableScannerAutoSubmit { get; set; } = false;
        public int ScannerAutoSubmitMinLength { get; set; } = 12;
        public int ScannerAutoSubmitQuietMs { get; set; } = 220;
        public int ScannerAutoSubmitMaxAverageIntervalMs { get; set; } = 30;
        public int ScannerAutoSubmitMaxKeyIntervalMs { get; set; } = 50;

        // 水印
        public bool EnableWatermark { get; set; } = true;
        public bool EnableThirdPartyWatermark { get; set; } = true;
        public bool EnableExtensionApi { get; set; } = false;

        // 局域网 Web 服务
        public bool EnableWebServer { get; set; } = true;
        public int WebServerPort { get; set; } = 5280;
        public int TranscodeCacheMaxMB { get; set; } = 1024;  // 转码缓存上限(MB)，超出后按时间清理最旧的
        public bool RequireWebAccessKey { get; set; } = true;
        public string WebAccessKey { get; set; } = "";
        public string MobileBackupComputerId { get; set; } = "";

        // AI 语音合成
        public bool EnableAiTts { get; set; } = true;
        public string AiTtsEngine { get; set; } = "Edge"; // "Kokoro" or "Edge"
        // 语音引擎同样按语言分别保存；日语默认走联网语音，因为随包发布的离线模型只有中英文词典。
        public string AiTtsEngineZhHans { get; set; } = "";
        public string AiTtsEngineEnUs { get; set; } = "";
        public string AiTtsEngineJaJp { get; set; } = "";
        public int AiTtsSpeakerId { get; set; } = 51;        // 普通播报声线
        public int AiTtsWarningSpeakerId { get; set; } = 50;  // 警告播报声线
        // Kokoro 声线编号同样按语言分别保存；0 表示未设置，由规范化填入该语言的默认值。
        public int AiTtsSpeakerIdZhHans { get; set; }
        public int AiTtsWarningSpeakerIdZhHans { get; set; }
        public int AiTtsSpeakerIdEnUs { get; set; }
        public int AiTtsWarningSpeakerIdEnUs { get; set; }
        public int AiTtsSpeakerIdJaJp { get; set; }
        public int AiTtsWarningSpeakerIdJaJp { get; set; }
        public float AiTtsSpeed { get; set; } = 1.0f;
        public string EdgeTtsVoice { get; set; } = "zh-CN-XiaoxiaoNeural";
        public string EdgeTtsWarningVoice { get; set; } = "zh-CN-YunjianNeural";
        public string EdgeTtsVoiceZhHans { get; set; } = "";
        public string EdgeTtsWarningVoiceZhHans { get; set; } = "";
        public string EdgeTtsVoiceEnUs { get; set; } = "en-US-JennyNeural";
        public string EdgeTtsWarningVoiceEnUs { get; set; } = "en-US-GuyNeural";
        public string EdgeTtsVoiceJaJp { get; set; } = "";
        public string EdgeTtsWarningVoiceJaJp { get; set; } = "";

        // Kokoro 使用同一个多语言模型，声线编号由模型决定，各语言默认值目前相同。
        public const int DefaultKokoroSpeakerId = 51;
        public const int DefaultKokoroWarningSpeakerId = 50;

        /// <summary>把界面上选中的在线音色与离线声线写回当前界面语言对应的存档字段。</summary>
        public void StoreSelectedSpeechVoices()
        {
            switch (AppLanguage.Resolve(Language))
            {
                case AppLanguage.Japanese:
                    AiTtsEngineJaJp = AiTtsEngine;
                    EdgeTtsVoiceJaJp = EdgeTtsVoice;
                    EdgeTtsWarningVoiceJaJp = EdgeTtsWarningVoice;
                    AiTtsSpeakerIdJaJp = AiTtsSpeakerId;
                    AiTtsWarningSpeakerIdJaJp = AiTtsWarningSpeakerId;
                    break;
                case AppLanguage.Chinese:
                    AiTtsEngineZhHans = AiTtsEngine;
                    EdgeTtsVoiceZhHans = EdgeTtsVoice;
                    EdgeTtsWarningVoiceZhHans = EdgeTtsWarningVoice;
                    AiTtsSpeakerIdZhHans = AiTtsSpeakerId;
                    AiTtsWarningSpeakerIdZhHans = AiTtsWarningSpeakerId;
                    break;
                default:
                    AiTtsEngineEnUs = AiTtsEngine;
                    EdgeTtsVoiceEnUs = EdgeTtsVoice;
                    EdgeTtsWarningVoiceEnUs = EdgeTtsWarningVoice;
                    AiTtsSpeakerIdEnUs = AiTtsSpeakerId;
                    AiTtsWarningSpeakerIdEnUs = AiTtsWarningSpeakerId;
                    break;
            }
        }

        // 订单备注播报（快递助手插件）
        public bool EnableOrderInfoAnnounce { get; set; } = true;
        public bool AnnounceBuyerMessage { get; set; } = true;
        public bool AnnounceSellerMemo { get; set; } = true;
        public bool AnnounceProductInfo { get; set; } = false;
        public bool AnnounceTotalItemCount { get; set; } = true;
        public bool EnablePrintedRefundAlert { get; set; } = true;
        public bool EnableOrderInfoLog { get; set; } = false;

        // TTS 断句关键词（电商场景，在这些词前自动插入停顿）
        public List<string> TtsBreakWords { get; set; } = new();

        // 缓存的检测结果
        public List<GpuEncoderOption> EncoderOptionsCache { get; set; } = new();
        public List<string> ValidatedEncodersCache { get; set; } = new();
        public List<RecordingBenchmarkCacheEntry> RecordingBenchmarkCache { get; set; } = new();
        public List<EncoderPerformanceCacheEntry> EncoderPerformanceCache { get; set; } = new();
        public bool IsEncoderDetected { get; set; } = false;
        public int EncoderDetectionCacheVersion { get; set; } = 0;
        public string EncoderDriverWarningCode { get; set; } = "";
        public string EncoderDriverRequiredApiVersion { get; set; } = "";
        public string EncoderDriverDetectedApiVersion { get; set; } = "";
        public string EncoderDriverMinimumVersion { get; set; } = "";

        public static bool NormalizeAfterLoad(AppConfig config)
        {
            bool changed = false;

            int normalizedVideoCqp = NormalizeVideoCqp(config.VideoCqp);
            if (config.VideoCqp != normalizedVideoCqp)
            {
                config.VideoCqp = normalizedVideoCqp;
                changed = true;
            }

            string normalizedPreset = DeploymentPresets.Normalize(config.DeploymentPreset);
            if (string.IsNullOrEmpty(normalizedPreset)
                && config.DeploymentSchemaVersion < DeploymentPresets.CurrentSchemaVersion)
            {
                normalizedPreset = DeploymentPresets.FromLegacyRole(config.WorkstationRole);
            }

            if (!string.Equals(config.DeploymentPreset, normalizedPreset, StringComparison.Ordinal))
            {
                config.DeploymentPreset = normalizedPreset;
                changed = true;
            }

            if (DeploymentPresets.IsKnown(normalizedPreset))
            {
                if (config.DeploymentSchemaVersion != DeploymentPresets.CurrentSchemaVersion)
                {
                    config.DeploymentSchemaVersion = DeploymentPresets.CurrentSchemaVersion;
                    changed = true;
                }

                if (!DeploymentCapabilities.ForPreset(normalizedPreset).CanRunWebServer
                    && config.EnableWebServer)
                {
                    config.EnableWebServer = false;
                    changed = true;
                }
            }
            else
            {
                if (config.DeploymentSchemaVersion != 0)
                {
                    config.DeploymentSchemaVersion = 0;
                    changed = true;
                }
                if (config.FirstUseWizardCompleted)
                {
                    config.FirstUseWizardCompleted = false;
                    changed = true;
                }
            }

            string normalizedLanguage = AppLanguage.NormalizePreference(config.Language);
            if (config.Language != normalizedLanguage)
            {
                config.Language = normalizedLanguage;
                changed = true;
            }

            string normalizedRecordingMode = NormalizeRecordingMode(config.RecordingMode);
            if (!string.Equals(config.RecordingMode, normalizedRecordingMode, StringComparison.Ordinal))
            {
                config.RecordingMode = normalizedRecordingMode;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(config.WebAccessKey) || config.WebAccessKey.Trim().Length < 16)
            {
                config.WebAccessKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                changed = true;
            }

            if (config.WebProtectionSetupVersion < CurrentWebProtectionSetupVersion)
            {
                config.RequireWebAccessKey = true;
                config.WebProtectionSetupVersion = CurrentWebProtectionSetupVersion;
                changed = true;
            }
            else if (!string.Equals(config.WebAccessKey, config.WebAccessKey.Trim(), StringComparison.Ordinal))
            {
                config.WebAccessKey = config.WebAccessKey.Trim();
                changed = true;
            }

            if (config.DeletedVideoVisibilitySetupVersion < CurrentDeletedVideoVisibilitySetupVersion)
            {
                config.ShowDeletedVideos = false;
                config.DeletedVideoVisibilitySetupVersion = CurrentDeletedVideoVisibilitySetupVersion;
                changed = true;
            }

            Guid stableNodeId;
            if (Guid.TryParse(config.NodeId, out Guid configuredNodeId) && configuredNodeId != Guid.Empty)
            {
                stableNodeId = configuredNodeId;
            }
            else if (Guid.TryParse(config.MobileBackupComputerId, out Guid existingComputerId)
                && existingComputerId != Guid.Empty)
            {
                stableNodeId = existingComputerId;
            }
            else
            {
                stableNodeId = Guid.NewGuid();
            }

            string normalizedNodeId = stableNodeId.ToString("D");
            if (!string.Equals(config.NodeId, normalizedNodeId, StringComparison.Ordinal))
            {
                config.NodeId = normalizedNodeId;
                changed = true;
            }

            if (!Guid.TryParse(config.MobileBackupComputerId, out Guid computerId) || computerId == Guid.Empty)
            {
                config.MobileBackupComputerId = normalizedNodeId;
                changed = true;
            }
            else
            {
                string normalizedComputerId = computerId.ToString("D");
                if (!string.Equals(config.MobileBackupComputerId, normalizedComputerId, StringComparison.Ordinal))
                {
                    config.MobileBackupComputerId = normalizedComputerId;
                    changed = true;
                }
            }

            string normalizedNodeName = config.NodeName?.Trim() ?? "";
            bool isPcRecorder = DeploymentPresets.IsKnown(normalizedPreset)
                && DeploymentCapabilities.ForPreset(normalizedPreset).CanRecordPcVideo;
            if (isPcRecorder)
            {
                if (!config.NodeNameCustomized
                    && normalizedNodeName.Length > 0
                    && !string.Equals(normalizedNodeName, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                    && !IsAutomaticComputerName(normalizedNodeName))
                {
                    config.NodeNameCustomized = true;
                    changed = true;
                }

                if (!config.NodeNameCustomized
                    && (normalizedNodeName.Length == 0
                        || string.Equals(normalizedNodeName, Environment.MachineName, StringComparison.OrdinalIgnoreCase)))
                {
                    normalizedNodeName = "电脑1";
                }
            }
            else if (normalizedNodeName.Length == 0)
            {
                normalizedNodeName = Environment.MachineName;
            }
            if (!string.Equals(config.NodeName, normalizedNodeName, StringComparison.Ordinal))
            {
                config.NodeName = normalizedNodeName;
                changed = true;
            }

            string normalizedHostNodeId = Guid.TryParse(config.LastKnownHostNodeId, out Guid hostNodeId)
                && hostNodeId != Guid.Empty
                    ? hostNodeId.ToString("D")
                    : "";
            if (!string.Equals(config.LastKnownHostNodeId, normalizedHostNodeId, StringComparison.Ordinal))
            {
                config.LastKnownHostNodeId = normalizedHostNodeId;
                changed = true;
            }
            string normalizedHostNodeName = config.LastKnownHostNodeName?.Trim() ?? "";
            if (!string.Equals(config.LastKnownHostNodeName, normalizedHostNodeName, StringComparison.Ordinal))
            {
                config.LastKnownHostNodeName = normalizedHostNodeName;
                changed = true;
            }
            string normalizedHostAddress = config.LastKnownHostAddress?.Trim().TrimEnd('/') ?? "";
            if (!string.Equals(config.LastKnownHostAddress, normalizedHostAddress, StringComparison.Ordinal))
            {
                config.LastKnownHostAddress = normalizedHostAddress;
                changed = true;
            }
            string normalizedHostAccessKey = config.LastKnownHostAccessKey?.Trim() ?? "";
            if (!string.Equals(config.LastKnownHostAccessKey, normalizedHostAccessKey, StringComparison.Ordinal))
            {
                config.LastKnownHostAccessKey = normalizedHostAccessKey;
                changed = true;
            }
            string normalizedHostWebAccessKey = config.LastKnownHostWebAccessKey?.Trim() ?? "";
            if (!string.Equals(config.LastKnownHostWebAccessKey, normalizedHostWebAccessKey, StringComparison.Ordinal))
            {
                config.LastKnownHostWebAccessKey = normalizedHostWebAccessKey;
                changed = true;
            }

            string normalizedCameraSourceKind = NormalizeCameraSourceKind(
                config.CameraSourceKind,
                config.NetworkCameraUrl);
            if (!string.Equals(config.CameraSourceKind, normalizedCameraSourceKind, StringComparison.Ordinal))
            {
                config.CameraSourceKind = normalizedCameraSourceKind;
                changed = true;
            }
            string normalizedNetworkCameraUrl = config.NetworkCameraUrl?.Trim() ?? "";
            if (!string.Equals(config.NetworkCameraUrl, normalizedNetworkCameraUrl, StringComparison.Ordinal))
            {
                config.NetworkCameraUrl = normalizedNetworkCameraUrl;
                changed = true;
            }
            string normalizedNetworkCameraTransport = NormalizeNetworkTransport(config.NetworkCameraRtspTransport);
            if (!string.Equals(
                    config.NetworkCameraRtspTransport,
                    normalizedNetworkCameraTransport,
                    StringComparison.Ordinal))
            {
                config.NetworkCameraRtspTransport = normalizedNetworkCameraTransport;
                changed = true;
            }

            int resolvedCameraRotation = ResolveRotationDegrees(
                config.CameraRotationDegrees,
                config.CameraRotate180);
            if (config.CameraRotationDegrees != resolvedCameraRotation)
            {
                config.CameraRotationDegrees = resolvedCameraRotation;
                changed = true;
            }

            // 旧版本只认 180：同步这个字段，降级回旧版本时至少保留"倒装"这一种常见情况。
            bool legacyCameraRotate180 = resolvedCameraRotation == 180;
            if (config.CameraRotate180 != legacyCameraRotate180)
            {
                config.CameraRotate180 = legacyCameraRotate180;
                changed = true;
            }

            // 叠加画面（画中画）与主摄同口径归一：来源判定、URL、传输方式、旋转、档位、
            // 识别框、画中画比例与落位。每一路各归一一次，加第三、第四路不用再写一份。
            if (NormalizeCameraChannels(config))
                changed = true;

            // 识别来源是"能不能扫到面单"的关键开关，写坏的通道号必须回到主摄。
            int normalizedBarcodeChannel = NormalizeBarcodeRecognitionChannel(
                config.CameraBarcodeRecognitionChannel,
                config.CameraChannels);
            if (config.CameraBarcodeRecognitionChannel != normalizedBarcodeChannel)
            {
                config.CameraBarcodeRecognitionChannel = normalizedBarcodeChannel;
                changed = true;
            }

            if (normalizedPreset == DeploymentPresets.RecordingWorkstation
                && config.BackupConnectionSchemaVersion < CurrentBackupConnectionSchemaVersion)
            {
                // v3 设备令牌与旧 Web 密钥派生凭据不兼容。保留主机 NodeId 作为
                // 自动重连提示，但清除旧地址和凭据，绝不触碰录像或上传队列。
                config.LastKnownHostAddress = "";
                config.LastKnownHostAccessKey = "";
                config.LastKnownHostBackupAuthVersion = 0;
                config.BackupConnectionSchemaVersion = CurrentBackupConnectionSchemaVersion;
                changed = true;
            }
            string normalizedCachePolicy =
                normalizedPreset == DeploymentPresets.RecordingWorkstation
                    ? "KeepWithinSize"
                    : config.RecordingCachePolicy switch
                    {
                        "DeleteImmediately" => "DeleteImmediately",
                        "KeepWithinSize" => "KeepWithinSize",
                        _ => "KeepDays"
                    };
            if (!string.Equals(config.RecordingCachePolicy, normalizedCachePolicy, StringComparison.Ordinal))
            {
                config.RecordingCachePolicy = normalizedCachePolicy;
                changed = true;
            }
            int normalizedCacheDays = Math.Clamp(config.RecordingCacheKeepDays, 0, 3650);
            if (config.RecordingCacheKeepDays != normalizedCacheDays)
            {
                config.RecordingCacheKeepDays = normalizedCacheDays;
                changed = true;
            }
            int normalizedCacheMaxGB = Math.Clamp(config.RecordingCacheMaxGB, 1, 10240);
            if (config.RecordingCacheMaxGB != normalizedCacheMaxGB)
            {
                config.RecordingCacheMaxGB = normalizedCacheMaxGB;
                changed = true;
            }
            if (normalizedPreset == DeploymentPresets.RecordingWorkstation
                && config.RecordingWorkstationActivatedAtUtc == null)
            {
                config.RecordingWorkstationActivatedAtUtc = DateTime.UtcNow;
                changed = true;
            }

            string normalizedEngine = NormalizeAiTtsEngine(config.AiTtsEngine);
            if (config.AiTtsEngine != normalizedEngine)
            {
                config.AiTtsEngine = normalizedEngine;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(config.EdgeTtsVoice))
            {
                config.EdgeTtsVoice = "zh-CN-XiaoxiaoNeural";
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(config.EdgeTtsWarningVoice))
            {
                config.EdgeTtsWarningVoice = "zh-CN-YunxiNeural";
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(config.EdgeTtsVoiceZhHans))
            {
                config.EdgeTtsVoiceZhHans = config.EdgeTtsVoice;
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(config.EdgeTtsWarningVoiceZhHans))
            {
                config.EdgeTtsWarningVoiceZhHans = config.EdgeTtsWarningVoice;
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(config.EdgeTtsVoiceEnUs))
            {
                config.EdgeTtsVoiceEnUs = "en-US-JennyNeural";
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(config.EdgeTtsWarningVoiceEnUs))
            {
                config.EdgeTtsWarningVoiceEnUs = "en-US-GuyNeural";
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(config.EdgeTtsVoiceJaJp))
            {
                config.EdgeTtsVoiceJaJp = "ja-JP-NanamiNeural";
                changed = true;
            }
            if (string.IsNullOrWhiteSpace(config.EdgeTtsWarningVoiceJaJp))
            {
                config.EdgeTtsWarningVoiceJaJp = "ja-JP-KeitaNeural";
                changed = true;
            }

            string effectiveLanguage = AppLanguage.Resolve(config.Language);

            // 引擎也按语言保存：历史单一值迁到中文/英文槽位，日语默认联网语音。
            string legacyEngine = config.AiTtsEngine;
            if (string.IsNullOrWhiteSpace(config.AiTtsEngineZhHans)) { config.AiTtsEngineZhHans = legacyEngine; changed = true; }
            if (string.IsNullOrWhiteSpace(config.AiTtsEngineEnUs)) { config.AiTtsEngineEnUs = legacyEngine; changed = true; }
            if (string.IsNullOrWhiteSpace(config.AiTtsEngineJaJp)) { config.AiTtsEngineJaJp = "Edge"; changed = true; }
            string normalizedZhEngine = NormalizeAiTtsEngine(config.AiTtsEngineZhHans);
            if (config.AiTtsEngineZhHans != normalizedZhEngine) { config.AiTtsEngineZhHans = normalizedZhEngine; changed = true; }
            string normalizedEnEngine = NormalizeAiTtsEngine(config.AiTtsEngineEnUs);
            if (config.AiTtsEngineEnUs != normalizedEnEngine) { config.AiTtsEngineEnUs = normalizedEnEngine; changed = true; }
            string normalizedJaEngine = NormalizeAiTtsEngine(config.AiTtsEngineJaJp);
            if (config.AiTtsEngineJaJp != normalizedJaEngine) { config.AiTtsEngineJaJp = normalizedJaEngine; changed = true; }
            string effectiveEngine = effectiveLanguage switch
            {
                AppLanguage.Chinese => config.AiTtsEngineZhHans,
                AppLanguage.Japanese => config.AiTtsEngineJaJp,
                _ => config.AiTtsEngineEnUs
            };
            if (config.AiTtsEngine != effectiveEngine) { config.AiTtsEngine = effectiveEngine; changed = true; }

            (string effectiveVoice, string effectiveWarningVoice) = effectiveLanguage switch
            {
                AppLanguage.Chinese => (config.EdgeTtsVoiceZhHans, config.EdgeTtsWarningVoiceZhHans),
                AppLanguage.Japanese => (config.EdgeTtsVoiceJaJp, config.EdgeTtsWarningVoiceJaJp),
                _ => (config.EdgeTtsVoiceEnUs, config.EdgeTtsWarningVoiceEnUs)
            };
            if (config.EdgeTtsVoice != effectiveVoice) { config.EdgeTtsVoice = effectiveVoice; changed = true; }
            if (config.EdgeTtsWarningVoice != effectiveWarningVoice) { config.EdgeTtsWarningVoice = effectiveWarningVoice; changed = true; }

            // Kokoro 声线：先把历史单一值迁到中文槽位，再为每种语言补上各自的默认值。
            int legacySpeakerId = config.AiTtsSpeakerId > 0 ? config.AiTtsSpeakerId : DefaultKokoroSpeakerId;
            int legacyWarningSpeakerId = config.AiTtsWarningSpeakerId > 0
                ? config.AiTtsWarningSpeakerId
                : DefaultKokoroWarningSpeakerId;
            if (config.AiTtsSpeakerIdZhHans <= 0) { config.AiTtsSpeakerIdZhHans = legacySpeakerId; changed = true; }
            if (config.AiTtsWarningSpeakerIdZhHans <= 0) { config.AiTtsWarningSpeakerIdZhHans = legacyWarningSpeakerId; changed = true; }
            if (config.AiTtsSpeakerIdEnUs <= 0) { config.AiTtsSpeakerIdEnUs = DefaultKokoroSpeakerId; changed = true; }
            if (config.AiTtsWarningSpeakerIdEnUs <= 0) { config.AiTtsWarningSpeakerIdEnUs = DefaultKokoroWarningSpeakerId; changed = true; }
            if (config.AiTtsSpeakerIdJaJp <= 0) { config.AiTtsSpeakerIdJaJp = DefaultKokoroSpeakerId; changed = true; }
            if (config.AiTtsWarningSpeakerIdJaJp <= 0) { config.AiTtsWarningSpeakerIdJaJp = DefaultKokoroWarningSpeakerId; changed = true; }

            (int effectiveSpeakerId, int effectiveWarningSpeakerId) = effectiveLanguage switch
            {
                AppLanguage.Chinese => (config.AiTtsSpeakerIdZhHans, config.AiTtsWarningSpeakerIdZhHans),
                AppLanguage.Japanese => (config.AiTtsSpeakerIdJaJp, config.AiTtsWarningSpeakerIdJaJp),
                _ => (config.AiTtsSpeakerIdEnUs, config.AiTtsWarningSpeakerIdEnUs)
            };
            if (config.AiTtsSpeakerId != effectiveSpeakerId) { config.AiTtsSpeakerId = effectiveSpeakerId; changed = true; }
            if (config.AiTtsWarningSpeakerId != effectiveWarningSpeakerId) { config.AiTtsWarningSpeakerId = effectiveWarningSpeakerId; changed = true; }

            if (config.VoiceSettingsVersion < CurrentVoiceSettingsVersion)
            {
                // 旧版把“是否播放提示”和“是否使用 AI 语音”拆成两个开关。
                // 新版合并成“语音提醒”总开关 + “语音引擎”选择；旧用户只要曾启用 AI 语音，就保留语音提醒开启。
                if (config.EnableAiTts && !config.EnableSoundPrompt)
                {
                    config.EnableSoundPrompt = true;
                }

                config.VoiceSettingsVersion = CurrentVoiceSettingsVersion;
                changed = true;
            }

            if (config.StorageLocations == null)
            {
                config.StorageLocations = new List<StorageLocation>();
                changed = true;
            }

            if (RemoveUnusableStorageLocations(config.StorageLocations, ReadOnlySystemRoot))
                changed = true;

            if (config.StorageLocations.Count == 0)
            {
                config.StorageLocations.AddRange(CreateDefaultStorageLocations());
                changed = true;
            }

            if (config.StorageReserveSchemaVersion < CurrentStorageReserveSchemaVersion)
            {
                // 历史设置页按"容量上限"呈现，用户把它理解成"本软件最多占用多少"，
                // 反推出来的预留值会让整块盘在其它软件占用空间后就被判成不可用；
                // 这些值一律清掉，改回默认预留（只有用户显式设置过才再带上预留）
                foreach (StorageLocation location in config.StorageLocations)
                    location.ReserveGB = 0;

                config.StorageReserveSchemaVersion = CurrentStorageReserveSchemaVersion;
                changed = true;
            }

            string normalizedCloseBehavior = WindowCloseBehaviors.Normalize(config.WindowCloseBehavior);
            if (config.WindowCloseBehavior != normalizedCloseBehavior)
            {
                config.WindowCloseBehavior = normalizedCloseBehavior;
                changed = true;
            }

            string normalizedTrayKeyboardBehavior =
                TrayKeyboardListeningBehaviors.Normalize(config.TrayKeyboardListeningBehavior);
            if (config.TrayKeyboardListeningBehavior != normalizedTrayKeyboardBehavior)
            {
                config.TrayKeyboardListeningBehavior = normalizedTrayKeyboardBehavior;
                changed = true;
            }

            foreach (var location in config.StorageLocations)
            {
                double normalizedReserveGB = StorageSpacePolicy.MigrateLegacyReserveGB(location.Path, location.ReserveGB);
                if (System.Math.Abs(location.ReserveGB - normalizedReserveGB) > 0.001)
                {
                    location.ReserveGB = normalizedReserveGB;
                    changed = true;
                }

                if (StorageLocationMetadata.RefreshVolumeId(location))
                    changed = true;
            }

            if (config.EnableGlobalKeyboard && config.EnableScannerAutoSubmit)
            {
                config.EnableGlobalKeyboard = false;
                changed = true;
            }

            string normalizedCameraBarcodeSpeed = CameraBarcodeSpeed.Normalize(
                config.CameraBarcodeRecognitionSpeed);
            if (!string.Equals(
                    config.CameraBarcodeRecognitionSpeed,
                    normalizedCameraBarcodeSpeed,
                    StringComparison.Ordinal))
            {
                config.CameraBarcodeRecognitionSpeed = normalizedCameraBarcodeSpeed;
                changed = true;
            }

            double normalizedGuideWidth = System.Math.Clamp(
                config.CameraBarcodeGuideWidthRatio,
                0.3,
                1.0);
            double normalizedMaxZoomScale = System.Math.Clamp(config.MaxZoomScale, 1.2, 3.0);
            if (System.Math.Abs(config.MaxZoomScale - normalizedMaxZoomScale) > 0.001)
            {
                config.MaxZoomScale = normalizedMaxZoomScale;
                changed = true;
            }
            double normalizedZoomDurationSeconds = System.Math.Clamp(config.ZoomDurationSeconds, 0.0, 5.0);
            // 旧默认值（3 秒 / 1 秒）会被写进用户配置，统一迁到现在的 2 秒；用户自己调过的其他值保持不动。
            foreach (double legacyDuration in LegacyZoomDurationSeconds)
            {
                if (System.Math.Abs(config.ZoomDurationSeconds - legacyDuration) > 0.001)
                    continue;

                config.ZoomDurationSeconds = DefaultZoomDurationSeconds;
                normalizedZoomDurationSeconds = DefaultZoomDurationSeconds;
                changed = true;
                break;
            }
            if (System.Math.Abs(config.ZoomDurationSeconds - normalizedZoomDurationSeconds) > 0.001)
            {
                config.ZoomDurationSeconds = normalizedZoomDurationSeconds;
                changed = true;
            }
            double normalizedZoomAnimationDurationMs = System.Math.Clamp(config.ZoomAnimationDurationMs, 50.0, 1000.0);
            if (System.Math.Abs(config.ZoomAnimationDurationMs - normalizedZoomAnimationDurationMs) > 0.001)
            {
                config.ZoomAnimationDurationMs = normalizedZoomAnimationDurationMs;
                changed = true;
            }
            if (System.Math.Abs(config.CameraBarcodeGuideWidthRatio - normalizedGuideWidth) > 0.001)
            {
                config.CameraBarcodeGuideWidthRatio = normalizedGuideWidth;
                changed = true;
            }

            double normalizedGuideHeight = System.Math.Clamp(
                config.CameraBarcodeGuideHeightRatio,
                0.3,
                1.0);
            if (System.Math.Abs(config.CameraBarcodeGuideHeightRatio - normalizedGuideHeight) > 0.001)
            {
                config.CameraBarcodeGuideHeightRatio = normalizedGuideHeight;
                changed = true;
            }

            double normalizedGuideOffsetX = System.Math.Clamp(
                config.CameraBarcodeGuideOffsetX,
                -1.0,
                1.0);
            if (System.Math.Abs(config.CameraBarcodeGuideOffsetX - normalizedGuideOffsetX) > 0.001)
            {
                config.CameraBarcodeGuideOffsetX = normalizedGuideOffsetX;
                changed = true;
            }

            double normalizedGuideOffsetY = System.Math.Clamp(
                config.CameraBarcodeGuideOffsetY,
                -1.0,
                1.0);
            if (System.Math.Abs(config.CameraBarcodeGuideOffsetY - normalizedGuideOffsetY) > 0.001)
            {
                config.CameraBarcodeGuideOffsetY = normalizedGuideOffsetY;
                changed = true;
            }

            double normalizedCameraBarcodeRearmSeconds = System.Math.Clamp(
                config.CameraBarcodeRearmSeconds,
                1.0,
                30.0);
            if (System.Math.Abs(config.CameraBarcodeRearmSeconds - normalizedCameraBarcodeRearmSeconds) > 0.001)
            {
                config.CameraBarcodeRearmSeconds = normalizedCameraBarcodeRearmSeconds;
                changed = true;
            }

            double normalizedCameraSameBarcodeConfirmationSeconds = System.Math.Clamp(
                config.CameraSameBarcodeConfirmationSeconds,
                0.5,
                10.0);
            if (System.Math.Abs(config.CameraSameBarcodeConfirmationSeconds - normalizedCameraSameBarcodeConfirmationSeconds) > 0.001)
            {
                config.CameraSameBarcodeConfirmationSeconds = normalizedCameraSameBarcodeConfirmationSeconds;
                changed = true;
            }

            int normalizedCameraSameBarcodeConfirmationHits = System.Math.Clamp(
                config.CameraSameBarcodeConfirmationHits,
                1,
                4);
            if (config.CameraSameBarcodeConfirmationHits != normalizedCameraSameBarcodeConfirmationHits)
            {
                config.CameraSameBarcodeConfirmationHits = normalizedCameraSameBarcodeConfirmationHits;
                changed = true;
            }
            int normalizedPreRecordBufferMB = config.PreRecordBufferMB;
            if (normalizedPreRecordBufferMB < 0 || (normalizedPreRecordBufferMB == 0 && config.PreRecordSeconds > 0))
            {
                if (config.PreRecordSeconds <= 0)
                {
                    normalizedPreRecordBufferMB = PreRecordBufferPolicy.GetRecommendedDefaultMb(
                        PreRecordBufferPolicy.GetPhysicalMemoryBytes());
                }
                else
                {
                // 旧版本只保存秒数，按当时的录像规格尽量保留用户意图；全新配置使用约 1 秒的默认容量
                double legacySeconds = Math.Clamp(config.PreRecordSeconds, 0, 5);
                long bytesPerSecond = (long)Math.Max(1, config.FrameWidth)
                    * Math.Max(1, config.FrameHeight)
                    * 3L
                    * Math.Max(1, config.Fps);
                long migratedBytes = legacySeconds > 0
                    ? (long)Math.Ceiling(bytesPerSecond * legacySeconds)
                    : 320L * 1024 * 1024;
                normalizedPreRecordBufferMB = (int)Math.Clamp(
                    (migratedBytes + (1024L * 1024) - 1) / (1024L * 1024),
                    0,
                    1024);
                }
            }
            ulong physicalMemoryBytes = PreRecordBufferPolicy.GetPhysicalMemoryBytes();
            normalizedPreRecordBufferMB = PreRecordBufferPolicy.ClampConfiguredMb(
                normalizedPreRecordBufferMB,
                config.FrameWidth,
                config.FrameHeight,
                config.Fps,
                physicalMemoryBytes);
            if (config.PreRecordBufferMB != normalizedPreRecordBufferMB)
            {
                config.PreRecordBufferMB = normalizedPreRecordBufferMB;
                changed = true;
            }
            if (config.PreRecordSeconds != 0)
            {
                config.PreRecordSeconds = 0;
                changed = true;
            }
            double normalizedSameCodePostRecordSeconds = Math.Clamp(config.SameCodePostRecordSeconds, 0, 5);
            if (Math.Abs(config.SameCodePostRecordSeconds - normalizedSameCodePostRecordSeconds) > 0.001)
            {
                config.SameCodePostRecordSeconds = normalizedSameCodePostRecordSeconds;
                changed = true;
            }

            int normalizedMinLength = System.Math.Clamp(config.ScannerAutoSubmitMinLength, 4, 30);
            if (config.ScannerAutoSubmitMinLength != normalizedMinLength)
            {
                config.ScannerAutoSubmitMinLength = normalizedMinLength;
                changed = true;
            }

            int normalizedQuietMs = System.Math.Clamp(config.ScannerAutoSubmitQuietMs, 120, 600);
            if (config.ScannerAutoSubmitQuietMs != normalizedQuietMs)
            {
                config.ScannerAutoSubmitQuietMs = normalizedQuietMs;
                changed = true;
            }

            int normalizedAverageMs = System.Math.Clamp(config.ScannerAutoSubmitMaxAverageIntervalMs, 10, 100);
            if (config.ScannerAutoSubmitMaxAverageIntervalMs != normalizedAverageMs)
            {
                config.ScannerAutoSubmitMaxAverageIntervalMs = normalizedAverageMs;
                changed = true;
            }

            int normalizedKeyIntervalMs = System.Math.Clamp(config.ScannerAutoSubmitMaxKeyIntervalMs, 20, 150);
            if (config.ScannerAutoSubmitMaxKeyIntervalMs != normalizedKeyIntervalMs)
            {
                config.ScannerAutoSubmitMaxKeyIntervalMs = normalizedKeyIntervalMs;
                changed = true;
            }

            return changed;
        }

        public static int NormalizeVideoCqp(int videoCqp) =>
            Math.Clamp(
                videoCqp > 0 ? videoCqp : DefaultVideoCqp,
                HighestQualityVideoCqp,
                LowestQualityVideoCqp);

        internal static string NormalizeRecordingMode(string? mode) =>
            string.Equals(mode?.Trim(), "退货", StringComparison.Ordinal)
                ? "退货"
                : "发货";

        internal static bool IsAutomaticComputerName(string? value)
        {
            string name = value?.Trim() ?? "";
            return name.StartsWith("电脑", StringComparison.Ordinal)
                && int.TryParse(name["电脑".Length..], out int number)
                && number > 0;
        }

        /// <summary>
        /// 副画面位置归一：拖动过就是一个 0~1 的比例，未拖动/非法值统一回到"自动右下角"。
        /// 用哨兵而不是 0，是为了让"贴左上角"和"还没动过"区分开。
        /// </summary>
        internal static double NormalizeOverlayPosition(double value) =>
            double.IsFinite(value) && value >= 0
                ? Math.Clamp(value, 0.0, 1.0)
                : UnsetOverlayPosition;

        /// <summary>
        /// 识别来源通道号归一：0 = 主摄像头，1..n = 第 n 路副摄像头；
        /// 超出当前路数、或者这一路已经被设成"无"，一律回到主摄 ——
        /// 界面上没接设备的那一路也不该还能被选成识别来源。
        /// 识别来源是"能不能扫到面单"的关键开关，写错不能变成两边都不识别。
        /// </summary>
        internal static int NormalizeBarcodeRecognitionChannel(
            int channelNumber,
            IReadOnlyList<CameraChannelConfig> channels)
        {
            if (channelNumber <= 0 || channels == null)
                return 0;

            int index = channelNumber - 1;
            return index < channels.Count && channels[index].IsConfigured ? channelNumber : 0;
        }

        /// <summary>
        /// 叠加画面采集规格预设归一：只认白名单，写坏或旧配置一律回落到 720p。
        /// 这里不做"猜一个相近值"，非法值静默变成默认规格比卡在非法尺寸上更容易解释。
        /// </summary>
        internal static string NormalizeOverlayResolutionPreset(string? preset) =>
            preset?.Trim().ToLowerInvariant() switch
            {
                "480p" => "480p",
                "1080p" => "1080p",
                _ => DefaultOverlayResolutionPreset,
            };

        /// <summary>把叠加画面规格预设解析成实际采集宽高。</summary>
        internal static (int Width, int Height) ResolveOverlayFrameSize(string? preset) =>
            NormalizeOverlayResolutionPreset(preset) switch
            {
                "480p" => (640, 480),
                "1080p" => (1920, 1080),
                _ => (1280, 720),
            };

        /// <summary>
        /// 叠加画面实际采集宽高：优先用设置页从设备枚举出来的档位（与主摄同一套口径），
        /// 没枚举过（0/0）才回落到预设。两个字段必须成对有效，避免半个尺寸。
        /// </summary>
        internal static (int Width, int Height) ResolveOverlayFrameSize(
            string? preset,
            int explicitWidth,
            int explicitHeight) =>
            explicitWidth > 0 && explicitHeight > 0
                ? (explicitWidth, explicitHeight)
                : ResolveOverlayFrameSize(preset);

        /// <summary>把实际采集尺寸回填成预设名；非标准尺寸回落到默认预设。</summary>
        internal static string PresetForSize(int width, int height) =>
            (width, height) switch
            {
                (640, 480) => "480p",
                (1280, 720) => "720p",
                (1920, 1080) => "1080p",
                _ => DefaultOverlayResolutionPreset,
            };

        /// <summary>
        /// 叠加画面来源归一：只认"无/本机/网络"三种，其余取值按"有地址就当网络摄像头、
        /// 否则这一路不接"处理。绝不因为一个写坏的值就猜成"随便开一台本机摄像头"——
        /// 那会让画面凭空多出一路，还会和主摄抢设备。
        /// </summary>
        internal static string NormalizeOverlayChannelSourceKind(string? kind, string? networkCameraUrl)
        {
            string trimmed = kind?.Trim() ?? "";
            if (string.Equals(trimmed, OverlayChannelSourceNone, StringComparison.OrdinalIgnoreCase))
                return OverlayChannelSourceNone;
            if (string.Equals(trimmed, "network", StringComparison.OrdinalIgnoreCase))
                return "network";
            if (string.Equals(trimmed, "usb", StringComparison.OrdinalIgnoreCase))
                return "usb";

            return string.IsNullOrWhiteSpace(networkCameraUrl) ? OverlayChannelSourceNone : "network";
        }

        /// <summary>叠加画面识别框尺寸占比归一：与主摄同一套区间。</summary>
        internal static double NormalizeOverlayGuideRatio(double value) =>
            double.IsFinite(value) && value > 0
                ? Math.Clamp(value, MinimumOverlayGuideRatio, MaximumOverlayGuideRatio)
                : DefaultOverlayGuideRatio;

        /// <summary>
        /// 叠加画面归一：路数跟着 <see cref="AppConfig.OverlayChannelCount"/> 走 ——
        /// 少了就补齐（没接也算一路，设置页才有卡片可显示），多了就截断（改小路数或手改过配置）。
        /// 每一路都按与主摄同口径的规则归一。
        /// 返回 true 表示有字段被改写。
        /// </summary>
        internal static bool NormalizeCameraChannels(AppConfig config)
        {
            bool changed = false;
            config.CameraChannels ??= new List<CameraChannelConfig>();

            int channelCount = NormalizeOverlayChannelCount(config.OverlayChannelCount);
            if (config.OverlayChannelCount != channelCount)
            {
                config.OverlayChannelCount = channelCount;
                changed = true;
            }

            if (config.CameraChannels.Count > channelCount)
            {
                config.CameraChannels.RemoveRange(
                    channelCount,
                    config.CameraChannels.Count - channelCount);
                changed = true;
            }

            // 设置页显示"副画面 1..N"这么多张卡，不接的那一路来源就是"无"。
            while (config.CameraChannels.Count < channelCount)
            {
                config.CameraChannels.Add(new CameraChannelConfig());
                changed = true;
            }

            foreach (CameraChannelConfig channel in config.CameraChannels)
                changed |= NormalizeCameraChannel(channel);

            return changed;
        }

        /// <summary>叠加画面路数归一：越界的取值按可配置区间夹紧。</summary>
        internal static int NormalizeOverlayChannelCount(int value) =>
            Math.Clamp(value, MinimumOverlayChannelCount, MaximumOverlayChannelCount);

        /// <summary>一路叠加画面的归一。返回 true 表示有字段被改写。</summary>
        internal static bool NormalizeCameraChannel(CameraChannelConfig channel)
        {
            bool changed = false;

            string sourceKind = NormalizeOverlayChannelSourceKind(channel.SourceKind, channel.NetworkCameraUrl);
            if (!string.Equals(channel.SourceKind, sourceKind, StringComparison.Ordinal))
            {
                channel.SourceKind = sourceKind;
                changed = true;
            }

            string url = channel.NetworkCameraUrl?.Trim() ?? "";
            if (!string.Equals(channel.NetworkCameraUrl, url, StringComparison.Ordinal))
            {
                channel.NetworkCameraUrl = url;
                changed = true;
            }

            string transport = NormalizeNetworkTransport(channel.NetworkCameraRtspTransport);
            if (!string.Equals(channel.NetworkCameraRtspTransport, transport, StringComparison.Ordinal))
            {
                channel.NetworkCameraRtspTransport = transport;
                changed = true;
            }

            int rotation = NormalizeRotationDegrees(channel.RotationDegrees);
            if (channel.RotationDegrees != rotation)
            {
                channel.RotationDegrees = rotation;
                changed = true;
            }

            string preset = NormalizeOverlayResolutionPreset(channel.ResolutionPreset);
            if (!string.Equals(channel.ResolutionPreset, preset, StringComparison.Ordinal))
            {
                channel.ResolutionPreset = preset;
                changed = true;
            }

            int fps = Math.Clamp(
                channel.FrameFps > 0 ? channel.FrameFps : DefaultOverlayFrameFps,
                MinimumOverlayFrameFps,
                MaximumOverlayFrameFps);
            if (channel.FrameFps != fps)
            {
                channel.FrameFps = fps;
                changed = true;
            }

            // 实际采集宽高必须成对有效且在合理范围内；半个尺寸或越界一律清空回落到预设。
            const int maximumFrameDimension = 7680;
            if (channel.FrameWidth < 0
                || channel.FrameHeight < 0
                || channel.FrameWidth > maximumFrameDimension
                || channel.FrameHeight > maximumFrameDimension
                || (channel.FrameWidth > 0) != (channel.FrameHeight > 0))
            {
                channel.FrameWidth = 0;
                channel.FrameHeight = 0;
                changed = true;
            }

            if (!IsCloseEnough(channel.BarcodeGuideWidthRatio, NormalizeOverlayGuideRatio(channel.BarcodeGuideWidthRatio)))
            {
                channel.BarcodeGuideWidthRatio = NormalizeOverlayGuideRatio(channel.BarcodeGuideWidthRatio);
                changed = true;
            }

            if (!IsCloseEnough(channel.BarcodeGuideHeightRatio, NormalizeOverlayGuideRatio(channel.BarcodeGuideHeightRatio)))
            {
                channel.BarcodeGuideHeightRatio = NormalizeOverlayGuideRatio(channel.BarcodeGuideHeightRatio);
                changed = true;
            }

            double offsetX = NormalizeGuideOffset(channel.BarcodeGuideOffsetX);
            if (!IsCloseEnough(channel.BarcodeGuideOffsetX, offsetX))
            {
                channel.BarcodeGuideOffsetX = offsetX;
                changed = true;
            }

            double offsetY = NormalizeGuideOffset(channel.BarcodeGuideOffsetY);
            if (!IsCloseEnough(channel.BarcodeGuideOffsetY, offsetY))
            {
                channel.BarcodeGuideOffsetY = offsetY;
                changed = true;
            }

            double widthRatio = double.IsFinite(channel.OverlayWidthRatio) && channel.OverlayWidthRatio > 0
                ? Math.Clamp(channel.OverlayWidthRatio, MinimumOverlayWidthRatio, MaximumOverlayWidthRatio)
                : DefaultOverlayWidthRatio;
            if (!IsCloseEnough(channel.OverlayWidthRatio, widthRatio))
            {
                channel.OverlayWidthRatio = widthRatio;
                changed = true;
            }

            int margin = channel.OverlayMargin >= 0
                ? Math.Min(channel.OverlayMargin, MaximumOverlayMargin)
                : DefaultOverlayMargin;
            if (channel.OverlayMargin != margin)
            {
                channel.OverlayMargin = margin;
                changed = true;
            }

            double left = NormalizeOverlayPosition(channel.OverlayLeftRatio);
            if (!IsCloseEnough(channel.OverlayLeftRatio, left))
            {
                channel.OverlayLeftRatio = left;
                changed = true;
            }

            double top = NormalizeOverlayPosition(channel.OverlayTopRatio);
            if (!IsCloseEnough(channel.OverlayTopRatio, top))
            {
                channel.OverlayTopRatio = top;
                changed = true;
            }

            return changed;
        }

        /// <summary>旋转角度归一：只认 0/90/180/270，其余（含手改坏的值）按不旋转处理。</summary>
        internal static int NormalizeRotationDegrees(int degrees) => degrees switch
        {
            90 => 90,
            180 => 180,
            270 => 270,
            _ => 0,
        };

        /// <summary>浮点归一比较：NaN 也要算"需要改写"，否则 NaN 会一直留在配置里。</summary>
        private static bool IsCloseEnough(double value, double normalized) =>
            double.IsFinite(value) && Math.Abs(value - normalized) <= 0.001;
        /// <summary>副摄识别框偏移归一：0 表示居中，±1 表示贴边。</summary>
        internal static double NormalizeGuideOffset(double value) =>
            double.IsFinite(value) ? Math.Clamp(value, -1.0, 1.0) : 0.0;

        /// <summary>
        /// 旋转角度归一：未设置（老配置）时按旧的"旋转 180°"开关推导；
        /// 其余非法值一律回到不旋转，绝不让一个手改坏的配置把画面转歪。
        /// </summary>
        internal static int ResolveRotationDegrees(int degrees, bool legacyRotate180) =>
            degrees == UnsetRotationDegrees
                ? (legacyRotate180 ? 180 : 0)
                : degrees switch
                {
                    90 => 90,
                    180 => 180,
                    270 => 270,
                    _ => 0,
                };

        /// <summary>把某台设备记忆的旋转角度写回运行配置；老配置里只有 180° 开关。</summary>
        internal static void ApplyRotation(AppConfig config, CameraSettings settings)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(settings);
            config.CameraRotationDegrees = ResolveRotationDegrees(
                settings.RotationDegrees,
                settings.Rotate180);
        }

        internal static string NormalizeCameraSourceKind(string? kind, string? networkCameraUrl)
        {
            if (string.Equals(kind, "network", StringComparison.OrdinalIgnoreCase))
                return "network";
            if (string.Equals(kind, "usb", StringComparison.OrdinalIgnoreCase))
                return "usb";
            return string.IsNullOrWhiteSpace(networkCameraUrl) ? "usb" : "network";
        }

        internal static string NormalizeNetworkTransport(string? transport)
        {
            return string.Equals(transport, "udp", StringComparison.OrdinalIgnoreCase) ? "udp" : "tcp";
        }

        internal static string GetCameraConfigKey(string? sourceKind, string? monikerOrUrl)
        {
            string value = monikerOrUrl?.Trim() ?? "";
            string kind = NormalizeCameraSourceKind(sourceKind, value);
            return kind == "network" ? "network:" + value : value;
        }

        /// <summary>
        /// 判断摄像头配置变化是否需要重新建立 Camera Source/Session，
        /// 而不是判断两个 AppConfig 是否任意不同。
        /// </summary>
        internal static bool RequiresCameraRestart(AppConfig current, AppConfig next)
        {
            ArgumentNullException.ThrowIfNull(current);
            ArgumentNullException.ThrowIfNull(next);

            string currentKind = NormalizeCameraSourceKind(current.CameraSourceKind, current.NetworkCameraUrl);
            string nextKind = NormalizeCameraSourceKind(next.CameraSourceKind, next.NetworkCameraUrl);
            string currentUrl = current.NetworkCameraUrl?.Trim() ?? "";
            string nextUrl = next.NetworkCameraUrl?.Trim() ?? "";
            string currentTransport = NormalizeNetworkTransport(current.NetworkCameraRtspTransport);
            string nextTransport = NormalizeNetworkTransport(next.NetworkCameraRtspTransport);

            return current.CameraIndex != next.CameraIndex
                || !string.Equals(current.CameraMonikerString, next.CameraMonikerString, StringComparison.Ordinal)
                || current.FrameWidth != next.FrameWidth
                || current.FrameHeight != next.FrameHeight
                || current.Fps != next.Fps
                || ResolveRotationDegrees(current.CameraRotationDegrees, current.CameraRotate180)
                    != ResolveRotationDegrees(next.CameraRotationDegrees, next.CameraRotate180)
                || !string.Equals(currentKind, nextKind, StringComparison.Ordinal)
                || !string.Equals(currentUrl, nextUrl, StringComparison.Ordinal)
                || (currentKind == "network"
                    && nextKind == "network"
                    && !string.Equals(currentTransport, nextTransport, StringComparison.Ordinal));
        }

        /// <summary>
        /// 副画面那几路是否需要按新配置重开采集。
        ///
        /// 单独拆出来是因为"重启主摄"和"重启副画面"是两件事：主摄的流跟副画面无关，
        /// 每次保存设置（哪怕只改了预录容量）都把主摄和副画面一起掐断一秒，
        /// 预录里就会留下一段没有副画面的接缝。
        /// </summary>
        internal static bool OverlayChannelsRequireRestart(AppConfig current, AppConfig next)
        {
            ArgumentNullException.ThrowIfNull(current);
            ArgumentNullException.ThrowIfNull(next);

            if (current.OverlayChannelCount != next.OverlayChannelCount
                || current.CameraChannels.Count != next.CameraChannels.Count)
            {
                return true;
            }

            for (int i = 0; i < current.CameraChannels.Count; i++)
            {
                if (OverlayChannelRequiresRestart(current.CameraChannels[i], next.CameraChannels[i]))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 某一路叠加画面的变化是否需要重开采集：来源、设备、地址、档位、旋转变了才算。
        /// 画中画的位置和大小不影响采集，改它们不该把摄像头重启一遍。
        /// </summary>
        private static bool OverlayChannelRequiresRestart(CameraChannelConfig current, CameraChannelConfig next)
        {
            string currentKind = NormalizeOverlayChannelSourceKind(current.SourceKind, current.NetworkCameraUrl);
            string nextKind = NormalizeOverlayChannelSourceKind(next.SourceKind, next.NetworkCameraUrl);
            string currentUrl = current.NetworkCameraUrl?.Trim() ?? "";
            string nextUrl = next.NetworkCameraUrl?.Trim() ?? "";

            return current.Index != next.Index
                || !string.Equals(current.MonikerString, next.MonikerString, StringComparison.Ordinal)
                || current.RotationDegrees != next.RotationDegrees
                || !string.Equals(
                    NormalizeOverlayResolutionPreset(current.ResolutionPreset),
                    NormalizeOverlayResolutionPreset(next.ResolutionPreset),
                    StringComparison.Ordinal)
                || current.FrameFps != next.FrameFps
                || current.FrameWidth != next.FrameWidth
                || current.FrameHeight != next.FrameHeight
                || !string.Equals(currentKind, nextKind, StringComparison.Ordinal)
                || !string.Equals(currentUrl, nextUrl, StringComparison.Ordinal)
                || (currentKind == "network"
                    && nextKind == "network"
                    && !string.Equals(
                        NormalizeNetworkTransport(current.NetworkCameraRtspTransport),
                        NormalizeNetworkTransport(next.NetworkCameraRtspTransport),
                        StringComparison.Ordinal));
        }

        /// <summary>
        /// 只读系统卷根：macOS 的 "/" 既不允许建目录，也不是存录像的地方。
        /// Windows 没有这种卷，返回 null 表示不做此限制。
        /// </summary>
        private static string? ReadOnlySystemRoot => OperatingSystem.IsMacOS() ? "/" : null;

        /// <summary>
        /// 清掉落在只读系统卷根下的保存位置。旧版把 Windows 默认值（D:\快递打包视频 一类）
        /// 在 macOS 上归一化成 /快递打包视频，而 "/" 是只读系统卷，这个目录永远建不出来，
        /// 留着它会让保存主机每次启动都失败、菜单栏一直显示"启动中"。
        /// 只是移除无效项，改由默认位置规则重建；其它平台的配置一律不动。
        /// </summary>
        internal static bool RemoveUnusableStorageLocations(
            List<StorageLocation> locations,
            string? readOnlySystemRoot)
        {
            if (string.IsNullOrWhiteSpace(readOnlySystemRoot)) return false;

            return locations.RemoveAll(location =>
                IsDirectChildOfRoot(location.Path, readOnlySystemRoot)) > 0;
        }

        /// <summary>
        /// 是否直接挂在给定根下："/快递打包视频"挂在"/"下，
        /// 外接盘上的"/Volumes/盘/快递打包视频"不算，避免误删真实保存位置。
        /// </summary>
        private static bool IsDirectChildOfRoot(string? path, string root)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            try
            {
                string parent = Path.GetDirectoryName(Path.GetFullPath(path.Trim())) ?? "";
                return string.Equals(
                    TrimTrailingSeparators(parent),
                    TrimTrailingSeparators(Path.GetFullPath(root)),
                    StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>去掉结尾分隔符后再比较："/" 与 "/" 相等，"/Volumes/盘/" 与 "/Volumes/盘" 相等</summary>
        private static string TrimTrailingSeparators(string path) =>
            path.Length <= 1
                ? path
                : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private static List<StorageLocation> CreateDefaultStorageLocations()
        {
            try
            {
                return CreateDefaultStorageLocations(
                    DriveInfo.GetDrives().Select(drive =>
                        new StorageDriveCandidate(drive.Name, drive.IsReady, drive.DriveType)),
                    ReadOnlySystemRoot);
            }
            catch
            {
                return CreateDefaultStorageLocations(
                    Array.Empty<StorageDriveCandidate>(),
                    ReadOnlySystemRoot);
            }
        }

        internal static List<StorageLocation> CreateDefaultStorageLocations(
            IEnumerable<StorageDriveCandidate> drives) =>
            CreateDefaultStorageLocations(drives, ReadOnlySystemRoot);

        /// <summary>
        /// 每个可用本地盘一个"快递打包视频"目录。readOnlySystemRoot 非空时该根不参与默认位置
        /// （macOS 传 "/"：默认值会变成一条永远建不出来的配置）；fallbackRoot 是测试注入用的
        /// 兜底根，正常调用按平台取。
        /// </summary>
        internal static List<StorageLocation> CreateDefaultStorageLocations(
            IEnumerable<StorageDriveCandidate> drives,
            string? readOnlySystemRoot,
            string? fallbackRoot = null)
        {
            var roots = drives
                .Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed)
                .Select(drive => Path.GetPathRoot(drive.RootPath) ?? drive.RootPath)
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Where(root => !IsSamePath(root, readOnlySystemRoot))
                .Select(root => root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(root => root, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (roots.Count == 0)
                roots.Add(fallbackRoot ?? DefaultStorageRoot);

            return roots
                .Select((root, index) =>
                {
                    string path = Path.Combine(root, DefaultStorageFolderName);
                    return new StorageLocation
                    {
                        Path = path,
                        ReserveGB = StorageSpacePolicy.GetDefaultReserveGB(path),
                        Priority = index
                    };
                })
                .ToList();
        }

        /// <summary>没有可用本地盘时的默认根：Windows 用系统盘，其它平台用个人影片目录</summary>
        internal static string DefaultStorageRoot =>
            OperatingSystem.IsWindows()
                ? Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\"
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Movies") + Path.DirectorySeparatorChar;

        /// <summary>保存位置统一用的文件夹名：每块盘都是"&lt;盘根&gt;\快递打包视频"，不分系统盘还是数据盘。</summary>
        internal const string DefaultStorageFolderName = "快递打包视频";

        /// <summary>
        /// 没有任何可用本地位置时的默认保存位置。与"添加磁盘"默认值同一套规则，
        /// 也就是"哪块盘就放哪块盘的快递打包视频"；绝不落到程序安装目录下。
        /// </summary>
        internal static string DefaultStoragePath =>
            Path.Combine(DefaultStorageRoot, DefaultStorageFolderName);

        private static bool IsSamePath(string left, string? right)
        {
            if (string.IsNullOrWhiteSpace(right)) return false;

            try
            {
                return string.Equals(
                    TrimTrailingSeparators(Path.GetFullPath(left)),
                    TrimTrailingSeparators(Path.GetFullPath(right)),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        internal bool IsCameraIdleNoSleepTime(DateTime now)
        {
            TimeSpan timeOfDay = now.TimeOfDay;
            return IsTimeInCameraIdlePeriod(timeOfDay, CameraIdleNoSleepStart1, CameraIdleNoSleepEnd1)
                || IsTimeInCameraIdlePeriod(timeOfDay, CameraIdleNoSleepStart2, CameraIdleNoSleepEnd2);
        }

        internal static bool TryNormalizeCameraIdlePeriod(
            string? startText,
            string? endText,
            out string normalizedStart,
            out string normalizedEnd)
        {
            normalizedStart = startText?.Trim() ?? "";
            normalizedEnd = endText?.Trim() ?? "";

            if (normalizedStart.Length == 0 && normalizedEnd.Length == 0)
                return true;

            if (!TryParseTimeOfDay(normalizedStart, out TimeSpan start)
                || !TryParseTimeOfDay(normalizedEnd, out TimeSpan end)
                || start == end)
            {
                return false;
            }

            normalizedStart = start.ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture);
            normalizedEnd = end.ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        private static bool IsTimeInCameraIdlePeriod(TimeSpan timeOfDay, string? startText, string? endText)
        {
            if (!TryNormalizeCameraIdlePeriod(startText, endText, out string normalizedStart, out string normalizedEnd)
                || normalizedStart.Length == 0)
            {
                return false;
            }

            TryParseTimeOfDay(normalizedStart, out TimeSpan start);
            TryParseTimeOfDay(normalizedEnd, out TimeSpan end);
            return start < end
                ? timeOfDay >= start && timeOfDay < end
                : timeOfDay >= start || timeOfDay < end;
        }

        private static bool TryParseTimeOfDay(string text, out TimeSpan value)
        {
            string[] formats = [@"h\:mm", @"hh\:mm"];
            return TimeSpan.TryParseExact(
                    text,
                    formats,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value)
                && value >= TimeSpan.Zero
                && value < TimeSpan.FromDays(1);
        }

        internal static void ApplyFirstUseDefaults(AppConfig config)
        {
            config.CameraBarcodeSetupVersion = CurrentCameraBarcodeSetupVersion;
            config.RecordingSetupVersion = CurrentRecordingSetupVersion;
            config.RequireWebAccessKey = true;
            config.WebProtectionSetupVersion = CurrentWebProtectionSetupVersion;
            MarkDeploymentSetupCompleted(config);
        }

        internal static bool ShouldRunRecordingSetup(AppConfig config)
        {
            return config == null
                || config.RecordingSetupVersion < CurrentRecordingSetupVersion;
        }

        internal static bool ShouldRunDeploymentSetup(AppConfig config)
        {
            return config == null
                || config.DeploymentSetupVersion < CurrentDeploymentSetupVersion;
        }

        internal static void MarkDeploymentSetupCompleted(AppConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            config.DeploymentSetupVersion = CurrentDeploymentSetupVersion;
            config.FirstUseWizardCompleted = true;
        }

        internal static void ResetDeploymentSetupForRetry(AppConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            config.DeploymentSetupVersion = 0;
        }

        internal static bool ShouldPromptCameraBarcodeUpgrade(AppConfig config)
        {
            return config != null
                && config.FirstUseWizardCompleted
                && config.CameraBarcodeSetupVersion < CurrentCameraBarcodeSetupVersion;
        }

        internal static void ApplyCameraBarcodeUpgradeChoice(AppConfig config, bool enableRecognition)
        {
            ArgumentNullException.ThrowIfNull(config);
            if (enableRecognition)
                config.EnableCameraBarcodeRecognition = true;
            config.CameraBarcodeSetupVersion = CurrentCameraBarcodeSetupVersion;
        }

        internal static bool ShouldPromptMobileConnection(AppConfig config)
        {
            return config != null
                && config.FirstUseWizardCompleted
                && config.EnableWebServer
                && config.MobileConnectionSetupVersion < CurrentMobileConnectionSetupVersion;
        }

        internal static void MarkMobileConnectionSetupCompleted(AppConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            config.MobileConnectionSetupVersion = CurrentMobileConnectionSetupVersion;
        }

        private static string NormalizeAiTtsEngine(string engine)
        {
            return string.Equals(engine, "Kokoro", System.StringComparison.OrdinalIgnoreCase) ? "Kokoro" : "Edge";
        }
    }
}
