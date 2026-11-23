namespace ExpressPackingMonitoring.UpdateCore;

/// <summary>
/// 增量包载荷能不能落盘的判定（安装那一刻的纵深防御）。
///
/// 打包侧已经有一份同样的白名单：`Tools\AppPatchRuntimeCompatibility.Common.ps1` 的
/// <c>Test-IsAppPatchManagedRuntimePath</c> 与 `Tools\Publish-CleanPackage.ps1` 里
/// `New-AppPatchPackage` 的清单守卫——用户可替换的 ffmpeg / libvlc 不得覆盖或删除；
/// 唯一例外是 0.0.100 基线缺失的内嵌窗口 drawable 插件可新增。主程序核心文件与运行时种子
/// 绝不进清理列表。这里再在安装前校验一次，因为落盘的清单来自网络、也可能被第三方改过。
///
/// 两侧必须同步修改：放宽/收紧白名单时，C# 这份与打包脚本那份要一起改，否则会出现"打包侧拒绝、
/// 安装侧接受"或反过来"安装侧拒绝、已经发出去的补丁装不上"的错位。
/// </summary>
public static class AppPatchPayloadPolicy
{
    /// <summary>
    /// 0.0.100 基线遗漏了 LibVLC 的内嵌窗口输出插件。该文件只能由补丁新增，
    /// 不允许覆盖用户已有文件，更不能进入删除列表。
    /// </summary>
    public const string LibVlcDrawablePluginRelativePath =
        @"libvlc\win-x64\plugins\video_output\libdrawable_plugin.dll";

    /// <summary>用户可替换、补丁一律不得写入或删除的相对路径（相对 app\ 的落点）。</summary>
    public static bool IsUserManagedRuntimePath(string? relativePath)
    {
        string normalized = Normalize(relativePath);
        return string.Equals(normalized, @"tools\ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, @"libvlc", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(@"libvlc\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 主程序核心文件：补丁必须能写（升级就是换它），但不能出现在清理列表里——删掉就没有程序了。
    /// </summary>
    public static bool IsCoreProgramFile(string? relativePath)
    {
        string normalized = Normalize(relativePath);
        return string.Equals(normalized, "ExpressPackingMonitoring.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "ExpressPackingMonitoring.dll", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "ExpressPackingMonitoring.deps.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "ExpressPackingMonitoring.runtimeconfig.json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>运行时种子：共享布局唯一的落成来源，删掉就再也落不回来。</summary>
    public static bool IsRuntimeSeedPath(string? relativePath)
    {
        string normalized = Normalize(relativePath);
        return string.Equals(normalized, RuntimeLayoutSeeder.SeedDirectoryName, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(RuntimeLayoutSeeder.SeedDirectoryName + @"\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 这条载荷能不能落盘。<paramref name="isDelete"/> 为 true 时按清理列表的规则判定（比写入更严）。
    /// 返回 null 表示允许，否则返回写进日志/异常里的中文理由。
    /// </summary>
    public static string? DescribeViolation(
        string? relativePath,
        bool isDelete,
        string? root = "app")
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return "Patch 条目缺少相对路径";
        string normalizedRoot = Normalize(root);
        if (string.IsNullOrWhiteSpace(normalizedRoot))
            normalizedRoot = "app";
        bool approvedVlcAddition = !isDelete
            && string.Equals(normalizedRoot, "app", StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                Normalize(relativePath),
                LibVlcDrawablePluginRelativePath,
                StringComparison.OrdinalIgnoreCase);
        if (IsUserManagedRuntimePath(relativePath) && !approvedVlcAddition)
            return $"用户可替换的运行时文件不能由增量包改动：{relativePath}";
        if (!isDelete) return null;
        if (IsCoreProgramFile(relativePath))
            return $"清理列表不得包含主程序核心文件：{relativePath}";
        if (IsRuntimeSeedPath(relativePath))
            return $"清理列表不得包含运行时种子：{relativePath}";
        return null;
    }

    /// <summary>统一成"反斜杠分隔、去掉首尾分隔符"的形式再比较，避免 / 与 \ 混写绕过判定。</summary>
    private static string Normalize(string? relativePath) =>
        (relativePath ?? "").Trim().Replace('/', '\\').Trim('\\').Trim();
}
