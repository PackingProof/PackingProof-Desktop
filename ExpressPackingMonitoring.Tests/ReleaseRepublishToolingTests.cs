using System.Diagnostics;
using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// "重发同一版本"一键化守卫：重发会覆盖两个平台上已发布的资产，属对外动作，
/// 必须先看计划、再加 -Confirmed；并且只允许 HEAD 比标签多出工具/文档提交，
/// 应用侧代码与标签不一致时要直接拒绝，避免发出去的包不是标签内容。
/// </summary>
public sealed class ReleaseRepublishToolingTests
{
    [Fact]
    public void DryRun_PrintsPlanWithoutBuildingOrUploading()
    {
        using TempReleaseRepo repo = TempReleaseRepo.Create();

        (int exitCode, string output) = repo.RunRepublish("-DryRun");

        Assert.True(exitCode == 0, output);
        Assert.Contains("重发同一版本：v9.9.9", output, StringComparison.Ordinal);
        Assert.Contains("Publish-CleanPackage.ps1", output, StringComparison.Ordinal);
        Assert.Contains("Publish-Releases.ps1", output, StringComparison.Ordinal);
        Assert.Contains("Publish-NoRuntimePackage.ps1", output, StringComparison.Ordinal);
        Assert.Contains("仅查看计划：未构建、未上传", output, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutConfirmed_RefusesToRepublish()
    {
        using TempReleaseRepo repo = TempReleaseRepo.Create();

        (int exitCode, string output) = repo.RunRepublish("-Title republish-check");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("对外动作", output, StringComparison.Ordinal);
        Assert.Contains("-Confirmed", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AppCodeAheadOfTag_IsRejected()
    {
        using TempReleaseRepo repo = TempReleaseRepo.Create();
        repo.CommitFile(@"ExpressPackingMonitoring\Services\Foo.cs", "// app code change", "touch app code");

        (int exitCode, string output) = repo.RunRepublish("-Title republish-check -Confirmed");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("应用侧代码有差异", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolOnlyCommitsAheadOfTag_AreAllowed()
    {
        using TempReleaseRepo repo = TempReleaseRepo.Create();
        repo.CommitFile(@"Tools\ReleaseVersion.Common.ps1", "# tool change", "touch tooling", append: true);

        (int exitCode, string output) = repo.RunRepublish("-DryRun");

        Assert.True(exitCode == 0, output);
        Assert.Contains("仅工具/文档差异", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseDocument_DescribesTheOneCommandRepublishFlow()
    {
        string repositoryRoot = FindRepositoryRoot();
        string document = File.ReadAllText(
            Path.Combine(repositoryRoot, "docs", "development", "RELEASE_AND_RUNTIME.md"),
            Encoding.UTF8);

        Assert.Contains("Tools\\Republish-SameVersion.ps1", document, StringComparison.Ordinal);
        Assert.Contains("-ConfirmCommitCoverage -Confirmed", document, StringComparison.Ordinal);
        Assert.Contains("package\\.release-notes\\", document, StringComparison.Ordinal);
    }

    private sealed class TempReleaseRepo : IDisposable
    {
        private TempReleaseRepo(string root)
        {
            Root = root;
        }

        public string Root { get; }

        public static TempReleaseRepo Create()
        {
            string repositoryRoot = FindRepositoryRoot();
            string root = Path.Combine(Path.GetTempPath(), "pp-republish", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Tools"));
            Directory.CreateDirectory(Path.Combine(root, "ExpressPackingMonitoring"));

            foreach (string scriptName in new[]
                     {
                         "Republish-SameVersion.ps1",
                         "ReleaseVersion.Common.ps1",
                         "ReleaseNotes.Common.ps1"
                     })
            {
                File.Copy(
                    Path.Combine(repositoryRoot, "Tools", scriptName),
                    Path.Combine(root, "Tools", scriptName));
            }

            File.WriteAllText(
                Path.Combine(root, "ExpressPackingMonitoring", "ExpressPackingMonitoring.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Version>9.9.9</Version></PropertyGroup></Project>",
                new UTF8Encoding(false));

            var repo = new TempReleaseRepo(root);
            Assert.True(repo.RunGit("init", "-q").ExitCode == 0);
            Assert.True(repo.RunGit("add", "-A").ExitCode == 0);
            Assert.True(repo.RunGit("-c", "user.name=test", "-c", "user.email=test@example.com", "commit", "-q", "-m", "baseline").ExitCode == 0);
            Assert.True(repo.RunGit("tag", "v9.9.9").ExitCode == 0);
            return repo;
        }

        public void CommitFile(string relativePath, string content, string message, bool append = false)
        {
            string fullPath = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            string text = append && File.Exists(fullPath)
                ? File.ReadAllText(fullPath, Encoding.UTF8) + Environment.NewLine + content
                : content;
            File.WriteAllText(fullPath, text, new UTF8Encoding(false));

            Assert.True(RunGit("add", "-A").ExitCode == 0);
            Assert.True(RunGit("-c", "user.name=test", "-c", "user.email=test@example.com", "commit", "-q", "-m", message).ExitCode == 0);
        }

        public (int ExitCode, string Output) RunRepublish(string arguments)
        {
            string scriptPath = Path.Combine(Root, "Tools", "Republish-SameVersion.ps1");
            // 子进程默认按本地代码页写 stdout，中文会变成乱码：先固定控制台编码再调用脚本。
            // 脚本保持正式形态（不为了测试改编码），这里用 -Command 包一层即可。
            string command = string.Join(
                Environment.NewLine,
                "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8",
                "$OutputEncoding = [System.Text.Encoding]::UTF8",
                $"& '{scriptPath.Replace("'", "''", StringComparison.Ordinal)}' {arguments}");

            var startInfo = new ProcessStartInfo
            {
                FileName = ResolvePwshPath(),
                WorkingDirectory = Root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command })
            {
                startInfo.ArgumentList.Add(argument);
            }

            return Run(startInfo);
        }

        private (int ExitCode, string Output) RunGit(params string[] arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = Root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);
            return Run(startInfo);
        }

        private static (int ExitCode, string Output) Run(ProcessStartInfo startInfo)
        {
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Unable to start {startInfo.FileName}");
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响断言结果
            }
            catch (UnauthorizedAccessException)
            {
                // 同上：.git 里的只读文件偶尔会挡住清理
            }
        }
    }

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
