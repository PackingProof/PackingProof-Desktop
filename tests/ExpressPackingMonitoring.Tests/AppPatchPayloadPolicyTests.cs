using System.Diagnostics;
using System.Text;
using ExpressPackingMonitoring.UpdateCore;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 增量包载荷白名单（安装侧的纵深防御）：用户可替换的 ffmpeg / libvlc 不能删，
/// 普通 LibVLC 文件也不能覆盖；唯一例外是 0.0.100 基线缺失的 drawable 插件可安全新增。
/// </summary>
public sealed class AppPatchPayloadPolicyTests
{
    [Theory]
    [InlineData("tools\\ffmpeg.exe")]
    [InlineData("TOOLS/FFMPEG.EXE")]
    [InlineData("libvlc\\win-x64\\libvlc.dll")]
    [InlineData("libvlc/win-x64/plugins/codec/avcodec_plugin.dll")]
    [InlineData("libvlc")]
    public void Write_RejectsUserManagedRuntimeFiles(string relativePath)
    {
        Assert.NotNull(AppPatchPayloadPolicy.DescribeViolation(relativePath, isDelete: false));
        Assert.NotNull(AppPatchPayloadPolicy.DescribeViolation(relativePath, isDelete: true));
    }

    [Fact]
    public void MissingDrawablePlugin_MayBeAddedButNeverDeleted()
    {
        string relativePath = AppPatchPayloadPolicy.LibVlcDrawablePluginRelativePath;

        Assert.Null(AppPatchPayloadPolicy.DescribeViolation(relativePath, isDelete: false));
        Assert.NotNull(AppPatchPayloadPolicy.DescribeViolation(
            relativePath,
            isDelete: false,
            root: "runtime"));
        Assert.NotNull(AppPatchPayloadPolicy.DescribeViolation(relativePath, isDelete: true));
    }

    [Theory]
    [InlineData("ExpressPackingMonitoring.exe")]
    [InlineData("ExpressPackingMonitoring.dll")]
    [InlineData("ExpressPackingMonitoring.deps.json")]
    [InlineData("ExpressPackingMonitoring.runtimeconfig.json")]
    public void Delete_RejectsCoreProgramFilesButWritingThemIsTheWholePointOfAPatch(string relativePath)
    {
        // 升级就是换主程序：写它必须放行。
        Assert.Null(AppPatchPayloadPolicy.DescribeViolation(relativePath, isDelete: false));
        Assert.NotNull(AppPatchPayloadPolicy.DescribeViolation(relativePath, isDelete: true));
    }

    [Theory]
    [InlineData("runtime-seed\\layout.json")]
    [InlineData("runtime-seed/shared/Microsoft.NETCore.App/10.0.13/.version")]
    public void Delete_RejectsTheRuntimeSeed(string relativePath)
    {
        Assert.Null(AppPatchPayloadPolicy.DescribeViolation(relativePath, isDelete: false));
        Assert.NotNull(AppPatchPayloadPolicy.DescribeViolation(relativePath, isDelete: true));
    }

    [Theory]
    [InlineData("ExpressPackingMonitoring.Core.dll")]
    [InlineData("Web/index.html")]
    [InlineData("mscordaccore_amd64_amd64_8.0.2826.26413.dll")]
    [InlineData("runtime\\shared\\Microsoft.NETCore.App\\10.0.13\\System.Private.CoreLib.dll")]
    public void NormalPayloadsStayAllowed(string relativePath)
    {
        Assert.Null(AppPatchPayloadPolicy.DescribeViolation(relativePath, isDelete: false));
        Assert.Null(AppPatchPayloadPolicy.DescribeViolation(relativePath, isDelete: true));
    }

    [Fact]
    public void EmptyPathIsRejected()
    {
        Assert.NotNull(AppPatchPayloadPolicy.DescribeViolation("", isDelete: false));
        Assert.NotNull(AppPatchPayloadPolicy.DescribeViolation("   ", isDelete: true));
    }

