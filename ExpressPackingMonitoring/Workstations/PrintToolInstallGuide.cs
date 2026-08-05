using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExpressPackingMonitoring;

internal static class PrintToolInstallGuide
{
    private const string TemplateFileName = "kuaidizs-install-guide.html";
    private const string ScriptFileName = "快递助手订单推送.user.js";
    private const string UpdateUrlsMarker = "// PACKING_PROOF_UPDATE_URLS";

    public static string CreateLocalGuide(string monitorAddress)
    {
        string guideDir = AppPaths.GuideCacheDir;
        Directory.CreateDirectory(guideDir);
        string guidePath = Path.Combine(guideDir, TemplateFileName);
        string sourceScriptPath = ResolveUserscriptPath();
        string scriptPath = Path.Combine(guideDir, ScriptFileName);
        AppConfig config = WorkstationConfigStore.Load();
        string hostAddress = RecordingDeviceCatalog.NormalizeLanHttpAddress(monitorAddress, config.WebServerPort);
        if (hostAddress.Length == 0)
            hostAddress = $"http://{WorkstationNetwork.GetBestLocalAccessAddress(config.WebServerPort)}";
        var receivers = new MobileOrderReceiverRegistry();
        IReadOnlyList<RecordingDeviceInfo> devices = RecordingDeviceCatalog.Build(
            config.DeploymentPreset,
            config.NodeId,
            config.NodeName,
            config.WebServerPort,
            hostAddress,
            receivers.GetKnownRecordingDevices(),
            connectedClients: null,
            includeOffline: true);
        if (File.Exists(sourceScriptPath) && devices.Count > 0)
        {
            string script = File.ReadAllText(sourceScriptPath, Encoding.UTF8);
            File.WriteAllText(scriptPath, AddRecordingDevices(script, devices), Encoding.UTF8);
        }
        string scriptUrl = devices.Count > 0 && File.Exists(scriptPath) ? new Uri(scriptPath).AbsoluteUri : "";
        string html = RenderForWeb(devices, scriptUrl);
        File.WriteAllText(guidePath, html, Encoding.UTF8);
        return guidePath;
    }

    public static string RenderForWeb(string monitorAddress, string scriptUrl)
    {
        return Render(monitorAddress, BuildScriptLink(scriptUrl));
    }

    public static string RenderForWeb(IReadOnlyList<RecordingDeviceInfo> devices, string scriptUrl)
    {
        string deviceSummary = devices.Count == 0
            ? "<div class=\"warn\">当前没有发现可接收订单的录像设备。请检查录像设备连接后返回桌面程序重试。</div>"
            : $"""
<div class="devices">
  <strong><span>找到</span> {devices.Count} <span>个录像设备</span></strong>
  <ul>{string.Join("", devices.Select(device =>
      $"<li>{WebUtility.HtmlEncode(device.NodeName)}（{WebUtility.HtmlEncode(device.DeviceType)}，{(device.Online ? "在线" : "离线")}）：{WebUtility.HtmlEncode(new Uri(device.Address).Authority)}</li>"))}</ul>
  <p>脚本会把订单同时发送给以上所有录像设备；离线设备发送失败不会影响其他设备。</p>
</div>
""";
        string template = LoadTemplate();
        return template
            .Replace("{{scriptLink}}", devices.Count == 0 ? "" : BuildScriptLink(scriptUrl), StringComparison.Ordinal)
            .Replace("{{address}}", devices.Count == 0
                ? ""
                : WebUtility.HtmlEncode(string.Join("、", devices.Select(device => new Uri(device.Address).Authority))), StringComparison.Ordinal)
            .Replace("{{deviceSummary}}", deviceSummary, StringComparison.Ordinal);
    }

