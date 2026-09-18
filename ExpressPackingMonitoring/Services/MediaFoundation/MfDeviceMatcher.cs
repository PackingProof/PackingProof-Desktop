using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services.MediaFoundation;

/// <summary>
/// 把 DirectShow 的 MonikerString 对应到 Media Foundation 的符号链接。
///
/// 两套 API 对同一台设备用不同的标识：DirectShow 是
/// <c>@device:pnp:\\?\usb#vid_046d&amp;pid_0825#...{guid}</c>，
/// MF 是 <c>\\?\usb#vid_046d&amp;pid_0825#...{guid}</c>。
/// 现有配置里存的是 MonikerString（用户换设备、重启都靠它认设备），不能改，
/// 所以需要这层映射才能让新后端打开"用户选中的那一台"。
///
/// 匹配按设备实例路径做，而不是按名字：同型号的两台摄像头名字完全一样
/// （"Iriun Webcam" 与 "Iriun Webcam #2" 只是显示名加了后缀，不可靠）。
///
/// 软件设备（<c>@device:sw:</c>，例如 OBS 虚拟摄像头）只在 DirectShow 里存在，
/// Media Foundation 根本枚举不到，所以这类一律判定为"配不上"并回退旧后端 ——
/// 这不是缺陷，是事实：那些设备本来就没有 MF 实现。
/// </summary>
internal static class MfDeviceMatcher
{
    /// <summary>DirectShow moniker 的前缀。pnp 是物理设备，sw 是软件设备。</summary>
    private const string PnpPrefix = "@device:pnp:";
    private const string SoftwarePrefix = "@device:sw:";

    /// <summary>
    /// 找到与这个 moniker 对应的 MF 设备。找不到返回 null，调用方应回退旧后端。
    /// </summary>
    internal static MfCaptureDevice? FindByMoniker(
        string? monikerString,
        IReadOnlyList<MfCaptureDevice> devices)
    {
        if (IsSoftwareOnlyDevice(monikerString))
        {
            RuntimeLog.Info(
                "Camera",
                "当前摄像头是 DirectShow 软件设备（如 OBS 虚拟摄像头），"
                    + "Media Foundation 无法枚举，使用 DirectShow 后端");
            return null;
        }

        string key = ExtractDeviceInstanceKey(monikerString);
        if (key.Length == 0 || devices.Count == 0)
            return null;

        foreach (MfCaptureDevice device in devices)
        {
            if (string.Equals(
                    ExtractDeviceInstanceKey(device.SymbolicLink),
                    key,
                    StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        RuntimeLog.Info(
            "Camera",
            $"Media Foundation 里找不到对应设备（key={key}，候选 {devices.Count} 台），回退旧采集后端");
        return null;
    }

    /// <summary>
    /// 是否是只存在于 DirectShow 的软件设备。这类设备（OBS 虚拟摄像头、
    /// 各种滤镜产生的虚拟源）没有 MF 实现，不必白白枚举一遍再失败。
    /// </summary>
    internal static bool IsSoftwareOnlyDevice(string? monikerString) =>
        (monikerString?.Trim() ?? "").StartsWith(SoftwarePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 从 moniker 或符号链接里取出可比较的设备实例键。
    ///
    /// 关键在于**必须去掉接口类 GUID**：两套 API 对同一台摄像头的同一个采集功能
    /// 用的是不同的接口类，DirectShow 报 KSCATEGORY_CAPTURE
    /// <c>{65e8773d-8f56-11d0-a3b9-00a0c9223196}</c>，
    /// Media Foundation 报 KSCATEGORY_VIDEO_CAMERA
    /// <c>{e5323777-f976-4f5b-9b55-b94699c46e44}</c>。
    /// 本机实测同一台设备的两个标识除这一段之外逐字符相同，所以把它算进键里
    /// 会让每一台物理摄像头都配不上，新后端永远退回旧路径 —— 之前正是这样，
    /// GPU 与 MF 全部代码都在，却一次都没真正跑起来。
    ///
    /// 接口 GUID 之后的 API 专属后缀要丢弃：DirectShow moniker 通常在 GUID 结束，
    /// MF 符号链接可能继续带上 <c>\\global</c> 等后缀。两侧都只枚举视频采集设备，
    /// 所以保留 GUID 之前的物理实例路径即可区分同型号的多台设备。
    /// </summary>
    internal static string ExtractDeviceInstanceKey(string? identifier)
    {
        string value = identifier?.Trim() ?? "";
        if (value.Length == 0)
            return "";

        if (value.StartsWith(PnpPrefix, StringComparison.OrdinalIgnoreCase))
            value = value[PnpPrefix.Length..];
        else if (value.StartsWith(SoftwarePrefix, StringComparison.OrdinalIgnoreCase))
            value = value[SoftwarePrefix.Length..];

        // 两套 API 对反斜杠的写法一致，但大小写和转义可能不同，统一小写比较。
        return StripInterfaceClass(value.Trim()).ToLowerInvariant();
    }

    /// <summary>
    /// 去掉标识里的接口类 GUID，保留其余部分。
    ///
    /// 标识形如 <c>\\?\root#devgen#{实例}#{接口类}\后缀</c>：接口类是最后一个
    /// <c>#</c> 之后、第一个 <c>\</c> 之前的那段。格式对不上时原样返回，
    /// 不去猜 —— 猜错会把两台不同的设备判成同一台，那比配不上更糟。
    /// </summary>
    private static string StripInterfaceClass(string value)
    {
        int lastSeparator = value.LastIndexOf('#');
        if (lastSeparator < 0 || lastSeparator == value.Length - 1)
            return value;

        string tail = value[(lastSeparator + 1)..];
        if (tail.Length == 0 || tail[0] != '{')
            return value;

        int guidEnd = tail.IndexOf('}');
        if (guidEnd < 0)
            return value;

        // 接口类 GUID 之后是 API 专属后缀（例如 "\global"），不参与跨 API 匹配。
        return value[..lastSeparator];
    }
}
