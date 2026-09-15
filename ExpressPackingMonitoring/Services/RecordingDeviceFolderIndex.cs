using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 录像目录里的"设备对照表"。
///
/// 目录名只用设备号（设备-XXXXXX），昵称随时可以改，所以不写进文件夹名。
/// 用户在录像文件夹里靠这份表格把目录对上设备昵称，主机每次收到备份或改名后刷新它。
/// </summary>
internal static class RecordingDeviceFolderIndex
{
    internal const string FileName = "设备对照表.txt";

    internal static string BuildContent(
        IEnumerable<MobileOrderReceiverInfo> devices,
        DateTime updatedAtLocal)
    {
        var builder = new StringBuilder();
        builder.AppendLine("设备对照表（由主机自动维护，请勿手工修改）");
        builder.AppendLine("录像目录按设备号命名，昵称可以随时修改，所以昵称不写进文件夹名。");
        builder.AppendLine("本表用于对照：目录 <-> 设备昵称。");
        builder.AppendLine($"更新时间：{updatedAtLocal:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine();

        MobileOrderReceiverInfo[] ordered = (devices ?? [])
            .Where(device => !string.IsNullOrWhiteSpace(device.NodeName) || !string.IsNullOrWhiteSpace(device.NodeId))
            .OrderBy(device => device.NodeName?.Trim() ?? "", StringComparer.CurrentCulture)
            .ToArray();
        if (ordered.Length == 0)
        {
            builder.AppendLine("（还没有设备向这台主机备份过录像）");
            return builder.ToString();
        }

        builder.AppendLine("目录\t昵称\t设备号\t最后在线（本机时间）");
        foreach (MobileOrderReceiverInfo device in ordered)
        {
            string folder = ArchivePathBuilder.GetDeviceDirectoryName(device.NodeId);
            string name = string.IsNullOrWhiteSpace(device.NodeName) ? "（未命名）" : device.NodeName.Trim();
            // 只写实际时间，不判"在线/离线"：省掉一套判断，用户看一眼就知道多久没连过。
            string lastSeen = device.LastSeenUtc == default
                ? "从未记录"
                : device.LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            builder.AppendLine($"{folder}\t{name}\t{device.NodeId}\t{lastSeen}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// 把对照表写到录像根目录。根目录为空或不可写时静默跳过：
    /// 这只是给用户看的辅助文件，不能因为它影响录像备份本身。
    /// </summary>
    internal static bool TryWrite(
        string? recordingRoot,
        IEnumerable<MobileOrderReceiverInfo> devices,
        DateTime updatedAtLocal)
    {
        string root = recordingRoot?.Trim() ?? "";
        if (root.Length == 0)
            return false;

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(
                Path.Combine(root, FileName),
                BuildContent(devices, updatedAtLocal),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
