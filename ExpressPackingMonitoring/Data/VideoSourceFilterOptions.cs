namespace ExpressPackingMonitoring.Data
{
    /// <summary>筛选下拉里的一个来源项。DeviceId 为空表示这个名字下有多台设备，只能按名字筛。</summary>
    internal sealed record VideoSourceFilterOption(
        string SourceType,
        string DeviceId,
        string Name,
        int VideoCount);

    /// <summary>
    /// 来源下拉的去重规则。数据库按 (SourceType, SourceDeviceId) 分组，
    /// 同一台手机重装或换设备号后会出现多条，直接铺到下拉里就会看到
    /// 好几个"手机1"。这里按显示名合并，本机永远只有一项。
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

            foreach (VideoSourceInfo source in sources)
            {
                bool isExternal = string.Equals(source.SourceType, "external", StringComparison.OrdinalIgnoreCase);
                // 外部设备一定带设备号，没有设备号的外部记录是脏数据，直接跳过。
                if (isExternal && string.IsNullOrWhiteSpace(source.DeviceId))
                    continue;

                string name = nameSelector(source)?.Trim() ?? "";
                if (name.Length == 0)
                    continue;

                string sourceType = isExternal ? "external" : "pc";
                string key = isExternal ? $"external:{name}" : "pc:";
                if (indexByKey.TryGetValue(key, out int index))
                {
                    VideoSourceFilterOption existing = groups[index];
                    groups[index] = existing with
                    {
                        // 同名多设备时不能再钉某一个设备号，否则筛出来少一半录像。
                        DeviceId = "",
                        VideoCount = existing.VideoCount + source.VideoCount
                    };
                    continue;
                }

                indexByKey[key] = groups.Count;
                groups.Add(new VideoSourceFilterOption(
                    sourceType,
                    isExternal ? source.DeviceId ?? "" : "",
                    name,
                    source.VideoCount));
            }

            return groups;
        }
    }
}