    [Fact]
    public void Installers_RejectOverwritingAnExistingDifferentDrawablePlugin()
    {
        string root = FindRepositoryRoot();
        string manualInstaller = File.ReadAllText(Path.Combine(root, "tools", "Apply-AppPatch.ps1"));
        string launcher = File.ReadAllText(Path.Combine(
            root, "src", "ExpressPackingMonitoring.Launcher", "Program.cs"));

        Assert.Contains(
            "目标电脑已有不同的 libdrawable_plugin.dll",
            manualInstaller,
            StringComparison.Ordinal);
        Assert.Contains(
            "目标电脑已有不同的 libdrawable_plugin.dll",
            launcher,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 打包脚本（tools\Apply-AppPatch.ps1 里的 Get-AppPatchPayloadViolation）与 C# 这份是两套实现，
    /// 必须给出同样的结论：任一侧单独改宽/改严，都会出现"打包侧拒绝、安装侧接受"或者反过来
    /// "安装侧拒绝、已经发出去的补丁装不上"。这里直接用同一批样本跑一遍两侧。
    /// </summary>
    [Fact]
    public void ManualInstallerGuard_MatchesTheCSharpPolicy()
    {
        string[] samples =
        [
            "tools\\ffmpeg.exe",
            "TOOLS/FFMPEG.EXE",
            "libvlc\\win-x64\\libvlc.dll",
            "libvlc/win-x64/plugins/codec/avcodec_plugin.dll",
            "libvlc",
            "libvlc/win-x64/plugins/video_output/libdrawable_plugin.dll",
            "ExpressPackingMonitoring.exe",
            "ExpressPackingMonitoring.dll",
            "ExpressPackingMonitoring.deps.json",
            "ExpressPackingMonitoring.runtimeconfig.json",
            "runtime-seed\\layout.json",
            "runtime-seed/shared/Microsoft.NETCore.App/10.0.13/.version",
            "ExpressPackingMonitoring.Core.dll",
            "Web/index.html",
            "runtime\\shared\\Microsoft.NETCore.App\\10.0.13\\System.Private.CoreLib.dll",
        ];

        string script = Path.Combine(FindRepositoryRoot(), "tools", "Apply-AppPatch.ps1");
        string command = new StringBuilder()
            .AppendLine("$ErrorActionPreference = 'Stop'")
            .AppendLine("$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)")
            .AppendLine($"$text = [System.IO.File]::ReadAllText('{script.Replace("'", "''")}', [System.Text.Encoding]::UTF8)")
            .AppendLine("$tokens = $null")
            .AppendLine("$errors = $null")
            .AppendLine("$ast = [System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$errors)")
            .AppendLine("$function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-AppPatchPayloadViolation' }, $true)")
            .AppendLine("if ($null -eq $function) { throw '脚本里找不到 Get-AppPatchPayloadViolation' }")
            .AppendLine(". ([scriptblock]::Create($function.Extent.Text))")
            .AppendLine(". ([scriptblock]::Create(($ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-IsNullOrWhiteSpace' }, $true)).Extent.Text))")
            .AppendLine("$samples = @(")
            .AppendLine(string.Join("," + Environment.NewLine, samples.Select(Quote)))
            .AppendLine(")")
            .AppendLine("foreach ($sample in $samples) {")
            .AppendLine("    $write = Get-AppPatchPayloadViolation -RelativePath $sample")
            .AppendLine("    $delete = Get-AppPatchPayloadViolation -RelativePath $sample -IsDelete")
            .AppendLine("    $writeFlag = if (Test-IsNullOrWhiteSpace $write) { 'ok' } else { 'blocked' }")
            .AppendLine("    $deleteFlag = if (Test-IsNullOrWhiteSpace $delete) { 'ok' } else { 'blocked' }")
            // 只能用双引号：单引号字符串里 `t 不是转义，输出会带上字面反引号。
            .AppendLine("    \"{0}`t{1}`t{2}\" -f $sample, $writeFlag, $deleteFlag")
            .AppendLine("}")
            .AppendLine("$runtimeViolation = Get-AppPatchPayloadViolation -RelativePath 'libvlc/win-x64/plugins/video_output/libdrawable_plugin.dll' -Root 'runtime'")
            .AppendLine("if (Test-IsNullOrWhiteSpace $runtimeViolation) { exit 2 }")
            .ToString();

        ProcessResult result = RunPowerShell(command);
        Assert.True(result.ExitCode == 0, result.Output + result.Error);
        Dictionary<string, (string Write, string Delete)> actual = ParseRows(result.Output);
        Assert.Equal(samples.Length, actual.Count);

        foreach (string sample in samples)
        {
            Assert.True(actual.ContainsKey(sample), $"脚本没有给出样本结论：{sample}");
            Assert.Equal(
                AppPatchPayloadPolicy.DescribeViolation(sample, isDelete: false) is null ? "ok" : "blocked",
                actual[sample].Write);
            Assert.Equal(
                AppPatchPayloadPolicy.DescribeViolation(sample, isDelete: true) is null ? "ok" : "blocked",
                actual[sample].Delete);
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static Dictionary<string, (string Write, string Delete)> ParseRows(string output)
    {
        var rows = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Trim().Split('\t');
            if (parts.Length != 3) continue;
            rows[parts[0]] = (parts[1], parts[2]);
        }
        return rows;
    }

    private static ProcessResult RunPowerShell(string command)
    {
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var startInfo = new ProcessStartInfo("pwsh.exe")
        {
            WorkingDirectory = FindRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encoded);
        using Process process = Process.Start(startInfo)!;
        // 两个流必须同时异步读：PowerShell 把 Write-Host 的信息流序列化成 CLIXML 写到 stderr，
        // 顺序读会写满缓冲区互相等死。
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, outputTask.Result, errorTask.Result);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
