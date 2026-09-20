using System.Diagnostics;
using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 发布守卫：产物目录里已经生成了增量包（AppPatch），更新清单就必须带上它。
/// 现场踩过一次：打包后手工还原了"生成补丁包之前"的清单，patch_supported=false / patch_package=null，
/// 发布出去的清单不带补丁包，启动器永远拿不到增量更新，只能靠人工发现。
/// </summary>
public sealed class ReleaseUpdateManifestPatchGuardTests
{
    [Fact]
    public void ManifestWithoutPatchPackageIsRejectedWhenPatchZipExists()
    {
        string root = Path.Combine(Path.GetTempPath(), "pp-manifest-guard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string patchPath = Path.Combine(root, "PackingProof_AppPatch_v0.0.69.zip");
            File.WriteAllBytes(patchPath, [1, 2, 3]);
            string jsonPath = Path.Combine(root, "update_v0.0.69.json");

            File.WriteAllText(
                jsonPath,
                """
                {
                  "latest_version": "0.0.69",
                  "title": "v0.0.69 修复摄像头死循环与导出卡死",
                  "patch_supported": false,
                  "patch_package": null,
                  "notes": ["摄像头异常时不再卡死"]
                }
                """,
                new UTF8Encoding(false));

            (int failedExit, string failedOutput) = RunGuard(jsonPath, patchPath);

            Assert.NotEqual(0, failedExit);
            Assert.True(
                failedOutput.Contains("更新清单没有带上已生成的增量包", StringComparison.Ordinal),
                "输出里没有命中守卫提示：" + failedOutput);

            File.WriteAllText(
                jsonPath,
                """
                {
                  "latest_version": "0.0.69",
                  "title": "v0.0.69 修复摄像头死循环与导出卡死",
                  "patch_supported": true,
                  "patch_package": {
                    "type": "baseline_patch",
                    "url": "https://gitee.com/PackingProof/PackingProof-Desktop/releases/download/v0.0.69/PackingProof_AppPatch_v0.0.69.zip",
                    "github_url": "https://github.com/PackingProof/PackingProof-Desktop/releases/download/v0.0.69/PackingProof_AppPatch_v0.0.69.zip",
                    "gitee_url": "https://gitee.com/PackingProof/PackingProof-Desktop/releases/download/v0.0.69/PackingProof_AppPatch_v0.0.69.zip",
                    "sha256": "702129dbf0f3a7b09f0d950897dc3804b1475478f8f3f38d88bca55b45410e85",
                    "size": 2028290
                  },
                  "notes": ["摄像头异常时不再卡死"]
                }
                """,
                new UTF8Encoding(false));

            (int passedExit, string passedOutput) = RunGuard(jsonPath, patchPath);

            Assert.True(passedExit == 0, passedOutput);
            Assert.Contains("OK", passedOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (int ExitCode, string Output) RunGuard(string jsonPath, string patchPath)
    {
        string repositoryRoot = FindRepositoryRoot();
        string commonScript = Path.Combine(repositoryRoot, "Tools", "ReleaseNotes.Common.ps1");
        string script = string.Join(
            Environment.NewLine,
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8",
            "$OutputEncoding = [System.Text.Encoding]::UTF8",
            "$ErrorActionPreference = 'Stop'",
            $". '{Escape(commonScript)}'",
            "try {",
            $"    Assert-UpdateManifestReady -UpdateJsonPath '{Escape(jsonPath)}' -ExpectedTitle 'v0.0.69 修复摄像头死循环与导出卡死' -AppPatchPath '{Escape(patchPath)}'",
            "    Write-Output 'OK'",
            "} catch {",
            "    Write-Output $_.Exception.Message",
            "    exit 2",
            "}");

        // 仓库脚本按 UTF-8 无 BOM 存中文，Windows PowerShell 5.1 会把中文读成 ANSI 导致解析失败，
        // 所以这里和文档一致，统一用 pwsh 7 跑。
        string powershellPath = ResolvePwshPath();
        var startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
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
            ?? throw new InvalidOperationException("Unable to start Windows PowerShell");
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
