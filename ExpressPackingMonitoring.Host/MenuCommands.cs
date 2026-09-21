// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 PackingProof contributors

using System.Globalization;
using System.Text.Json;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.Host;

/// <summary>
/// 菜单栏壳与 .NET 主机之间的命令行接口：壳只负责展示，容量、主机列表与状态词一律取自核心。
///
/// 为什么走命令行而不是本地设置接口：查看端要能切换/移除已记住的主机，而查看端不常驻 HTTP 服务，
/// 只有命令行在两种用途下都能用同一份配置读写代码。
/// </summary>
internal static class MenuCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>命中菜单命令就执行并返回 true；没命中返回 false，交给主流程继续。</summary>
    internal static async Task<bool> TryRunAsync(string[] arguments, CancellationToken token)
    {
        if (HasFlag(arguments, "--storage-summary"))
        {
            WriteJson(new { locations = DescribeStorageLocations(WorkstationConfigStore.Load()) });
            return true;
        }

        string? setCapacity = ReadOption(arguments, "--set-storage-capacity");
        string? setReserve = ReadOption(arguments, "--set-storage-reserve");
        if (setCapacity != null || setReserve != null)
        {
            ApplyStorageLimit(
                byCapacity: setCapacity != null,
                rawValue: (setCapacity ?? setReserve)!,
                requestedPath: ReadOption(arguments, "--storage-path"));
            return true;
        }

        if (HasFlag(arguments, "--list-hosts"))
        {
            WriteJson(new { hosts = await DiscoverHostsAsync(token) });
            return true;
        }

        string? selectHost = ReadOption(arguments, "--select-host");
        if (selectHost != null)
        {
            SelectHost(
                selectHost,
                ReadOption(arguments, "--host-node-id"),
                ReadOption(arguments, "--host-node-name"));
            return true;
        }

        if (HasFlag(arguments, "--forget-host"))
        {
            ForgetHost();
            return true;
        }

        if (HasFlag(arguments, "--viewer-status"))
        {
            await WriteViewerStatusAsync(ReadOption(arguments, "--state"), token);
            return true;
        }

        return false;
    }

    /// <summary>各保存位置的容量现状：总量、可用、容量上限与预留，全部按真实卷计算。</summary>
    private static List<object> DescribeStorageLocations(AppConfig config)
    {
        var summaries = new List<object>();
        foreach (StorageLocation location in OrderedLocations(config))
        {
            bool volumeKnown = StorageVolumeInfo.TryGet(location.Path, out StorageVolumeInfo volume);
            // 容量上限与预留必须加起来等于卷总容量，否则菜单上两个数字互相打架：
            // 没单独设置过预留时按生效的默认预留算容量，而不是按底线算（底线只决定可设的最大值）
            bool capacityKnown = StorageCapacityPolicy.TryGetCapacityGB(
                location,
                out _,
                out double maximumCapacityGB);
            double reserveGB = Math.Ceiling(StorageSpacePolicy.GetEffectiveReserveGB(location));
            double capacityGB = capacityKnown && StorageCapacityPolicy.TryGetTotalGB(location, out long totalGB)
                ? Math.Max(0, totalGB - reserveGB)
                : 0;

            summaries.Add(new
            {
                path = location.Path,
                displayName = Path.GetFileName(
                    location.Path.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar)),
                available = volumeKnown,
                totalGB = volumeKnown
                    ? Math.Round(volume.TotalSize / (double)StorageSpacePolicy.BytesPerGiB, 1)
                    : 0,
                freeGB = volumeKnown
                    ? Math.Round(volume.AvailableFreeSpace / (double)StorageSpacePolicy.BytesPerGiB, 1)
                    : 0,
                reserveGB,
                recommendedReserveGB = Math.Round(StorageSpacePolicy.GetDefaultReserveGB(location.Path), 1),
                capacityKnown,
                capacityGB,
                maximumCapacityGB = capacityKnown ? Math.Round(maximumCapacityGB, 1) : 0
            });
        }

        return summaries;
    }

    /// <summary>设置容量上限或预留空间；两者共用桌面端同一套换算，写进同一个 ReserveGB。</summary>
    private static void ApplyStorageLimit(bool byCapacity, string rawValue, string? requestedPath)
    {
        if (!double.TryParse(
                rawValue,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double gigabytes)
            || !double.IsFinite(gigabytes)
            || gigabytes <= 0)
        {
            WriteJson(new { ok = false, error = "容量必须是大于 0 的数字（单位 GB）" });
            return;
        }

        AppConfig config = WorkstationConfigStore.Load();
        StorageLocation? location = FindLocation(config, requestedPath);
        if (location == null)
        {
            WriteJson(new { ok = false, error = "还没有设置保存位置，先添加磁盘" });
            return;
        }

        bool applied = byCapacity
            ? StorageCapacityPolicy.TryApplyCapacityGB(location, gigabytes)
            : StorageCapacityPolicy.TryApplyReserveGB(location, gigabytes);
        if (!applied)
        {
            WriteJson(new { ok = false, error = "读不到该磁盘的真实容量，暂时不能设置容量上限" });
            return;
        }

        if (!WorkstationConfigStore.TrySave(config, out string saveError))
        {
            WriteJson(new { ok = false, error = saveError });
            return;
        }

        StorageCapacityPolicy.TryGetCapacityGB(
            location,
            out double capacityGB,
            out double maximumCapacityGB);
        WriteJson(new
        {
            ok = true,
            path = location.Path,
            reserveGB = Math.Round(StorageSpacePolicy.GetEffectiveReserveGB(location), 1),
            capacityGB = Math.Round(capacityGB, 1),
            maximumCapacityGB = Math.Round(maximumCapacityGB, 1)
        });
    }

    /// <summary>发现同一网络里的保存主机；复用桌面端查看窗口的同一套发现代码。</summary>
    private static async Task<List<object>> DiscoverHostsAsync(CancellationToken token)
    {
        AppConfig config = WorkstationConfigStore.Load();
        IReadOnlyList<PackingProofNodeInfo> hosts = await WorkstationNetwork.FindHostsAsync(
            lastKnownAddress: config.LastKnownHostAddress,
            config.WebServerPort,
            progress: null,
            hostProgress: null,
            token);

        return hosts
            .Where(host => host.IsValidHost)
            .OrderBy(host => host.NodeName, StringComparer.CurrentCulture)
            .Select(host => (object)new
            {
                nodeId = host.NodeId,
                nodeName = host.NodeName,
                address = host.Address
            })
            .ToList();
    }

    /// <summary>切换查看端主机：换主机时清掉旧主机发的网页访问密钥，避免拿旧 key 去连新主机。</summary>
    private static void SelectHost(string address, string? nodeId, string? nodeName)
    {
        if (string.IsNullOrWhiteSpace(address)
            || WorkstationNetwork.NormalizeAddress(address).Length == 0)
        {
            WriteJson(new { ok = false, error = "主机地址无效" });
            return;
        }

        AppConfig config = WorkstationConfigStore.Load();
        string normalizedAddress = WorkstationNetwork.BuildWebAccessUrl(address, null);
        string normalizedNodeId = (nodeId ?? "").Trim();
        bool nodeChanged = !string.Equals(
            config.LastKnownHostNodeId,
            normalizedNodeId,
            StringComparison.OrdinalIgnoreCase);

        config.LastKnownHostAddress = normalizedAddress;
        config.LastKnownHostNodeId = normalizedNodeId;
        config.LastKnownHostNodeName = (nodeName ?? "").Trim();
        if (nodeChanged) config.LastKnownHostWebAccessKey = "";

        if (!WorkstationConfigStore.TrySave(config, out string error))
        {
            WriteJson(new { ok = false, error });
            return;
        }

        WriteJson(new
        {
            ok = true,
            nodeId = config.LastKnownHostNodeId,
            nodeName = config.LastKnownHostNodeName,
            address = config.LastKnownHostAddress
        });
    }

    /// <summary>移除查看端已记住的主机，连同旧密钥一起清掉。</summary>
    private static void ForgetHost()
    {
        AppConfig config = WorkstationConfigStore.Load();
        config.LastKnownHostNodeId = "";
        config.LastKnownHostNodeName = "";
        config.LastKnownHostAddress = "";
        config.LastKnownHostWebAccessKey = "";

        if (!WorkstationConfigStore.TrySave(config, out string error))
        {
            WriteJson(new { ok = false, error });
            return;
        }

        WriteJson(new { ok = true });
    }

    /// <summary>
    /// 查看端一行状态：文案取自核心的唯一来源，与 Windows 查看窗口同一套口径；
    /// 搜索中不探测网络，直接给状态词，避免菜单在搜索期间卡住。
    /// </summary>
    private static async Task WriteViewerStatusAsync(string? state, CancellationToken token)
    {
        AppConfig config = WorkstationConfigStore.Load();
        // 指定状态名时只取核心的词表，不探测网络（搜索中、未绑定这类状态壳要用同一句话）
        if (!string.IsNullOrWhiteSpace(state)
            && ViewerConnectionStatusText.AllWords.TryGetValue(state.Trim(), out string? word))
        {
            WriteJson(new
            {
                state = state.Trim(),
                text = word,
                texts = ViewerConnectionStatusText.AllWords
            });
            return;
        }

        if (string.IsNullOrWhiteSpace(config.LastKnownHostAddress))
        {
            WriteJson(new
            {
                state = "notBound",
                text = ViewerConnectionStatusText.NotBound,
                texts = ViewerConnectionStatusText.AllWords
            });
            return;
        }

        WorkstationNetwork.WebAccessProbeResult probe = await WorkstationNetwork.ProbeWebAccessAsync(
            config.LastKnownHostAddress,
            config.LastKnownHostWebAccessKey,
            token);
        switch (probe)
        {
            case WorkstationNetwork.WebAccessProbeResult.Authorized:
                WriteJson(new
                {
                    state = "online",
                    text = ViewerConnectionStatusText.Connected(config.LastKnownHostNodeName),
                    texts = ViewerConnectionStatusText.AllWords
                });
                break;
            case WorkstationNetwork.WebAccessProbeResult.Unauthorized:
                WriteJson(new
                {
                    state = "approval",
                    text = ViewerConnectionStatusText.RequestingHostApproval,
                    texts = ViewerConnectionStatusText.AllWords
                });
                break;
            default:
                WriteJson(new
                {
                    state = "offline",
                    text = ViewerConnectionStatusText.HostOfflineOrChanged,
                    texts = ViewerConnectionStatusText.AllWords
                });
                break;
        }
    }

    private static List<StorageLocation> OrderedLocations(AppConfig config) =>
        (config.StorageLocations ?? [])
            .Where(location => !string.IsNullOrWhiteSpace(location.Path))
            .OrderBy(location => location.Priority)
            .ToList();

    private static StorageLocation? FindLocation(AppConfig config, string? requestedPath)
    {
        List<StorageLocation> locations = OrderedLocations(config);
        if (locations.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(requestedPath)) return locations[0];

        string wanted = TrimPath(requestedPath);
        return locations.FirstOrDefault(location =>
                string.Equals(TrimPath(location.Path), wanted, StringComparison.Ordinal))
            ?? locations[0];
    }

    private static string TrimPath(string path) =>
        path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool HasFlag(string[] arguments, string name) =>
        arguments.Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));

    private static string? ReadOption(string[] arguments, string name)
    {
        for (int index = 0; index < arguments.Length - 1; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                return arguments[index + 1];
        }

        return null;
    }

    private static void WriteJson(object payload) =>
        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
}
