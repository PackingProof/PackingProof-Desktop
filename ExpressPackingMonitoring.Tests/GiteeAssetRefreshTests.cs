using System.Diagnostics;
using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// Gitee 不支持覆盖上传同名附件：重发同一版本时若不先删旧文件，Release 上会留下两份同名资产，
/// 用户下到哪一份全看运气。这里守住"上传前按名字清理，Release 还不存在时不算错误，
/// 但真正的失败（例如令牌失效）必须原样抛出，不能悄悄跳过清理"。
/// </summary>
public sealed class GiteeAssetRefreshTests
{
    [Fact]
    public void SharedCleanup_RemovesEveryRequestedAttachmentName()
    {
        (int exitCode, string output) = RunScript(string.Join(
            Environment.NewLine,
            Preamble(),
            "function Test-GiteeReleaseExists { param($Repository, $Tag) return $true }",
            "function Get-GiteeReleaseId { param($Repository, $Tag) return 42 }",
            "$script:removed = New-Object System.Collections.Generic.List[string]",
            "function Remove-GiteeReleaseAttachmentByName {",
            "    param($Repository, $ReleaseId, $FileName)",
            "    $script:removed.Add(\"$ReleaseId/$FileName\")",
            "    return 1",
            "}",
            "Remove-GiteeReleaseAttachmentsForTag -Repository 'org/repo' -Tag 'v9.9.9' -FileNames @('A.exe', 'B.zip')",
            "Write-Output ('REMOVED::' + ($script:removed -join ','))"));

        Assert.True(exitCode == 0, output);
        Assert.Contains("REMOVED::42/A.exe,42/B.zip", output, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedCleanup_SkipsWhenReleaseDoesNotExistYet()
    {
        (int exitCode, string output) = RunScript(string.Join(
            Environment.NewLine,
            Preamble(),
            "function Test-GiteeReleaseExists { param($Repository, $Tag) return $false }",
            "function Get-GiteeReleaseId { param($Repository, $Tag) throw 'Release 不存在时不应该去取 id' }",
            "$script:called = $false",
            "function Remove-GiteeReleaseAttachmentByName { param($Repository, $ReleaseId, $FileName) $script:called = $true; return 0 }",
            "Remove-GiteeReleaseAttachmentsForTag -Repository 'org/repo' -Tag 'v9.9.9' -FileNames @('A.exe')",
            "Write-Output ('CALLED::' + $script:called)"));

        Assert.True(exitCode == 0, output);
        Assert.Contains("还不存在", output, StringComparison.Ordinal);
        Assert.Contains("CALLED::False", output, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedCleanup_DoesNotSwallowRealFailures()
    {
        (int exitCode, string output) = RunScript(string.Join(
            Environment.NewLine,
            Preamble(),
            "function Test-GiteeReleaseExists { param($Repository, $Tag) throw 'HTTP 401' }",
            "function Remove-GiteeReleaseAttachmentByName { param($Repository, $ReleaseId, $FileName) return 0 }",
            "try {",
            "    Remove-GiteeReleaseAttachmentsForTag -Repository 'org/repo' -Tag 'v9.9.9' -FileNames @('A.exe')",
            "} catch {",
            "    Write-Output ('FAILED::' + $_.Exception.Message)",
            "    exit 0",
            "}",
            "Write-Output 'FAILED::none'"));

        Assert.True(exitCode == 0, output);
        Assert.Contains("FAILED::HTTP 401", output, StringComparison.Ordinal);
    }

    [Fact]
    public void NoRuntimePackage_CleansGiteeAttachmentsBeforeUpload()
    {
        string repositoryRoot = FindRepositoryRoot();
        string noRuntime = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Publish-NoRuntimePackage.ps1"),
            Encoding.UTF8);
        string giteeAuth = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "GiteeAuth.Common.ps1"),
            Encoding.UTF8);

        Assert.Contains("function Remove-GiteeReleaseAttachmentsForTag", giteeAuth, StringComparison.Ordinal);
        Assert.Contains("function Test-GiteeReleaseExists", giteeAuth, StringComparison.Ordinal);
        Assert.Contains("-SkipHttpErrorCheck", giteeAuth, StringComparison.Ordinal);
        Assert.Contains("$statusCode -eq 404", giteeAuth, StringComparison.Ordinal);

        int cleanupIndex = noRuntime.IndexOf("Remove-GiteeReleaseAttachmentsForTag", StringComparison.Ordinal);
        int uploadIndex = noRuntime.IndexOf(
            "& gitee release upload --repo $GiteeRepository $releaseTag @uploadFiles",
            StringComparison.Ordinal);

        Assert.True(cleanupIndex >= 0, "Publish-NoRuntimePackage.ps1 必须在上传前清理 Gitee 同名附件");
        Assert.True(uploadIndex >= 0, "找不到 no-runtime 的 Gitee 上传调用");
        Assert.True(cleanupIndex < uploadIndex, "清理必须发生在上传之前");
    }

    private static string Preamble()
    {
        string repositoryRoot = FindRepositoryRoot();
        string commonScript = Path.Combine(repositoryRoot, "Tools", "GiteeAuth.Common.ps1");
        return string.Join(
            Environment.NewLine,
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8",
            "$OutputEncoding = [System.Text.Encoding]::UTF8",
            "$ErrorActionPreference = 'Stop'",
            $". '{Escape(commonScript)}'");
    }

    private static (int ExitCode, string Output) RunScript(string script)
    {
        // 仓库脚本按 UTF-8 无 BOM 存中文，统一用 pwsh 7 跑，避免 PowerShell 5.1 按 ANSI 解析中文。
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolvePwshPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script })
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start pwsh 7");
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string ResolvePwshPath()
    {
        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        foreach (string directory in (pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(directory.Trim().Trim('"'), "pwsh.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // PATH 中的非法路径不影响其它候选
            }
        }

        throw new FileNotFoundException("pwsh 7 was not found on PATH; script tests require it.");
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

        throw new DirectoryNotFoundException("ExpressPackingMonitoring repository root was not found.");
    }
}
