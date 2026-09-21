using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// Windows 专有的监听调优、权限查询与外壳 COM 调用。
///
/// 集中放在这里，跨平台代码就不必散落平台判断：非 Windows 宿主（例如 macOS 保存主机）
/// 走到这些入口时一律安全返回，不抛 DllNotFound 或 COM 异常。
/// </summary>
internal static class WindowsPlatformSupport
{
    /// <summary>HttpListener 的超时设置只有 Windows 支持；其它平台保持默认值。</summary>
    internal static void ConfigureHttpListenerTimeouts(
        HttpListener listener,
        TimeSpan headerWait,
        TimeSpan entityBody,
        TimeSpan idleConnection,
        TimeSpan drainEntityBody)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            listener.TimeoutManager.HeaderWait = headerWait;
            listener.TimeoutManager.EntityBody = entityBody;
            listener.TimeoutManager.IdleConnection = idleConnection;
            listener.TimeoutManager.DrainEntityBody = drainEntityBody;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("WebServer", $"Unable to configure HTTP request timeouts: {ex.Message}");
        }
    }

    /// <summary>取当前进程用户的 SID，用于 Windows 的 URL ACL 授权。</summary>
    internal static string GetCurrentUserSidOrThrow()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("当前平台不需要配置 Windows 服务访问权限");

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string? userSid = identity.User?.Value;
        if (string.IsNullOrWhiteSpace(userSid))
            throw new InvalidOperationException("无法获取当前用户 SID，不能配置局域网服务监听权限");

        return userSid;
    }

    /// <summary>当前进程是否以管理员身份运行；非 Windows 一律返回 false。</summary>
    internal static bool IsCurrentProcessElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>创建 Windows 外壳 COM 对象（例如防火墙策略）；非 Windows 返回 false。</summary>
    internal static bool TryCreateComInstance(string progId, out object? instance)
    {
        instance = null;
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            Type? type = Type.GetTypeFromProgID(progId);
            if (type == null) return false;
            instance = Activator.CreateInstance(type);
            return instance != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>释放 COM 对象；非 Windows 或释放失败都不影响调用方。</summary>
    internal static void ReleaseComObject(object? instance)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            if (instance != null && Marshal.IsComObject(instance))
                Marshal.FinalReleaseComObject(instance);
        }
        catch
        {
            // 释放失败不影响调用方，进程退出时会回收。
        }
    }
}
