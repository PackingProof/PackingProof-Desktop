using System.Net;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 备份上传完成时的设备登记与"来源设备名"取值。
///
/// 记录里的来源名只是写入当时的快照，写哪个名字决定了同一台设备会不会攒出多个名字：
/// 客户端自报的名字可能是它本地的旧值（手机缓存的上一个名字、电脑工位还没应用主机分配
/// 结果），照抄进库就会让一台设备在列表里出现好几个名字。主机分配的昵称才是这台设备
/// 当前唯一的名字，所以按设备类别去对应的昵称表取，取不到才退回客户端自报的名字。
///
/// 电脑工位的名字只有电脑昵称表一个权威来源：手机登记表也按"电脑N"编号，
/// 两边同时给一台电脑发名字会各起一套编号（主机占用电脑1时，工位在两边分别叫
/// 电脑2 和 电脑1），界面里就成了一台设备两个昵称。
/// </summary>
internal static class BackupUploadDeviceRegistration
{
    internal static string RegisterAndResolveSourceName(
        MobileOrderReceiverRegistry mobileRegistry,
        RecordingComputerNicknameRegistry computerRegistry,
        IPAddress? remoteAddress,
        string? nodeId,
        string? clientReportedName,
        string? deviceKind,
        string? platform)
    {
        ArgumentNullException.ThrowIfNull(mobileRegistry);
        ArgumentNullException.ThrowIfNull(computerRegistry);

        string reported = clientReportedName?.Trim() ?? "";
        if (IsComputer(deviceKind, platform))
        {
            string assigned = computerRegistry.Assign(nodeId, reported, customized: false);
            // 仍然登记进手机登记表：设备对照表、可用录像设备列表都读它。
            // trustProvidedName 让登记表原样收下电脑昵称表的分配结果，不再自己编号。
            mobileRegistry.Register(
                remoteAddress,
                nodeId,
                assigned,
                deviceKind: deviceKind,
                platform: platform,
                trustProvidedName: true);
            return assigned;
        }

        MobileOrderReceiverInfo? registered = mobileRegistry.Register(
            remoteAddress,
            nodeId,
            reported,
            deviceKind: deviceKind,
            platform: platform);
        return registered?.NodeName?.Trim() is { Length: > 0 } assignedName ? assignedName : reported;
    }

    private static bool IsComputer(string? deviceKind, string? platform)
    {
        if (string.Equals(deviceKind?.Trim(), "pc", StringComparison.OrdinalIgnoreCase))
            return true;

        string normalizedPlatform = platform?.Trim().ToLowerInvariant() ?? "";
        return normalizedPlatform is "windows" or "macos" or "mac";
    }
}
