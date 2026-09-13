using System.Collections.Generic;
using System.Linq;
using ExpressPackingMonitoring.Audio;
using ExpressPackingMonitoring.Config;
using NAudio.CoreAudioApi;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// 设置页音频设备下拉的选中与回写规则。
    /// 与界面解耦，麦克风和播放设备共用同一套匹配逻辑，也便于单独回归。
    /// </summary>
    internal static class AudioDeviceSelectionPolicy
    {
        internal const string NoMicrophoneText = "未检测到麦克风";
        internal const string FollowSystemDefaultText = "跟随系统默认";

        /// <summary>占位项不是真实设备，不能被当成有效选择回写到配置里。</summary>
        public static bool IsAvailable(MicInfo? device) =>
            device != null
            && !string.IsNullOrWhiteSpace(device.Name)
            && device.Name != NoMicrophoneText;

        /// <summary>
        /// 是否为一个具体端点。"跟随系统默认"与"未检测到"都只是占位项，
        /// 选中它们一律按清空配置处理，由解析时回落到系统默认端点。
        /// </summary>
        public static bool IsExplicitDevice(MicInfo? device) =>
            IsAvailable(device) && device!.Name != FollowSystemDefaultText;

        /// <summary>
        /// 按配置挑出应当选中的项：先用 Id 精确匹配，再退回名称匹配。
        /// 换机器或插拔设备后 Id 会失效，按名称兜底比什么都不选更符合预期。
        /// 配置为空表示跟随系统默认，直接选占位项而不是猜一个设备。
        /// </summary>
        public static MicInfo? Match(
            IReadOnlyList<MicInfo> devices,
            string? configuredMoniker,
            string? configuredName)
        {
            if (devices == null || devices.Count == 0) return null;

            if (string.IsNullOrWhiteSpace(configuredMoniker) && string.IsNullOrWhiteSpace(configuredName))
                return devices.FirstOrDefault(d => d.Name == FollowSystemDefaultText);

            return devices.FirstOrDefault(d =>
                       !string.IsNullOrEmpty(configuredMoniker) && d.Moniker == configuredMoniker)
                   ?? devices.FirstOrDefault(d => d.Name == configuredName);
        }

        /// <summary>把枚举到的端点转成下拉项，并在最前面放"跟随系统默认"。</summary>
        public static List<MicInfo> BuildDeviceList(DataFlow flow)
        {
            var items = new List<MicInfo>
            {
                new() { Name = FollowSystemDefaultText, Moniker = "" }
            };

            foreach (AudioEndpointInfo endpoint in AudioEndpointCatalog.List(flow))
                items.Add(new MicInfo { Name = endpoint.Name, Moniker = endpoint.Id });

            return items;
        }

        /// <summary>把枚举到的播放端点转成下拉项。</summary>
        public static List<MicInfo> BuildPlaybackDeviceList() => BuildDeviceList(DataFlow.Render);

        /// <summary>
        /// 把下拉选择回写到配置。选中占位项或没选时清空，
        /// 清空即代表跟随系统默认，与 SpeechService 的回落行为一致。
        /// </summary>
        public static void ApplyPlaybackSelection(AppConfig config, MicInfo? selected)
        {
            if (IsExplicitDevice(selected))
            {
                config.PlaybackDeviceName = selected!.Name;
                config.PlaybackDeviceMoniker = selected.Moniker ?? "";
            }
            else
            {
                config.PlaybackDeviceName = "";
                config.PlaybackDeviceMoniker = "";
            }
        }

        /// <summary>麦克风回写：占位项与"跟随系统默认"同样按"未选择"处理。</summary>
        public static void ApplyMicrophoneSelection(AppConfig config, MicInfo? selected)
        {
            if (IsExplicitDevice(selected))
            {
                config.AudioDeviceName = selected!.Name;
                config.AudioDeviceMoniker = selected.Moniker ?? "";
            }
            else
            {
                config.AudioDeviceName = "";
                config.AudioDeviceMoniker = "";
            }
        }
    }
}
