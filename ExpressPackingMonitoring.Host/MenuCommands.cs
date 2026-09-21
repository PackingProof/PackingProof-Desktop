// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 PackingProof contributors

using System.Globalization;
using System.Text.Json;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
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

        string? setPurpose = ReadOption(arguments, "--set-purpose");
        if (setPurpose != null)
        {
            ApplyPurpose(setPurpose);
            return true;
        }

        string? addStorage = ReadOption(arguments, "--add-storage");
        if (addStorage != null)
        {
            AddStorageLocation(addStorage);
            return true;
        }

        string? setAutostart = ReadOption(arguments, "--set-autostart");
        if (setAutostart != null)
        {
            ApplyAutostart(setAutostart);
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

    /// <summary>切换用途（只写配置，不启动任何会话；进程由菜单栏壳负责）。</summary>
    private static void ApplyPurpose(string purpose)
    {
        string mapped = purpose.Trim().ToLowerInvariant() switch
        {
            "host" => DeploymentPresets.MobileBackupHost,
            "viewer" => DeploymentPresets.ViewerClient,
            _ => ""
        };
        if (mapped.Length == 0)
        {
            WriteJson(new { ok = false, error = "用途只能是保存主机或查看端" });
            return;
        }

        AppConfig config = WorkstationConfigStore.Load();
        if (!string.Equals(config.DeploymentPreset, mapped, StringComparison.Ordinal))
        {
            RuntimeLog.Info("MenuCommands", $"SetPurpose {config.DeploymentPreset} -> {mapped}");
            config.DeploymentPreset = mapped;
            AppConfig.NormalizeAfterLoad(config);
            AppConfig.MarkDeploymentSetupCompleted(config);
            if (!WorkstationConfigStore.TrySave(config, out string saveError))
            {
                WriteJson(new { ok = false, error = saveError });
                return;
            }
        }

        WriteJson(new
        {
            ok = true,
            purpose = DeploymentPresets.Normalize(config.DeploymentPreset),
            purposeName = DeploymentPresets.GetDisplayName(config.DeploymentPreset)
        });
    }

    /// <summary>追加一个保存位置（按优先级排在最后），与桌面端"添加磁盘"一致。</summary>
    private static void AddStorageLocation(string path)
    {
        string full = (path ?? "").Trim();
        if (full.Length == 0 || !Path.IsPathRooted(full))
        {
            WriteJson(new { ok = false, error = "存储位置必须是绝对路径" });
            return;
        }

        try
        {
            Directory.CreateDirectory(full);
        }
        catch (Exception ex)
        {
            WriteJson(new { ok = false, error = $"无法使用该目录：{ex.Message}" });
            return;
        }

        AppConfig config = WorkstationConfigStore.Load();
        List<StorageLocation> locations = OrderedLocations(config);
        if (!locations.Any(location => string.Equals(location.Path, full, StringComparison.Ordinal)))
        {
            RuntimeLog.Info("MenuCommands", $"AddStorage path={full}");
            int nextPriority = locations.Count == 0 ? 1 : locations.Max(location => location.Priority) + 1;
            locations.Add(new StorageLocation
            {
                Path = full,
                Priority = nextPriority,
                IsBackupTarget = false
            });
            config.StorageLocations = locations;
            if (!WorkstationConfigStore.TrySave(config, out string saveError))
            {
                WriteJson(new { ok = false, error = saveError });
                return;
            }
        }

        WriteJson(new { ok = true, path = full });
    }

    /// <summary>开关开机自启；装了自启时保存主机归 launchd 托管。</summary>
    private static void ApplyAutostart(string value)
    {
        bool enable = value.Trim().ToLowerInvariant() is "on" or "true" or "1";
        RuntimeLog.Info("MenuCommands", $"SetAutostart enable={enable}");
        bool ok = enable
            ? LaunchAgentInstaller.TryInstall(out string error)
            : LaunchAgentInstaller.TryUninstall(out error);
        if (!ok)
        {
            WriteJson(new { ok = false, error });
            return;
        }

        WriteJson(new { ok = true, autostartInstalled = LaunchAgentInstaller.IsInstalled });
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

        RuntimeLog.Info(
            "MenuCommands",
            $"SetStorageLimit byCapacity={byCapacity}, value={gigabytes}, path={location.Path}");
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
            // 本机自己不能连自己：用途互斥，列出来只会让人点到"自己"
            .Where(host => host.IsValidHost
                && !string.Equals(host.NodeId, config.NodeId, StringComparison.OrdinalIgnoreCase))
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
        // 手动连接允许直接粘贴带 key 的完整链接（与 MacViewer 的手动连接一致）：
        // 拿到 key 就不用再走一次申请授权
        string keyFromInput = ExtractAccessKey(address);

        RuntimeLog.Info(
            "MenuCommands",
            $"SelectHost address={normalizedAddress}, nodeChanged={nodeChanged}");

        config.LastKnownHostAddress = normalizedAddress;
        config.LastKnownHostNodeId = normalizedNodeId;
        config.LastKnownHostNodeName = (nodeName ?? "").Trim();
        if (keyFromInput.Length > 0)
        {
            config.LastKnownHostWebAccessKey = keyFromInput;
        }
        else if (nodeChanged)
        {
            config.LastKnownHostWebAccessKey = "";
        }

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
        RuntimeLog.Info("MenuCommands", "ForgetHost 清除已记住的主机");
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
                    // 稳态说明"需要主机允许连接"，不是"正在请求"：请求动作要用户点了才有
                    text = ViewerConnectionStatusText.NeedsHostApproval,
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

    /// 从"主机地址或连接链接"里取出 ?key=，取不到返回空串。
    private static string ExtractAccessKey(string input)
    {
        if (!Uri.TryCreate(input?.Trim() ?? "", UriKind.Absolute, out Uri? uri))
            return "";

        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            if (parts.Length == 2 && string.Equals(parts[0], "key", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(parts[1]);
        }

        return "";
    }

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
