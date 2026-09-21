using System.Diagnostics;

namespace ExpressPackingMonitoring.Host;

/// <summary>
/// macOS 原生对话框。用途选择、目录选择与设备接入确认都走系统对话框：
/// Mac 端不为了几个问题再造一套界面，文案沿用桌面端"选择电脑用途"的说法。
/// 非 macOS 平台一律返回"没有答案"，由调用方按失败处理，不静默放行。
/// </summary>
internal static class MacDialog
{
    internal static bool IsSupported => OperatingSystem.IsMacOS();

    /// <summary>问用途：返回 "host"、"viewer"，取消返回 null。</summary>
    internal static string? ChoosePurpose()
    {
        string? answer = Run(
            "set messageText to \"这台电脑要负责长期保存录像吗？\" & return & return & "
                + "\"主机就是负责长期保存录像的电脑，可接收手机和其他电脑的录像；不保存则只连接主机查看录像。\"",
            "set answer to button returned of (display dialog messageText buttons {\"取消\", \"不要，只连接主机查看\", \"要，作为保存主机\"} default button \"要，作为保存主机\" with title \"选择这台电脑的用途\" with icon note)",
            "return answer");

        return answer switch
        {
            "要，作为保存主机" => "host",
            "不要，只连接主机查看" => "viewer",
            _ => null
        };
    }

    /// <summary>选择录像保存位置，取消返回 null。</summary>
    /// <summary>手动打开程序时问是否切换用途；返回 true 表示要切换。</summary>
    internal static bool AskSwitchPurpose(string currentPurposeName)
    {
        string? answer = Run(
            $"set messageText to \"当前用途：{Escape(currentPurposeName)}\"",
            "set answer to button returned of (display dialog messageText buttons {\"继续使用\", \"切换用途\"} default button \"继续使用\" with title \"PackingProof\" with icon note)",
            "return answer");
        return answer == "切换用途";
    }

    internal static string? ChooseFolder(string prompt, string? defaultPath)
    {
        bool hasDefault = !string.IsNullOrWhiteSpace(defaultPath) && Directory.Exists(defaultPath);
        string firstLine = hasDefault
            ? $"set chosenFolder to choose folder with prompt \"{Escape(prompt)}\" default location (POSIX file \"{Escape(defaultPath!)}\" as alias)"
            : $"set chosenFolder to choose folder with prompt \"{Escape(prompt)}\"";
        string? path = Run(firstLine, "return POSIX path of chosenFolder");
        return string.IsNullOrWhiteSpace(path) ? null : path.Trim();
    }

    /// <summary>多个主机时让用户选一个；返回下标，取消返回 null。</summary>
    internal static int? ChooseHost(IReadOnlyList<string> hostLabels)
    {
        if (hostLabels.Count == 0) return null;
        string list = string.Join(", ", hostLabels.Select(label => $"\"{Escape(label)}\""));
        string? chosen = Run(
            $"set options to {{{list}}}",
            "set chosen to choose from list options with prompt \"选择要连接的保存主机\" with title \"PackingProof 查看端\"",
            "if chosen is false then",
            "return \"\"",
            "end if",
            "return item 1 of chosen");
        if (string.IsNullOrWhiteSpace(chosen)) return null;

        for (int index = 0; index < hostLabels.Count; index++)
        {
            if (string.Equals(hostLabels[index], chosen, StringComparison.Ordinal)) return index;
        }

        return null;
    }

    /// <summary>设备申请接入时的确认；无法弹窗时按拒绝处理，避免任何人默认可接入。</summary>
    internal static bool AskDeviceApproval(string deviceName, string deviceKind)
    {
        string kindText = deviceKind switch
        {
            "pc" => "电脑",
            "viewer" => "查看端",
            _ => "手机"
        };
        string name = string.IsNullOrWhiteSpace(deviceName) ? "未命名设备" : deviceName.Trim();
        string? answer = Run(
            $"set messageText to \"{Escape($"设备「{name}」（{kindText}）请求连接本机保存录像，是否允许？")}\"",
            "set answer to button returned of (display dialog messageText buttons {\"不允许\", \"允许\"} default button \"允许\" with title \"PackingProof 保存主机\" with icon caution)",
            "return answer");
        return answer == "允许";
    }

    internal static void ShowMessage(string text, string title = "PackingProof")
    {
        Run(
            $"set messageText to \"{Escape(text)}\"",
            $"display dialog messageText buttons {{\"好\"}} default button \"好\" with title \"{Escape(title)}\"");
    }

    /// <summary>每条语句作为独立的 -e 参数交给 osascript，避免shell 与转义互相牵连。</summary>
    private static string? Run(params string[] statements)
    {
        if (!IsSupported || !HostOptions.DialogsEnabled) return null;

        try
        {
            var startInfo = new ProcessStartInfo("/usr/bin/osascript")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string statement in statements)
            {
                startInfo.ArgumentList.Add("-e");
                startInfo.ArgumentList.Add(statement);
            }

            using var process = Process.Start(startInfo);
            if (process == null) return null;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(120_000);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r\n", "\" & return & \"")
            .Replace("\n", "\" & return & \"");
}
