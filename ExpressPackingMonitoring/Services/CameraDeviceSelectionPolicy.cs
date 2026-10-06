using System;
using System.Collections.Generic;
using System.Linq;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 摄像头设备下拉的**唯一**选设备规则：拿一份完整设备清单，投影成某一"路"能选的项 ——
/// 排除其它路已占用的设备，但保留自己当前选中的那台（否则重算后选中项会被清空）。
///
/// 主摄、副摄、以后的第三/第四路都调这里，页面与 ViewModel 不再各写一套过滤逻辑；
/// 任一路换了设备，就用新的占用关系把各路各投影一次，几个下拉同时正确。
/// </summary>
internal static class CameraDeviceSelectionPolicy
{
    /// <summary>网络摄像头在下拉里的固定身份：它不占用本机设备，但不能和"无/未检测到"混成一个空串。</summary>
    internal const string NetworkIdentity = "network:";

    /// <summary>
    /// 投影某一路的可选项。<paramref name="otherMonikers"/> 是其它路占用的设备标识；
    /// 自己当前那台（<paramref name="selfMoniker"/>）即使被算进占用也保留在列表首位。
    /// </summary>
    internal static IReadOnlyList<CameraDeviceChoice> Project(
        IReadOnlyList<CameraDeviceChoice> allDevices,
        string? selfMoniker,
        IEnumerable<string?> otherMonikers)
    {
        var occupied = otherMonikers
            .Where(m => !string.IsNullOrEmpty(m))
            .Select(m => m!)
            .ToHashSet(StringComparer.Ordinal);

        var list = new List<CameraDeviceChoice>(allDevices.Count);
        foreach (CameraDeviceChoice device in allDevices)
        {
            if (device.Kind == "usb" && occupied.Contains(device.Moniker))
                continue;

            list.Add(device);
        }

        if (!string.IsNullOrEmpty(selfMoniker)
            && list.All(d => !string.Equals(d.Moniker, selfMoniker, StringComparison.Ordinal)))
        {
            CameraDeviceChoice? self = allDevices.FirstOrDefault(
                d => string.Equals(d.Moniker, selfMoniker, StringComparison.Ordinal));
            if (self != null)
                list.Insert(0, self);
        }

        return list;
    }

    /// <summary>
    /// 某一路当前选中的设备还能不能用：被其它路占用了、或设备已经不在清单里，就不能用，
    /// 调用方应把它退回"无"。
    /// </summary>
    internal static bool CanKeepSelection(
        string? selfMoniker,
        IEnumerable<string?> otherMonikers,
        IReadOnlyList<CameraDeviceChoice> allDevices)
    {
        if (string.IsNullOrEmpty(selfMoniker))
            return true;

        if (otherMonikers.Any(m => string.Equals(m, selfMoniker, StringComparison.Ordinal)))
            return false;

        return allDevices.Any(d =>
            d.Kind == "usb" && string.Equals(d.Moniker, selfMoniker, StringComparison.Ordinal));
    }

    /// <summary>
    /// 多路通道的占用归一：按**优先级从高到低**依次占设备（主摄在前、副摄在后），
    /// 某一路要占的标识为空、被前面那一路占了、或者清单里已经找不到这台设备，就返回空串
    /// （调用方应把这一路退回"无"）。
    ///
    /// 这样"两路选了同一台"永远只有一种结果：优先级高的保留、低的让位。
    /// 不这么做的话，两边各自投影会互相把对方挤掉，下拉会在两台设备之间来回跳。
    /// </summary>
    internal static IReadOnlyList<string> ResolveOwnership(
        IReadOnlyList<string?> requestedMonikers,
        IReadOnlyList<CameraDeviceChoice> allDevices)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new string[requestedMonikers.Count];
        for (int i = 0; i < requestedMonikers.Count; i++)
        {
            string? moniker = requestedMonikers[i];
            resolved[i] = "";
            if (string.IsNullOrEmpty(moniker))
                continue;

            bool exists = allDevices.Any(d =>
                d.Kind == "usb" && string.Equals(d.Moniker, moniker, StringComparison.Ordinal));
            if (!exists || !taken.Add(moniker!))
                continue;

            resolved[i] = moniker!;
        }

        return resolved;
    }

    /// <summary>下拉项的"身份"：USB 用设备标识，网络摄像头固定 <see cref="NetworkIdentity"/>，其余为空串。</summary>
    internal static string IdentityOf(CameraDeviceChoice? choice) =>
        choice == null
            ? ""
            : choice.Kind == "network"
                ? NetworkIdentity
                : choice.Moniker ?? "";

    /// <summary>
    /// 当前该选中哪一项：先按身份精确匹配，匹配不到才退回第一项（"无/未检测到摄像头"）。
    ///
    /// 不能只看设备标识是否为空——没有 USB 摄像头时"未检测到摄像头"和"网络摄像头"的标识都是空的，
    /// 那样会把用户刚选好的网络摄像头又顶回"未检测到摄像头"，网络摄像头地址框跟着被收起来。
    /// </summary>
    internal static int SelectIdentityIndex(IReadOnlyList<CameraDeviceChoice>? choices, string? identity)
    {
        if (choices == null || choices.Count == 0)
            return -1;

        string target = identity ?? "";
        for (int i = 0; i < choices.Count; i++)
        {
            if (string.Equals(IdentityOf(choices[i]), target, StringComparison.Ordinal))
                return i;
        }

        return 0;
    }
}
