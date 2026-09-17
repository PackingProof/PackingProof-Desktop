namespace ExpressPackingMonitoring.Services.MediaFoundation;

/// <summary>摄像头采集后端。</summary>
internal enum CameraBackendKind
{
    /// <summary>Media Foundation：直接拿原生 YUY2/NV12，自己按正确矩阵转 BGR。</summary>
    MediaFoundation,

    /// <summary>AForge/DirectShow：系统按 BT.601 转成 RGB24 再交给我们。</summary>
    DirectShow,
}

/// <summary>
/// 决定用哪个采集后端。
///
/// 核心约束：**绝不能因为后端问题录不了像**。所以
/// - 新后端只在探测到真实首帧后才启用（协商成功不算，虚拟摄像头会协商成功却不出帧）
/// - 任何失败都回退 DirectShow，包括用户强制指定新后端的情况
/// - 用户可以强制 DirectShow，这是新后端在某台设备上表现异常时的出口
/// </summary>
internal static class CameraBackendPolicy
{
    internal const string ModeAuto = "auto";
    internal const string ModeMediaFoundation = "mediafoundation";
    internal const string ModeDirectShow = "directshow";

    /// <summary>把配置里的写法收敛成已知模式，未知值一律按 auto 处理。</summary>
    internal static string Normalize(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return ModeAuto;

        return mode.Trim().ToLowerInvariant() switch
        {
            ModeMediaFoundation or "mf" => ModeMediaFoundation,
            ModeDirectShow or "aforge" or "ds" => ModeDirectShow,
            _ => ModeAuto,
        };
    }

    /// <summary>用户是否明确禁用了新后端。这种情况下连探测都不做，省掉两次开设备。</summary>
    internal static bool IsMediaFoundationDisabled(string? mode) =>
        Normalize(mode) == ModeDirectShow;

    /// <summary>
    /// 根据探测结果决定后端。
    ///
    /// <paramref name="probeUsable"/> 必须来自"真的收到过一帧"的探测，
    /// 不能只看格式协商是否成功。
    /// </summary>
    internal static CameraBackendKind Decide(string? mode, bool probeUsable)
    {
        if (IsMediaFoundationDisabled(mode))
            return CameraBackendKind.DirectShow;

        return probeUsable
            ? CameraBackendKind.MediaFoundation
            : CameraBackendKind.DirectShow;
    }
}
