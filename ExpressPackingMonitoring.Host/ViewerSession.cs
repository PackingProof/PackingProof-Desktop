using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.Host;

/// <summary>
/// 查看端模式：不录像也不保存，只负责在局域网里找到保存主机并打开它的网页回放。
/// 主机发现、网页预检与设备登记全部复用桌面端已有的实现。
/// </summary>
internal static class ViewerSession
{
    internal static async Task<int> RunAsync(AppConfig config, CancellationToken token)
    {
        Console.WriteLine("正在查找同一网络中的保存主机…");
        IReadOnlyList<PackingProofNodeInfo> hosts = await WorkstationNetwork.FindHostsAsync(
            lastKnownAddress: null,
            config.WebServerPort,
            progress: null,
            hostProgress: null,
            token);

        if (hosts.Count == 0)
        {
            MacDialog.ShowMessage("未找到保存主机。请确认主机已开机，并且与本机在同一个局域网。");
            return 1;
        }

        Console.WriteLine($"发现 {hosts.Count} 台保存主机：");
        foreach (PackingProofNodeInfo discovered in hosts)
            Console.WriteLine($"  · {Describe(discovered)}");

        PackingProofNodeInfo? host = SelectHost(config, hosts);
        if (host == null)
        {
            Console.WriteLine("已取消选择主机");
            return 1;
        }

        string address = host.Address;
        RememberHost(config, host, "");
        string? targetUrl = null;
        switch (await WorkstationNetwork.ProbeWebAccessAsync(address, null, token))
        {
            case WorkstationNetwork.WebAccessProbeResult.Authorized:
                targetUrl = WorkstationNetwork.BuildWebAccessUrl(address, null);
                break;
            case WorkstationNetwork.WebAccessProbeResult.Unauthorized:
                Console.WriteLine("主机开启了网页访问保护，正在申请接入，请到主机上点允许…");
                BackupDeviceEnrollmentResult enrollment;
                try
                {
                    enrollment = await WorkstationNetwork.EnrollBackupDeviceAsync(
                        address,
                        config.NodeId,
                        config.NodeName,
                        "viewer",
                        token,
                        platform: "macos");
                }
                catch (Exception ex)
                {
                    MacDialog.ShowMessage($"连接 {host.NodeName} 需要主机确认，本次未完成：{ex.Message}");
                    return 1;
                }

                targetUrl = enrollment.WebAccessUrl;
                if (string.IsNullOrWhiteSpace(targetUrl))
                {
                    MacDialog.ShowMessage("主机没有返回可用的网页地址，请确认主机版本是否需要更新。");
                    return 1;
                }
                break;
            default:
                MacDialog.ShowMessage($"无法连接保存主机 {host.NodeName}（{address}）。");
                return 1;
        }

        RememberHost(config, host, targetUrl!);
        HostOptions.OpenUrl(targetUrl!);
        Console.WriteLine($"已连接 {host.NodeName}，网页回放 {targetUrl}");
        return 0;
    }

    private static PackingProofNodeInfo? SelectHost(
        AppConfig config,
        IReadOnlyList<PackingProofNodeInfo> hosts)
    {
        PackingProofNodeInfo? remembered = hosts.FirstOrDefault(host =>
            string.Equals(host.NodeId, config.LastKnownHostNodeId, StringComparison.OrdinalIgnoreCase));
        if (remembered != null) return remembered;
        if (hosts.Count == 1) return hosts[0];

        // 没有对话框可用时（常驻或自动化）不阻塞：用记住的主机，否则取第一台
        if (!HostOptions.DialogsEnabled)
        {
            Console.WriteLine("无法弹出选择框，使用列表中的第一台主机；可用 --switch-purpose 重新选择");
            return hosts[0];
        }

        IReadOnlyList<string> labels = hosts.Select(Describe).ToArray();
        int? index = MacDialog.ChooseHost(labels);
        return index.HasValue ? hosts[index.Value] : null;
    }

    private static string Describe(PackingProofNodeInfo host) =>
        string.IsNullOrWhiteSpace(host.NodeName)
            ? host.Address
            : $"{host.NodeName}（{host.Address}）";

    /// <summary>
    /// 记住主机：除了身份，还要记下**地址**与网页访问密钥，
    /// 否则桌面的"打开网页回放"只能打开本机地址（曾经就是这个 bug）。
    /// </summary>
    private static void RememberHost(AppConfig config, PackingProofNodeInfo host, string accessUrl)
    {
        config.LastKnownHostNodeId = host.NodeId;
        config.LastKnownHostNodeName = host.NodeName;
        config.LastKnownHostAddress = host.Address;
        config.LastKnownHostWebAccessKey = ExtractAccessKey(accessUrl);
        WorkstationConfigStore.TrySave(config, out _);
    }

    private static string ExtractAccessKey(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return "";
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            if (parts.Length == 2 && string.Equals(parts[0], "key", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(parts[1]);
        }

        return "";
    }
}
