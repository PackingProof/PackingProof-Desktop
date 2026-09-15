using System;
using System.Collections.Generic;

namespace ExpressPackingMonitoring.Data
{
    /// <summary>
    /// 导出单号时的筛选条件，以及对应的 SQL 片段。
    ///
    /// 单独成类而不是写在 VideoDatabase 里：后者是规模冻结的历史例外，
    /// 而且条件拼装需要能独立测试——导出结果与列表不一致正是之前的缺陷来源。
    /// </summary>
    internal sealed record OrderNumberExportFilter(
        DateTime? StartDate,
        DateTime? EndDate,
        string Mode = "",
        string DeviceId = "",
        string SourceName = "",
        string SourceType = "",
        IReadOnlyList<string>? DeviceIds = null)
    {
        /// <summary>
        /// 拼出 WHERE 片段与参数。表别名由调用方给，导出查询里录像表是 v。
        ///
        /// 设备判定与回放列表保持一致：多台同名设备合并后由界面给出设备号集合，
        /// 优先按集合命中；只有一个设备号时按它；都没有才退回按设备名。
        /// </summary>
        internal (string Sql, IReadOnlyList<(string Name, string Value)> Parameters) BuildWhere(string alias)
        {
            string prefix = string.IsNullOrWhiteSpace(alias) ? "" : alias.Trim() + ".";
            var parameters = new List<(string Name, string Value)>();
            string sql = "";

            string normalizedMode = RecordingModeFilter.Normalize(Mode);
            if (normalizedMode == RecordingModeFilter.Return)
                sql += $" AND {prefix}Mode IN ('return', '退货')";
            else if (normalizedMode == RecordingModeFilter.Shipping)
                sql += $" AND ({prefix}Mode IN ('shipping', '发货', '') OR {prefix}Mode IS NULL)";

            if (StartDate.HasValue)
            {
                sql += $" AND {prefix}StartTime >= @startDate";
                parameters.Add(("startDate", StartDate.Value.Date.ToString("yyyy-MM-dd 00:00:00")));
            }

            if (EndDate.HasValue)
            {
                sql += $" AND {prefix}StartTime < @endDate";
                parameters.Add(("endDate", EndDate.Value.Date.AddDays(1).ToString("yyyy-MM-dd 00:00:00")));
            }

            string deviceId = DeviceId?.Trim() ?? "";
            string sourceName = SourceName?.Trim() ?? "";
            string sourceType = SourceType?.Trim().ToLowerInvariant() ?? "";
            List<string> deviceIds = NormalizeDeviceIds(DeviceIds);
            if (deviceIds.Count > 0)
            {
                var placeholders = new List<string>(deviceIds.Count);
                for (int index = 0; index < deviceIds.Count; index++)
                {
                    placeholders.Add($"@exportDeviceId{index}");
                    parameters.Add(($"exportDeviceId{index}", deviceIds[index]));
                }

                sql += $" AND {prefix}SourceType = 'external' AND {prefix}SourceDeviceId IN ({string.Join(", ", placeholders)})";
            }
            else if (deviceId.Length > 0)
            {
                sql += $" AND {prefix}SourceType = 'external' AND {prefix}SourceDeviceId = @deviceId";
                parameters.Add(("deviceId", deviceId));
            }
            else if (sourceName.Length > 0 && sourceType != "pc")
            {
                // 没有设备号的外部来源只能按设备名认。
                sql += $" AND {prefix}SourceType = 'external' AND {prefix}SourceDeviceName = @sourceName";
                parameters.Add(("sourceName", sourceName));
            }
            else if (sourceType == "pc")
            {
                sql += $" AND ({prefix}SourceType IS NULL OR {prefix}SourceType <> 'external')";
            }

            return (sql, parameters);
        }

        private static List<string> NormalizeDeviceIds(IReadOnlyList<string>? deviceIds)
        {
            var normalized = new List<string>();
            if (deviceIds == null)
                return normalized;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string deviceId in deviceIds)
            {
                string value = deviceId?.Trim() ?? "";
                if (value.Length == 0 || !seen.Add(value))
                    continue;

                normalized.Add(value);
            }

            return normalized;
        }
    }
}
