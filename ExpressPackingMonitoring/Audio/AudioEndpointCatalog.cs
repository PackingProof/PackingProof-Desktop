using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;

namespace ExpressPackingMonitoring.Audio
{
    /// <summary>一个可选的音频端点，Id 用于持久化，Name 用于展示。</summary>
    public readonly record struct AudioEndpointInfo(string Id, string Name, bool IsDefault)
    {
        /// <summary>Id 为空表示"跟随系统默认"这一占位项，而不是某个具体端点。</summary>
        public bool IsFollowSystemDefault => string.IsNullOrWhiteSpace(Id);
    }

    /// <summary>
    /// 麦克风与扬声器端点的统一枚举与匹配入口。
    /// 设置页、首次配置向导和悬浮小窗共用这里，避免各处重复写 MMDeviceEnumerator。
    /// </summary>
    public static class AudioEndpointCatalog
    {
        /// <summary>列出指定方向的可用端点；枚举失败时返回空列表而不是抛出，调用方按"无设备"处理。</summary>
        public static IReadOnlyList<AudioEndpointInfo> List(DataFlow flow)
        {
            var result = new List<AudioEndpointInfo>();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                string? defaultId = null;
                try { defaultId = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia).ID; }
                catch { }

                var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
                for (int i = 0; i < devices.Count; i++)
                {
                    MMDevice device = devices[i];
                    result.Add(new AudioEndpointInfo(
                        device.ID,
                        device.FriendlyName,
                        string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// 按配置解析端点：优先用 Id 精确匹配，其次按名称匹配，都没命中则回落到系统默认。
        /// 换机器或插拔设备后配置里的 Id 会失效，这时按名称兜底比直接静音更符合店员预期。
        /// </summary>
        public static MMDevice? Resolve(DataFlow flow, string? deviceId, string? deviceName)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
                if (devices == null || devices.Count == 0) return null;

                if (!string.IsNullOrWhiteSpace(deviceId))
                {
                    foreach (MMDevice device in devices)
                    {
                        if (string.Equals(device.ID, deviceId, StringComparison.OrdinalIgnoreCase))
                            return device;
                    }
                }

                if (!string.IsNullOrWhiteSpace(deviceName))
                {
                    foreach (MMDevice device in devices)
                    {
                        if (string.Equals(device.FriendlyName, deviceName, StringComparison.OrdinalIgnoreCase))
                            return device;
                    }
                }

                try { return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia); }
                catch { return devices[0]; }
            }
            catch
            {
                return null;
            }
        }
    }
}
