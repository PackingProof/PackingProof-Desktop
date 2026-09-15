using System;
using System.Collections.Generic;
using System.ComponentModel;
using ExpressPackingMonitoring.Audio;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.UI;
using NAudio.CoreAudioApi;

namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 悬浮置顶小窗的状态绑定。小窗与主窗口共用同一个 VideoFrame，
    /// 不复制帧、也不介入录像与摄像头生命周期。
    /// </summary>
    public partial class MainViewModel
    {
        private FloatingPreviewIndicator _floatingPreviewIndicator = FloatingPreviewIndicator.Idle;
        private string _floatingPreviewStatusText = AppLanguage.Get(FloatingPreviewStatusPolicy.IdleText);
        private bool _floatingPreviewTextIsOrderId;
        private bool _isFloatingPreviewActive;

        public FloatingPreviewIndicator FloatingPreviewIndicator
        {
            get => _floatingPreviewIndicator;
            private set => SetProperty(ref _floatingPreviewIndicator, value);
        }

        /// <summary>已本地化的状态文案；录制中带单号时就是单号本身。</summary>
        public string FloatingPreviewStatusText
        {
            get => _floatingPreviewStatusText;
            private set => SetProperty(ref _floatingPreviewStatusText, value);
        }

        /// <summary>文案是否为单号，供小窗用等宽字体展示。</summary>
        public bool FloatingPreviewTextIsOrderId
        {
            get => _floatingPreviewTextIsOrderId;
            private set => SetProperty(ref _floatingPreviewTextIsOrderId, value);
        }

        /// <summary>小窗是否正在显示，供主窗口按钮状态与生命周期判断使用。</summary>
        public bool IsFloatingPreviewActive
        {
            get => _isFloatingPreviewActive;
            set => SetProperty(ref _isFloatingPreviewActive, value);
        }

        /// <summary>
        /// 主界面预览控件的显示宽度（设备像素）。窗口最小化或隐藏时传 0，
        /// 这样只显示小窗时预览会按小窗尺寸发布，不用白白搬运整帧。
        /// </summary>
        public void ReportMainPreviewDisplayWidth(double width) =>
            UpdatePreviewDisplayWidths(ref _previewDisplayWidthMain, width);

        /// <summary>小窗预览控件的显示宽度（设备像素）。小窗关闭时传 0。</summary>
        public void ReportFloatingPreviewDisplayWidth(double width) =>
            UpdatePreviewDisplayWidths(ref _previewDisplayWidthFloating, width);

        private void UpdatePreviewDisplayWidths(ref int slot, double width)
        {
            int pixels = double.IsFinite(width) && width > 0 ? (int)Math.Round(width) : 0;
            if (Interlocked.Exchange(ref slot, pixels) == pixels)
                return;

            // 取两个可见预览里较大的那个：主界面在看就按主界面发，只剩小窗就按小窗发。
            Volatile.Write(
                ref _previewDisplayWidth,
                Math.Max(Volatile.Read(ref _previewDisplayWidthMain), Volatile.Read(ref _previewDisplayWidthFloating)));
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            switch (e.PropertyName)
            {
                case nameof(IsRecording):
                case nameof(CurrentOrderId):
                case nameof(PreRecordBufferProgress):
                case nameof(IsCameraSleeping):
                case nameof(Config):
                    RefreshFloatingPreviewStatus();
                    break;
            }
        }

        /// <summary>
        /// 录制中取录制开始时锁定的单号，与水印和扩展数据保持同一口径；
        /// 否则取当前扫到的单号。
        /// </summary>
        internal void RefreshFloatingPreviewStatus()
        {
            FloatingPreviewStatus status = FloatingPreviewStatusPolicy.Evaluate(
                IsRecording,
                IsCameraSleeping,
                _config?.EnableEventRecordingBuffer == true,
                PreRecordBufferProgress > 0,
                IsRecording ? _recordingOrderId : CurrentOrderId);

            FloatingPreviewIndicator = status.Indicator;
            FloatingPreviewTextIsOrderId = status.TextIsOrderId;
            FloatingPreviewStatusText = status.TextIsOrderId
                ? status.Text
                : AppLanguage.Get(status.Text);
        }

        /// <summary>
        /// 小窗的麦克风下拉内容，首项固定是"跟随系统默认"，与设置页保持一致。
        /// </summary>
        internal IReadOnlyList<AudioEndpointInfo> ListMicrophoneEndpoints() =>
            BuildEndpointMenuItems(DataFlow.Capture);

        /// <summary>小窗的扬声器下拉内容。</summary>
        internal IReadOnlyList<AudioEndpointInfo> ListPlaybackEndpoints() =>
            BuildEndpointMenuItems(DataFlow.Render);

        private static IReadOnlyList<AudioEndpointInfo> BuildEndpointMenuItems(DataFlow flow)
        {
            // 首项带显式标记而不是空值，否则会被判定成"没选过设备"，
            // 录制时直接跳过音频采集，录出来没有声音。
            var items = new List<AudioEndpointInfo>
            {
                new(
                    AudioEndpointCatalog.SystemDefaultId,
                    AppLanguage.Get(AudioDeviceSelectionPolicy.FollowSystemDefaultText),
                    false)
            };
            items.AddRange(AudioEndpointCatalog.List(flow));
            return items;
        }

        internal string CurrentMicrophoneEndpointId => _config?.AudioDeviceMoniker ?? string.Empty;

        internal string CurrentPlaybackEndpointId => _config?.PlaybackDeviceMoniker ?? string.Empty;

        /// <summary>旧配置可能只存了名称没存 Id，菜单勾选要按名称回落匹配。</summary>
        internal string CurrentMicrophoneEndpointName => _config?.AudioDeviceName ?? string.Empty;

        internal string CurrentPlaybackEndpointName => _config?.PlaybackDeviceName ?? string.Empty;

        /// <summary>小窗上次停靠的角落，供下次打开时贴回同一位置。</summary>
        internal string FloatingPreviewCorner => _config?.FloatingPreviewCorner ?? string.Empty;

        /// <summary>只记角落不记坐标，换分辨率或换显示器都不会把小窗放到屏幕外。</summary>
        internal void SaveFloatingPreviewCorner(FloatingPreviewCorner corner)
        {
            string value = corner.ToString();
            if (string.Equals(_config?.FloatingPreviewCorner, value, StringComparison.Ordinal))
                return;

            if (WorkstationConfigStore.TryUpdate(
                    saved => saved.FloatingPreviewCorner = value,
                    out AppConfig savedConfig,
                    out string error))
            {
                Config.FloatingPreviewCorner = savedConfig.FloatingPreviewCorner;
            }
            else
            {
                RuntimeLog.Warn("FloatingPreview", $"小窗停靠角落保存失败：{error}");
            }
        }

        /// <summary>
        /// 切换录音麦克风。下一次开始录制时 ResolveAudioEndpoint 会读到新配置，
        /// 正在进行的录制不打断，避免为了换设备而毁掉当前这一单的音轨。
        /// </summary>
        internal bool TrySelectMicrophoneEndpoint(AudioEndpointInfo endpoint) =>
            TryPersistAudioEndpoint(
                "麦克风",
                saved =>
                {
                    saved.AudioDeviceName = endpoint.Name;
                    saved.AudioDeviceMoniker = endpoint.Id;
                },
                applied =>
                {
                    Config.AudioDeviceName = applied.AudioDeviceName;
                    Config.AudioDeviceMoniker = applied.AudioDeviceMoniker;
                });

        /// <summary>切换播报扬声器，立即对之后的播报生效。</summary>
        internal bool TrySelectPlaybackEndpoint(AudioEndpointInfo endpoint) =>
            TryPersistAudioEndpoint(
                "扬声器",
                saved =>
                {
                    saved.PlaybackDeviceName = endpoint.Name;
                    saved.PlaybackDeviceMoniker = endpoint.Id;
                },
                applied =>
                {
                    Config.PlaybackDeviceName = applied.PlaybackDeviceName;
                    Config.PlaybackDeviceMoniker = applied.PlaybackDeviceMoniker;
                    if (_speechService != null)
                    {
                        _speechService.PlaybackDeviceId = Config.PlaybackDeviceMoniker;
                        _speechService.PlaybackDeviceName = Config.PlaybackDeviceName;
                    }
                });

        /// <summary>
        /// 设备选择要落盘，否则重启后又回到旧设备。
        /// 落盘失败时不改运行时配置，避免界面显示的设备和实际用的设备不一致。
        /// </summary>
        private bool TryPersistAudioEndpoint(
            string kind,
            System.Action<AppConfig> mutate,
            System.Action<AppConfig> apply)
        {
            if (!WorkstationConfigStore.TryUpdate(mutate, out AppConfig savedConfig, out string error))
            {
                RuntimeLog.Warn("FloatingPreview", $"{kind}切换保存失败：{error}");
                return false;
            }

            apply(savedConfig);
            RuntimeLog.Info("FloatingPreview", $"{kind}已切换");
            return true;
        }
    }
}
