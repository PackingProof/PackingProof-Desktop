using ExpressPackingMonitoring;

namespace ExpressPackingMonitoring.Host;

/// <summary>
/// 主机进程的运行开关。常驻服务（由 launchd 拉起）不该弹窗或打开浏览器，
/// 自动化验证同样需要这两种行为，因此做成命令行开关而不是环境变量。
/// </summary>
internal static class HostOptions
{
    internal static bool DialogsEnabled { get; private set; } = true;
    internal static bool BrowserEnabled { get; private set; } = true;

    /// <summary>作为常驻服务运行：设置类问题只记日志，不弹窗打断登录。</summary>
    internal static bool ServiceMode { get; private set; }

    internal static void Apply(IReadOnlyList<string> arguments)
    {
        if (arguments.Any(argument => string.Equals(argument, "--no-dialog", StringComparison.OrdinalIgnoreCase)))
            DialogsEnabled = false;
        if (arguments.Any(argument => string.Equals(argument, "--no-browser", StringComparison.OrdinalIgnoreCase)))
            BrowserEnabled = false;
        if (arguments.Any(argument => string.Equals(argument, "--service", StringComparison.OrdinalIgnoreCase)))
            ServiceMode = true;
    }

    /// <summary>服务模式只写日志；交互运行时才弹窗告诉用户。</summary>
    internal static void ReportProblem(string message, string title = "PackingProof 保存主机")
    {
        Console.Error.WriteLine(message);
        if (DialogsEnabled && !ServiceMode) MacDialog.ShowMessage(message, title);
    }

    /// <summary>打开网页；关闭时只打印地址，便于常驻与排查。</summary>
    internal static void OpenUrl(string url)
    {
        if (!BrowserEnabled)
        {
            Console.WriteLine($"（未自动打开浏览器）{url}");
            return;
        }

        if (!WorkstationNetwork.TryOpenUrl(url, out string error))
            Console.WriteLine($"打开网页失败：{error}");
    }
}