    public static string ResolveUserscriptPath()
    {
        string[] candidates =
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Scripts", ScriptFileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Scripts", ScriptFileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Scripts", ScriptFileName))
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    internal static string AddMonitorConnectPermission(string script, string monitorAddress)
    {
        return AddMonitorConnectPermissions(script, new[] { monitorAddress });
    }

    internal static string AddMonitorConnectPermissions(string script, IEnumerable<string> monitorAddresses)
    {
        if (string.IsNullOrWhiteSpace(script)) return script;

        List<Uri> addresses = NormalizeMonitorAddresses(monitorAddresses);
        if (addresses.Count == 0) return script;

        string customized = script.Replace(
            "const INSTALL_MONITOR_ADDRESSES = [];",
            $"const INSTALL_MONITOR_ADDRESSES = {JsonSerializer.Serialize(addresses.Select(uri => uri.Authority))};",
            StringComparison.Ordinal);
        customized = customized.Replace(
            "const INSTALL_PRIMARY_MONITOR_ADDRESS = '';",
            $"const INSTALL_PRIMARY_MONITOR_ADDRESS = {JsonSerializer.Serialize(addresses[0].Authority)};",
            StringComparison.Ordinal);
        string newline = customized.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        const string marker = "// @connect      localhost";
        int markerIndex = customized.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0) return customized;
        int lineEnd = customized.IndexOf('\n', markerIndex);
        int insertIndex = lineEnd < 0 ? customized.Length : lineEnd + 1;
        string prefix = lineEnd < 0 ? newline : "";

        foreach (string host in addresses.Select(uri => uri.Host).Distinct(StringComparer.OrdinalIgnoreCase).Reverse())
        {
            string directive = $"// @connect      {host}";
            if (!customized.Contains(directive, StringComparison.Ordinal))
                customized = customized.Insert(insertIndex, prefix + directive + newline);
        }

        return customized;
    }

    internal static string AddRecordingDevices(
        string script,
        IEnumerable<RecordingDeviceInfo> recordingDevices,
        PackingProofNodeInfo? host = null)
    {
        if (string.IsNullOrWhiteSpace(script))
            return script;

        RecordingDeviceInfo[] devices = NormalizeRecordingDevices(recordingDevices);
        if (devices.Length == 0)
            return script;

        var payload = devices.Select(device => new
        {
            nodeId = device.NodeId,
            name = device.NodeName,
            type = device.DeviceType,
            url = device.Address
        });
        string customized = script.Replace(
            "const PACKING_PROOF_RECORDERS = [];",
            $"const PACKING_PROOF_RECORDERS = {JsonSerializer.Serialize(payload)};",
            StringComparison.Ordinal);
        customized = customized.Replace(
            "const PACKING_PROOF_HOST = null;",
            $"const PACKING_PROOF_HOST = {JsonSerializer.Serialize(host == null ? null : new { host.NodeId, host.NodeName, host.Address })};",
            StringComparison.Ordinal);

        IEnumerable<string> connectAddresses = devices.Select(device => device.Address);
        if (!string.IsNullOrWhiteSpace(host?.Address))
            connectAddresses = connectAddresses.Append(host.Address);
        List<Uri> addresses = NormalizeMonitorAddresses(connectAddresses);
        customized = AddExactConnectPermissions(customized, addresses.Select(uri => uri.Host));
        return customized;
    }

    /// <summary>把油猴元数据占位行替换为指向当前工位的 @updateURL / @downloadURL。</summary>
    internal static string AddUserscriptUpdateUrls(string script, string scriptUrl)
    {
        if (string.IsNullOrWhiteSpace(script) || string.IsNullOrWhiteSpace(scriptUrl))
            return script;
        string newline = script.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string replacement =
            $"// @updateURL     {scriptUrl}{newline}" +
            $"// @downloadURL   {scriptUrl}";
        return script.Replace(UpdateUrlsMarker, replacement, StringComparison.Ordinal);
    }

