using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class AppPatchOpenCvExclusionTests
{
    [Fact]
    public void MinimalOpenCv_IsNeitherWrittenNorDeletedByAppPatch()
    {
        using var fixture = new Fixture();

        ProcessResult result = fixture.CreatePatch();

        Assert.True(result.ExitCode == 0, $"{result.Output}\n{result.Error}");
        using ZipArchive archive = ZipFile.OpenRead(fixture.PatchPath);
        Assert.DoesNotContain(
            archive.Entries,
            entry => Path.GetFileName(entry.FullName).Equals(
                "OpenCvSharpExtern.dll",
                StringComparison.OrdinalIgnoreCase));

        string manifest = ReadEntry(archive, "patch_manifest.json");
        using JsonDocument document = JsonDocument.Parse(manifest);
        AssertNoOpenCv(document.RootElement.GetProperty("files"));
        if (document.RootElement.TryGetProperty("deletes", out JsonElement deletes))
            AssertNoOpenCv(deletes);
    }

    [Fact]
    public void ManagedOpenCvChange_BlocksPatchWhileNativeIsExcluded()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.CurrentDir, "OpenCvSharp.dll"), "managed-new");

        ProcessResult result = fixture.CreatePatch();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OpenCvSharp.dll changed", result.Output + result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingCurrentNativeOpenCv_BlocksPatch()
    {
        using var fixture = new Fixture();
        File.Delete(Path.Combine(fixture.CurrentDir, "OpenCvSharpExtern.dll"));

        ProcessResult result = fixture.CreatePatch();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("managed or native OpenCV is missing", result.Output + result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AppPatchIncludesMatchingNativeOpenCvWhenExclusionIsDisabled()
    {
        using var fixture = new Fixture();

        ProcessResult result = fixture.CreatePatch(excludeNativeOpenCv: false);

        Assert.True(result.ExitCode == 0, $"{result.Output}\n{result.Error}");
        using ZipArchive archive = ZipFile.OpenRead(fixture.PatchPath);
        Assert.Contains(
            archive.Entries,
            entry => entry.FullName.Equals(
                "files/OpenCvSharpExtern.dll",
                StringComparison.OrdinalIgnoreCase));

        string manifest = ReadEntry(archive, "patch_manifest.json");
        using JsonDocument document = JsonDocument.Parse(manifest);
        Assert.Contains(
            document.RootElement.GetProperty("files").EnumerateArray(),
            entry => entry.GetProperty("path").GetString() == "OpenCvSharpExtern.dll");
    }

    private static void AssertNoOpenCv(JsonElement entries)
    {
        Assert.DoesNotContain(
            entries.EnumerateArray(),
            entry => Path.GetFileName(entry.GetProperty("path").GetString() ?? "")
                .Equals("OpenCvSharpExtern.dll", StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        ZipArchiveEntry entry = Assert.Single(
            archive.Entries,
            candidate => candidate.FullName.Equals(name, StringComparison.Ordinal));
        using StreamReader reader = new(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            RepositoryRoot = FindRepositoryRoot();
            Root = Path.Combine(Path.GetTempPath(), $"packingproof-apppatch-opencv-{Guid.NewGuid():N}");
            BaselineDir = Path.Combine(Root, "baseline");
            CurrentDir = Path.Combine(Root, "current");
            PatchPath = Path.Combine(Root, "patch.zip");
            Directory.CreateDirectory(BaselineDir);
            Directory.CreateDirectory(CurrentDir);

            Write(BaselineDir, "ExpressPackingMonitoring.dll", "baseline-app");
            Write(BaselineDir, "OpenCvSharp.dll", "managed-same");
            Write(BaselineDir, "OpenCvSharpExtern.dll", "official-full");
            Write(CurrentDir, "ExpressPackingMonitoring.dll", "current-app");
            Write(CurrentDir, "OpenCvSharp.dll", "managed-same");
            Write(CurrentDir, "OpenCvSharpExtern.dll", "minimal");
            Write(CurrentDir, "new-file.txt", "new");
        }

        public string RepositoryRoot { get; }
        public string Root { get; }
        public string BaselineDir { get; }
        public string CurrentDir { get; }
        public string PatchPath { get; }

        public ProcessResult CreatePatch(bool excludeNativeOpenCv = true)
        {
            string packagingScript = Path.Combine(RepositoryRoot, "tools", "Publish-CleanPackage.ps1");
            string compatibilityScript = Path.Combine(
                RepositoryRoot,
                "tools",
                "AppPatchRuntimeCompatibility.Common.ps1");
            string launcherCommonScript = Path.Combine(
                RepositoryRoot,
                "tools",
                "LauncherBaseline.Common.ps1");
            string brandingCommonScript = Path.Combine(
                RepositoryRoot,
                "tools",
                "Branding.Common.ps1");
            string installerCmd = Path.Combine(RepositoryRoot, "tools", "Install-AppPatch.cmd");
            string installerScript = Path.Combine(RepositoryRoot, "tools", "Apply-AppPatch.ps1");
            string retireList = Path.Combine(Root, "retire.json");
            string nativeExclusion = excludeNativeOpenCv
                ? "@('OpenCvSharpExtern.dll')"
                : "@()";
            string command = $$"""
                $ErrorActionPreference = 'Stop'
                . '{{Escape(compatibilityScript)}}'
                . '{{Escape(launcherCommonScript)}}'
                . '{{Escape(brandingCommonScript)}}'
                $tokens = $null
                $errors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    '{{Escape(packagingScript)}}', [ref]$tokens, [ref]$errors)
                if ($errors.Count -gt 0) { throw $errors[0].Message }
                $functions = @($ast.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
                }, $true))
                . ([scriptblock]::Create(($functions | ForEach-Object { $_.Extent.Text }) -join [Environment]::NewLine))
                $repoRoot = '{{Escape(RepositoryRoot)}}'
                $brand = Get-BrandProfile -RepoRoot $repoRoot
                $ZipCompressionLevel = 'Optimal'
                $appPatchInstallerCmdName = '双击更新主程序.cmd'
                $appPatchInstallerScriptName = 'apply_app_patch.ps1'
                $appPatchNoticeName = '主程序更新说明.txt'
                New-AppPatchPackage `
                    -CurrentAppDir '{{Escape(CurrentDir)}}' `
                    -BaselineDir '{{Escape(BaselineDir)}}' `
                    -PatchZipPath '{{Escape(PatchPath)}}' `
                    -BaselineVersion '0.0.84' `
                    -LatestVersion '0.0.90' `
                    -InstallerCmdPath '{{Escape(installerCmd)}}' `
                    -InstallerScriptPath '{{Escape(installerScript)}}' `
                    -ExcludeCompatibleRuntimes `
                    -ExcludeRelativePaths {{nativeExclusion}} `
                    -RetireListPath '{{Escape(retireList)}}'
                """;
            return RunPowerShell(command, Root);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }

        private static void Write(string directory, string name, string content) =>
            File.WriteAllText(Path.Combine(directory, name), content, Encoding.UTF8);
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private static ProcessResult RunPowerShell(string command, string workingDirectory)
    {
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var startInfo = new ProcessStartInfo("pwsh.exe")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encoded);
        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output, error);
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

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
