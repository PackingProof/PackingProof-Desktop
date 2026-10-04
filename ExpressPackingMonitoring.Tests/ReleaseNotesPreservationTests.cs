using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 发布笔记保留守卫：产物目录名是"提交级"的（标签外多提交一次、甚至工作区变脏，
/// 目录名就多出 -2-g&lt;sha&gt; / -dirty 后缀），只按"本次产物目录"找笔记，
/// 重打时会误判成"还没写"并换成模板骨架 —— v0.0.74 现场就这样把 5736 字节的
/// 人工笔记换成了 4358 字节模板。这里守住"换目录名也能找回人工笔记"。
/// </summary>
public sealed class ReleaseNotesPreservationTests
{
    private const string Version = "9.9.9";
    private const string NotesFileName = "RELEASE_NOTES_v9.9.9.md";

    private const string HumanNotes = """
        # PackingProof v9.9.9

        ## 更新内容

        ### 功能与体验

        - 人工写的发布笔记，重打时不能丢

        ### 问题修复

        - 无

        ### 兼容与工程

        - 无

        ## 下载与更新说明

        - 安装向导：PackingProof_Setup_v9.9.9.exe

        ## 未验证事项

        - 无
        """;

    [Fact]
    public void PreservedNotes_AreRecoveredWhenPackageDirectoryNameChanges()
    {
        using TempRepo repo = TempRepo.Create();
        repo.WriteNotes(@"package\PackingProof+v9.9.9\PackingProof+v9.9.9", HumanNotes);
        string suffixedDir = repo.WriteNotes(
            @"package\PackingProof+v9.9.9-2-g1234abcd\PackingProof+v9.9.9-2-g1234abcd",
            repo.TemplateText);

        ResolvedNotes resolved = repo.ResolveNotes(outputDir: suffixedDir);

        Assert.Equal(HumanNotes, resolved.Text);
        Assert.EndsWith(
            Path.Combine("PackingProof+v9.9.9", "PackingProof+v9.9.9", NotesFileName),
            resolved.Path,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("2-g1234abcd", resolved.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreservedNotes_IgnoreTemplateSkeleton()
    {
        using TempRepo repo = TempRepo.Create();
        repo.WriteNotes(@"package\PackingProof+v9.9.9\PackingProof+v9.9.9", repo.TemplateText);
        string suffixedDir = repo.WriteNotes(
            @"package\PackingProof+v9.9.9-2-g1234abcd\PackingProof+v9.9.9-2-g1234abcd",
            repo.TemplateText);

        ResolvedNotes resolved = repo.ResolveNotes(outputDir: suffixedDir);

        Assert.Equal("", resolved.Text);
        Assert.Equal("", resolved.Path);
    }

    [Fact]
    public void PreservedNotes_FallBackToStableDraftLocation()
    {
        using TempRepo repo = TempRepo.Create();
        repo.WriteNotes(@"package\.release-notes", HumanNotes);

        ResolvedNotes resolved = repo.ResolveNotes(
            outputDir: Path.Combine(repo.Root, @"package\PackingProof+v9.9.9-3-gfeedface\PackingProof+v9.9.9-3-gfeedface"));

        Assert.Equal(HumanNotes, resolved.Text);
        Assert.EndsWith(Path.Combine("package", ".release-notes", NotesFileName), resolved.Path, StringComparison.Ordinal);
    }

    /// <summary>
    /// 打包脚本不能退回"只认本次产物目录"的老写法，并且要把找到的笔记同时留在版本稳定位置。
    /// </summary>
    [Fact]
    public void Packaging_ScriptResolvesNotesAcrossPackageDirectoryNames()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string notesCommon = File.ReadAllText(
            Path.Combine(repositoryRoot, "Tools", "ReleaseNotes.Common.ps1"),
            Encoding.UTF8);

        Assert.Contains("Resolve-PreservedReleaseNotes", publishScript, StringComparison.Ordinal);
        Assert.Contains("Get-ReleaseNotesDraftPath", publishScript, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$preservedReleaseNotes = if (Test-Path -LiteralPath $releaseNotesPath",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains("function Resolve-PreservedReleaseNotes", notesCommon, StringComparison.Ordinal);
        Assert.Contains("function Get-ReleaseNotesDraftPath", notesCommon, StringComparison.Ordinal);
        Assert.Contains("function Test-ReleaseNotesIsTemplate", notesCommon, StringComparison.Ordinal);
    }

    private sealed record ResolvedNotes(string Text, string Path);

    private sealed class TempRepo : IDisposable
    {
        private TempRepo(string root, string templateText)
        {
            Root = root;
            TemplateText = templateText;
        }

        public string Root { get; }

        public string TemplateText { get; }

        public static TempRepo Create()
        {
            string repositoryRoot = FindRepositoryRoot();
            string root = Path.Combine(Path.GetTempPath(), "pp-notes-preserve", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            string template = File.ReadAllText(
                Path.Combine(repositoryRoot, "RELEASE_NOTES_TEMPLATE.md"),
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(root, "RELEASE_NOTES_TEMPLATE.md"),
                template,
                new UTF8Encoding(false));
            return new TempRepo(root, template);
        }

        public string WriteNotes(string directory, string text)
        {
            string fullDirectory = Path.Combine(Root, directory);
            Directory.CreateDirectory(fullDirectory);
            File.WriteAllText(
                Path.Combine(fullDirectory, NotesFileName),
                text,
                new UTF8Encoding(false));
            return fullDirectory;
        }

        public ResolvedNotes ResolveNotes(string outputDir)
        {
            string commonScript = Path.Combine(FindRepositoryRoot(), "Tools", "ReleaseNotes.Common.ps1");
            string resultPath = Path.Combine(Root, "resolved.json");
            string script = string.Join(
                Environment.NewLine,
                "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8",
                "$OutputEncoding = [System.Text.Encoding]::UTF8",
                "$ErrorActionPreference = 'Stop'",
                $". '{Escape(commonScript)}'",
                $"$resolved = Resolve-PreservedReleaseNotes -RepoRoot '{Escape(Root)}' -NormalizedVersion '{Version}' -OutputDir '{Escape(outputDir)}'",
                "$json = @{ Text = [string]$resolved.Text; Path = [string]$resolved.Path } | ConvertTo-Json -Compress",
                $"[System.IO.File]::WriteAllText('{Escape(resultPath)}', $json, [System.Text.UTF8Encoding]::new($false))",
                "Write-Output 'OK'");

            (int exitCode, string output) = RunPwsh(script);
            Assert.True(exitCode == 0, output);

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(resultPath, Encoding.UTF8));
            return new ResolvedNotes(
                document.RootElement.GetProperty("Text").GetString() ?? "",
                document.RootElement.GetProperty("Path").GetString() ?? "");
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
        }
    }

    private static (int ExitCode, string Output) RunPwsh(string script)
    {
        // 仓库脚本按 UTF-8 无 BOM 存中文，Windows PowerShell 5.1 会把中文读成 ANSI 导致解析失败，
        // 所以和文档一致，统一用 pwsh 7 跑。
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
