using System;
using System.Collections.Generic;
using System.Linq;

namespace ExpressPackingMonitoring.Data
{
    /// <summary>
    /// 筛选下拉里的一个来源项。<see cref="DeviceIds"/> 是这个显示名背后的全部设备号，
    /// 多台同名设备（或同一台设备换过设备号）合并成一项后靠它按身份筛选，
    /// 否则只能按名字匹配，改名前的录像会查不到。
    /// </summary>
    internal sealed record VideoSourceFilterOption(
        string SourceType,
        string DeviceId,
        string Name,
        int VideoCount,
        IReadOnlyList<string>? DeviceIds = null)
    {
        /// <summary>筛选用到的设备号集合：没有集合时退回单个设备号，再没有就按名字筛。</summary>
        internal IReadOnlyList<string> FilterDeviceIds =>
            DeviceIds is { Count: > 0 } ? DeviceIds
            : string.IsNullOrEmpty(DeviceId) ? Array.Empty<string>()
            : new[] { DeviceId };
    }

    /// <summary>
    /// 来源下拉的去重规则。数据库按 (SourceType, SourceDeviceId) 分组，
    /// 同一台手机重装或换设备号后会出现多条，直接铺到下拉里就会看到
    /// 好几个"手机1"。这里按显示名合并，本机永远只有一项；
    /// 合并时把成员的设备号收集起来，筛选按设备号命中，不会漏掉任何一台的录像。
    /// Web 端和回放窗口共用同一套规则，避免两边表现不一致。
    /// </summary>
    internal static class VideoSourceFilterOptions
    {
        internal static IReadOnlyList<VideoSourceFilterOption> Build(
            IEnumerable<VideoSourceInfo> sources,
            Func<VideoSourceInfo, string> nameSelector)
        {
            var groups = new List<VideoSourceFilterOption>();
            var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var deviceIdsByKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (VideoSourceInfo source in sources)
            {
                bool isExternal = string.Equals(source.SourceType, "external", StringComparison.OrdinalIgnoreCase);

                string name = nameSelector(source)?.Trim() ?? "";
                if (name.Length == 0)
                    continue;

                // 外部设备即使没有设备号（历史脏数据）也要保留：
                // 只要它还有录像，用户就必须能在下拉里选到并按名字筛出来。
                string deviceId = isExternal ? source.DeviceId?.Trim() ?? "" : "";
                string sourceType = isExternal ? "external" : "pc";
                string key = isExternal ? $"external:{name}" : "pc:";
                if (indexByKey.TryGetValue(key, out int index))
                {
                    List<string> memberIds = deviceIdsByKey[key];
                    if (deviceId.Length > 0 && !memberIds.Contains(deviceId, StringComparer.OrdinalIgnoreCase))
                        memberIds.Add(deviceId);

                    VideoSourceFilterOption existing = groups[index];
                    groups[index] = existing with
                    {
                        DeviceId = memberIds.Count == 1 ? memberIds[0] : "",
                        DeviceIds = memberIds.ToArray(),
                        VideoCount = existing.VideoCount + source.VideoCount
                    };
                    continue;
                }

                var ids = new List<string>();
                if (deviceId.Length > 0)
                    ids.Add(deviceId);

                indexByKey[key] = groups.Count;
                deviceIdsByKey[key] = ids;
                groups.Add(new VideoSourceFilterOption(
                    sourceType,
                    deviceId,
                    name,
                    source.VideoCount,
                    ids.ToArray()));
            }

            return groups;
        }
    }
}
