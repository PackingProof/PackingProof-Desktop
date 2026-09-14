using System.Collections.Generic;
using System.Collections.Specialized;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 录像来源的"当前设备名"查询。
///
/// 录像记录保存的是写入当时的来源名快照，设备被改名（例如从"从机1"改成"安卓1"）后，
/// 老记录仍带着旧昵称，筛选下拉里就会同时出现老名字和新名字。这里按设备号取主机当前
/// 分配的名字（优先取登记过的录像设备，其次取在线客户端），取不到时再退回记录里的快照名。
/// </summary>
internal static class RecordingSourceNameLookup
{    internal static IReadOnlyDictionary<string, string> Build(
        IEnumerable<MobileOrderReceiverInfo>? mobileDevices,
        IEnumerable<ConnectedClientInfo>? connectedClients)
    {
        // 登记表里的名字就是主机分配的昵称，优先级高于客户端自报的显示名。
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (MobileOrderReceiverInfo device in mobileDevices ?? [])
            TryAdd(names, device.NodeId, device.NodeName);
        foreach (ConnectedClientInfo client in connectedClients ?? [])
            TryAdd(names, client.NodeId, client.DisplayName);
        return names;
    }

    internal static string Resolve(
        IReadOnlyDictionary<string, string>? names,
        string? deviceId,
        string? storedName)
    {
        string id = deviceId?.Trim() ?? "";
        if (names != null
            && id.Length > 0
            && names.TryGetValue(id, out string? current)
            && !string.IsNullOrWhiteSpace(current))
        {
            return current.Trim();
        }

        return storedName?.Trim() ?? "";
    }

    private static void TryAdd(Dictionary<string, string> names, string? nodeId, string? name)
    {
        string id = nodeId?.Trim() ?? "";
        string value = name?.Trim() ?? "";
        if (id.Length == 0 || value.Length == 0 || names.ContainsKey(id))
            return;

        names[id] = value;
    }
}

/// <summary>
/// 记录来源名的显示解析：先把记录里的名字快照换成主机当前分配的名字，
/// 再套用 WebServer 的显示规则（本机取节点名、电脑工位加前缀）。
/// 单独放一个文件，避免继续往规模冻结的 WebServer.cs 里堆解析逻辑。
/// </summary>
internal static class VideoSourceNameResolver
{
    /// <summary>设备平台头：手机在注册、能力查询与备份请求里都会带上它，主机据此分配"安卓N/苹果N"。</summary>
    internal const string DevicePlatformHeader = "X-EPM-Device-Platform";

    internal static string ReadDeviceKind(NameValueCollection headers) =>
        headers["X-EPM-Device-Kind"]?.Trim() ?? "";

    internal static string ReadDevicePlatform(NameValueCollection headers) =>
        headers[DevicePlatformHeader]?.Trim().ToLowerInvariant() ?? "";

    internal static string ResolveDisplayName(
        IReadOnlyDictionary<string, string>? currentNames,
        string sourceType,
        string deviceId,
        string deviceName,
        string deviceKind,
        string localNodeName) =>
        WebServer.ResolveVideoSourceDisplayName(
            sourceType ?? "",
            deviceId ?? "",
            RecordingSourceNameLookup.Resolve(currentNames, deviceId, deviceName),
            deviceKind ?? "",
            localNodeName ?? "");
}