    /// <summary>在模板基础版本后追加配置修订号：2.12 → 2.12.0 / 2.12.3。</summary>
    internal static string RewriteUserscriptVersion(string script, int revision)
    {
        if (string.IsNullOrWhiteSpace(script) || revision < 0)
            return script;
        return Regex.Replace(
            script,
            @"(// @version\s+[^\r\n\s]+)",
            match => match.Value + "." + revision,
            RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// 计算设备配置指纹：与 AddRecordingDevices 使用同一份去重后的设备集，
    /// 按地址升序归一化，保证“最近活跃顺序变化”不触发修订号递增。
    /// </summary>
    internal static string ComputeConfigFingerprint(
        IEnumerable<RecordingDeviceInfo>? recordingDevices,
        PackingProofNodeInfo? host)
    {
        RecordingDeviceInfo[] devices = NormalizeRecordingDevices(recordingDevices);
        var builder = new StringBuilder();
        foreach (RecordingDeviceInfo device in devices
            .OrderBy(device => device.Address, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(device.NodeId).Append('\n');
            builder.Append(device.NodeName).Append('\n');
            builder.Append(device.DeviceType).Append('\n');
            builder.Append(device.Address).Append('\n');
        }
        if (host != null)
        {
            builder.Append(host.NodeId).Append('\n');
            builder.Append(host.NodeName).Append('\n');
            builder.Append(host.Address).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static RecordingDeviceInfo[] NormalizeRecordingDevices(
        IEnumerable<RecordingDeviceInfo>? recordingDevices)
    {
        return (recordingDevices ?? [])
            .Where(device => device != null)
            .GroupBy(device => device.Address, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static string AddExactConnectPermissions(string script, IEnumerable<string> hosts)
    {
        string newline = script.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        const string dynamicMarker = "// PACKING_PROOF_CONNECT_TARGETS";
        int markerIndex = script.IndexOf(dynamicMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            const string legacyMarker = "// @connect      localhost";
            markerIndex = script.IndexOf(legacyMarker, StringComparison.Ordinal);
        }
        if (markerIndex < 0)
            return script;

        int lineEnd = script.IndexOf('\n', markerIndex);
        int insertIndex = lineEnd < 0 ? script.Length : lineEnd + 1;
        string prefix = lineEnd < 0 ? newline : "";
        foreach (string host in hosts.Distinct(StringComparer.OrdinalIgnoreCase).Reverse())
        {
            string directive = $"// @connect      {host}";
            if (!script.Contains(directive, StringComparison.Ordinal))
                script = script.Insert(insertIndex, prefix + directive + newline);
        }
        return script;
    }

    internal static List<Uri> NormalizeMonitorAddresses(IEnumerable<string>? monitorAddresses)
    {
        var result = new List<Uri>();
        foreach (string rawAddress in monitorAddresses ?? Array.Empty<string>())
        {
            foreach (string part in (rawAddress ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                string value = part.Contains("://", StringComparison.Ordinal) ? part : "http://" + part;
                if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
                    || !IsAllowedMonitorHost(uri.Host)
                    || uri.Port is <= 0 or > 65535)
                    continue;

                int port = uri.IsDefaultPort ? 5280 : uri.Port;
                var normalized = new UriBuilder(Uri.UriSchemeHttp, uri.Host, port).Uri;
                if (result.Any(item => string.Equals(item.Authority, normalized.Authority, StringComparison.OrdinalIgnoreCase)))
                    continue;

                result.Add(normalized);
                if (result.Count >= 8) break;
            }

            if (result.Count >= 8) break;
        }

        return result;
    }

    private static bool IsAllowedMonitorHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out IPAddress? address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;

        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    private static string Render(string monitorAddress, string scriptLink)
    {
        string template = LoadTemplate();
        return template
            .Replace("{{scriptLink}}", scriptLink, StringComparison.Ordinal)
            .Replace("{{address}}", WebUtility.HtmlEncode(monitorAddress), StringComparison.Ordinal)
            .Replace("{{deviceSummary}}", "", StringComparison.Ordinal);
    }

    private static string LoadTemplate()
    {
        string[] candidates =
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Web", TemplateFileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Web", TemplateFileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Web", TemplateFileName))
        };

        string? path = candidates.FirstOrDefault(File.Exists);
        return path == null ? MissingTemplateHtml : File.ReadAllText(path, Encoding.UTF8);
    }

    private static string BuildScriptLink(string scriptUrl)
    {
        return string.IsNullOrWhiteSpace(scriptUrl)
            ? "<div class=\"warn\">未找到订单联动脚本文件，请确认发布包内包含 Scripts 文件夹。</div>"
            : $"<a class=\"primary\" href=\"{WebUtility.HtmlEncode(scriptUrl)}\" target=\"_blank\" rel=\"noopener\">安装订单联动</a>";
    }

    private const string MissingTemplateHtml = """
<!doctype html>
<html lang="zh-CN">
<head><meta charset="utf-8"><title>安装向导缺失</title></head>
<body style="font-family: Microsoft YaHei UI, sans-serif; padding: 32px;">
  <h1>安装向导文件缺失</h1>
  <p>未找到 Web/kuaidizs-install-guide.html，请检查程序发布目录是否完整。</p>
</body>
</html>
""";
}
