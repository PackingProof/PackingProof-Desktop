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
        Console.WriteLine(ViewerConnectionStatusText.SearchingViewer);
        IReadOnlyList<PackingProofNodeInfo> hosts = await WorkstationNetwork.FindHostsAsync(
            lastKnownAddress: null,
            config.WebServerPort,
            progress: null,
            hostProgress: null,
            token);

        // 本机不能连自己：这台电脑自己是保存主机时，连自己会在同一台机器上弹"设备请求连接本机"，
        // 而用途是互斥的，本机永远排除在可选主机之外
        IReadOnlyList<PackingProofNodeInfo> candidates = hosts
            .Where(host => !IsSelf(config, host))
            .ToList();

        if (candidates.Count == 0)
        {
            MacDialog.ShowMessage(hosts.Count > 0
                ? "只找到本机自己。请确认另一台保存主机已开机，并且与本机在同一个局域网。"
                : "未找到保存主机。请确认主机已开机，并且与本机在同一个局域网。");
            return 1;
        }

        Console.WriteLine($"发现 {candidates.Count} 台保存主机：");
        foreach (PackingProofNodeInfo discovered in candidates)
            Console.WriteLine($"  · {Describe(discovered)}");

        PackingProofNodeInfo? host = SelectHost(config, candidates);
        if (host == null)
        {
            Console.WriteLine("已取消选择主机");
            return 1;
        }

        string address = host.Address;
        // 同一台主机沿用已保存的网页访问密钥：换了主机才丢旧密钥，
        // 否则每次启动都要重新申请一次授权，主机那边会反复弹"设备请求连接"
        string savedKey = string.Equals(
                config.LastKnownHostNodeId,
                host.NodeId,
                StringComparison.OrdinalIgnoreCase)
            ? (config.LastKnownHostWebAccessKey ?? "").Trim()
            : "";
        RememberHost(config, host, savedKey);
        string? targetUrl = null;
        switch (await WorkstationNetwork.ProbeWebAccessAsync(
                    address,
                    savedKey.Length > 0 ? savedKey : null,
                    token))
        {
            case WorkstationNetwork.WebAccessProbeResult.Authorized:
                // 已有可用密钥时直接用同一把密钥打开，不再走申请流程
                targetUrl = WorkstationNetwork.BuildWebAccessUrl(
                    address,
                    savedKey.Length > 0 ? savedKey : null);
                break;
            case WorkstationNetwork.WebAccessProbeResult.Unauthorized:
                // 首次连接必须让主人知道要去主机上点允许，否则只会在主机端莫名弹窗
                MacDialog.ShowMessage(
                    $"「{Describe(host)}」还没有允许这台电脑查看。\n\n"
                        + "请到保存主机上点“允许”，允许后这台电脑以后可以直接查看，不用再确认。",
                    "PackingProof 查看端");
                Console.WriteLine($"主机开启了网页访问保护，正在申请接入，请到 {Describe(host)} 上点允许…");
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

        RememberHost(config, host, ExtractAccessKey(targetUrl!));
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

    /// <summary>本机自己就是这台主机（NodeId 相同）时不能作为查看端连自己。</summary>
    private static bool IsSelf(AppConfig config, PackingProofNodeInfo host) =>
        !string.IsNullOrWhiteSpace(config.NodeId)
        && string.Equals(config.NodeId, host.NodeId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 记住主机：除了身份，还要记下**地址**与网页访问密钥。
    /// 密钥按调用方传入的值整体覆盖（换主机传空串即丢弃旧密钥），
    /// 不能在这里自己从地址里猜，否则拿不到密钥的调用会把已保存的密钥清掉。
    /// 用 TryUpdate 在锁内读最新配置再改：连接主机要等对方点允许，
    /// 这段时间用户可能在菜单里改别的设置，整份回写旧配置会把那些改动冲掉。
    /// </summary>
    private static void RememberHost(AppConfig config, PackingProofNodeInfo host, string webAccessKey)
    {
        string key = (webAccessKey ?? "").Trim();
        if (!WorkstationConfigStore.TryUpdate(
                latest =>
                {
                    latest.LastKnownHostNodeId = host.NodeId;
                    latest.LastKnownHostNodeName = host.NodeName;
                    latest.LastKnownHostAddress = host.Address;
                    latest.LastKnownHostWebAccessKey = key;
                },
                out AppConfig saved,
                out _))
        {
            return;
        }

        config.LastKnownHostNodeId = saved.LastKnownHostNodeId;
        config.LastKnownHostNodeName = saved.LastKnownHostNodeName;
        config.LastKnownHostAddress = saved.LastKnownHostAddress;
        config.LastKnownHostWebAccessKey = saved.LastKnownHostWebAccessKey;
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
