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
        /// 是否为一个具体端点。"未检测到"只是占位项，不能写进配置。
        /// "跟随系统默认"是一个真实可选项，会以显式标记落盘，不按占位项处理。
        /// </summary>
        public static bool IsExplicitDevice(MicInfo? device) => IsAvailable(device);

        /// <summary>
        /// 按配置挑出应当选中的项：先用 Id 精确匹配，再退回名称匹配。
        /// 换机器或插拔设备后 Id 会失效，按名称兜底比什么都不选更符合预期。
        /// </summary>
        public static MicInfo? Match(
            IReadOnlyList<MicInfo> devices,
            string? configuredMoniker,
            string? configuredName)
        {
            if (devices == null || devices.Count == 0) return null;

            if (AudioEndpointCatalog.IsSystemDefault(configuredMoniker))
                return devices.FirstOrDefault(d => d.Moniker == AudioEndpointCatalog.SystemDefaultId);

            return devices.FirstOrDefault(d =>
                       !string.IsNullOrEmpty(configuredMoniker) && d.Moniker == configuredMoniker)
                   ?? devices.FirstOrDefault(d => d.Name == configuredName);
        }

        /// <summary>
        /// 把枚举到的端点转成下拉项，并在最前面放"跟随系统默认"。
        /// 该项带显式标记而不是空值，避免被判定成"没选过麦克风"。
        /// </summary>
        public static List<MicInfo> BuildDeviceList(DataFlow flow) =>
            PrependFollowSystemDefault(
                AudioEndpointCatalog.List(flow).Select(endpoint => new MicInfo
                {
                    Name = endpoint.Name,
                    Moniker = endpoint.Id
                }));

        /// <summary>
        /// 在已经枚举好的端点前面插入"跟随系统默认"。
        /// 哨兵值必须和播放设备一致：写成空串会被判定成"没选过麦克风"，
        /// 保存设置后录制会因为找不到同名设备而放弃这一单。
        /// </summary>
        public static List<MicInfo> PrependFollowSystemDefault(IEnumerable<MicInfo>? devices)
        {
            var items = new List<MicInfo>
            {
                new()
                {
                    Name = FollowSystemDefaultText,
                    Moniker = AudioEndpointCatalog.SystemDefaultId
                }
            };

            if (devices != null)
                items.AddRange(devices);

            return items;
        }

        /// <summary>把枚举到的播放端点转成下拉项。</summary>
        public static List<MicInfo> BuildPlaybackDeviceList() => BuildDeviceList(DataFlow.Render);

        /// <summary>
        /// 配置里是否已经有一个可用的录音设备选择。
        /// "跟随系统默认"带显式标记，属于明确选择，不应再提示"未选择麦克风"。
        /// </summary>
        public static bool HasUsableMicrophoneSelection(AppConfig config) =>
            AudioEndpointCatalog.IsSystemDefault(config.AudioDeviceMoniker)
            || !string.IsNullOrWhiteSpace(config.AudioDeviceName);

        /// <summary>把下拉选择回写到配置；占位项按"未选择"清空。</summary>
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

        /// <summary>麦克风回写：占位项按"未选择"处理，"跟随系统默认"则落显式标记。</summary>
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
