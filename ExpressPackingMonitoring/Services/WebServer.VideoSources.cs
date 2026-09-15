#nullable disable
using ExpressPackingMonitoring.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 录像来源与状态的查询接口：来源下拉（按设备号取当前昵称）与单条状态批量查询。
/// 从规模冻结的 WebServer.cs 抽出来，避免继续向该文件堆路由与查询逻辑。
/// </summary>
public sealed partial class WebServer
{
        private void HandleVideoSources(HttpListenerContext ctx)
        {
            // 去重规则与回放窗口共用 VideoSourceFilterOptions，避免两端下拉表现不一致。
            // 名字按设备号取当前昵称，否则设备改名后老名字会一直留在筛选里。
            IReadOnlyDictionary<string, string> currentNames = GetCurrentSourceDeviceNames();
            var data = VideoSourceFilterOptions.Build(
                    _db.GetVideoSources(),
                    source => string.Equals(source.SourceType, "external", StringComparison.OrdinalIgnoreCase)
                        ? ResolveVideoSourceName(
                            source.DeviceId,
                            RecordingSourceNameLookup.Resolve(currentNames, source.DeviceId, source.DeviceName))
                        : ResolveVideoSourceDisplayName(
                            source.SourceType,
                            source.DeviceId,
                            source.DeviceName,
                            "pc",
                            _nodeName))
                .Select(source => new
                {
                    sourceType = source.SourceType,
                    deviceId = source.DeviceId,
                    // 多台同名设备合并后返回设备号集合，前端按它筛才不会漏掉改名前的记录。
                    deviceIds = source.FilterDeviceIds,
                    name = source.Name,
                    videoCount = source.VideoCount
                });
            SendJson(ctx, 200, new { data });
        }

        /// <summary>设备号→当前昵称。含离线但仍在保留期内的设备，供筛选把老昵称归并到新昵称。</summary>
        internal IReadOnlyDictionary<string, string> GetCurrentSourceDeviceNames() =>
            RecordingSourceNameLookup.Build(
                _mobileOrderReceivers.GetKnownRecordingDevices(),
                _connectedClients.GetSnapshot());

        /// <summary>
        /// 关键字命中某台设备的当前昵称时，返回这些设备号，让搜索同时按设备号命中：
        /// 记录里存的是写入当时的名字快照，只按名字搜不到设备改名前的录像。
        /// </summary>
        internal IReadOnlyList<string> ResolveKeywordDeviceIds(string keyword)
        {
            string value = keyword?.Trim() ?? "";
            if (value.Length == 0)
                return Array.Empty<string>();

            return GetCurrentSourceDeviceNames()
                .Where(pair => pair.Value.Contains(value, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .Take(50)
                .ToArray();
        }

        private static string ResolveVideoSourceName(string deviceId, string deviceName)
        {
            if (!string.IsNullOrWhiteSpace(deviceName))
                return deviceName.Trim();
            string normalized = new((deviceId ?? "").Where(char.IsLetterOrDigit).ToArray());
            return normalized.Length == 0
                ? "手机设备"
                : $"设备 {normalized[^Math.Min(6, normalized.Length)..].ToUpperInvariant()}";
        }

        internal static string ResolveVideoSourceDisplayName(
            string sourceType,
            string deviceId,
            string deviceName,
            string deviceKind,
            string localNodeName)
        {
            if (!string.Equals(sourceType, "external", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(localNodeName))
                    return localNodeName.Trim();
                if (!string.IsNullOrWhiteSpace(deviceName))
                    return deviceName.Trim();
                return "电脑";
            }

            if (!string.IsNullOrWhiteSpace(deviceName))
                return deviceName.Trim();

            string normalized = new((deviceId ?? "").Where(char.IsLetterOrDigit).ToArray());
            if (normalized.Length > 0)
                return $"设备 {normalized[^Math.Min(6, normalized.Length)..].ToUpperInvariant()}";
            return string.Equals(deviceKind, "pc", StringComparison.OrdinalIgnoreCase)
                ? "电脑设备"
                : "手机设备";
        }

        private void HandleVideoStatuses(HttpListenerContext ctx)
        {
            long[] ids = (ctx.Request.QueryString["ids"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => long.TryParse(value, out long id) ? id : 0)
                .Where(id => id > 0)
                .Distinct()
                .Take(100)
                .ToArray();
            var records = _db.QueryVideoStatuses(ids);
            var data = ids.Select(id =>
            {
                records.TryGetValue(id, out VideoRecord record);
                bool exists = record != null
                    && !string.IsNullOrWhiteSpace(PlaybackFileResolver.ResolvePlaybackPath(record));
                string status = record == null || (!record.IsDeleted && !exists)
                    ? "missing"
                    : record.IsDeleted ? "deleted" : "available";
                string reason = record == null
                    ? "记录不存在"
                    : record.IsDeleted
                        ? (string.IsNullOrWhiteSpace(record.DeleteReason) ? "已清理" : record.DeleteReason)
                        : exists ? "" : "文件缺失";
                return new { id, status, exists, reason };
            });
            SendJson(ctx, 200, new { data });
        }
}
