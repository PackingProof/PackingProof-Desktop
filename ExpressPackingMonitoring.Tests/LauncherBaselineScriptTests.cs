using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class LauncherBaselineScriptTests
{
    [Fact]
    public void LockedPackage_ValidatesAndExtractsExactLauncher()
    {
        string repositoryRoot = FindRepositoryRoot();
        using var fixture = new Fixture(repositoryRoot);

        ProcessResult result = fixture.RunValidation();

        Assert.True(result.ExitCode == 0, $"{result.Output}\n{result.Error}");
        Assert.Equal(fixture.LauncherBytes, File.ReadAllBytes(fixture.ExtractedLauncherPath));
    }

    [Fact]
    public void TamperedPackage_IsRejectedWithoutPublishingLauncher()
    {
        string repositoryRoot = FindRepositoryRoot();
        using var fixture = new Fixture(repositoryRoot);
        File.AppendAllText(fixture.PackagePath, "tampered", Encoding.UTF8);

        ProcessResult result = fixture.RunValidation();

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(fixture.ExtractedLauncherPath));
    }

    [Fact]
    public void LogicalFingerprint_IncludesRuntimeUpdateUrlAndTrackedInputs()
    {
        string repositoryRoot = FindRepositoryRoot();
        string commonScript = Path.Combine(repositoryRoot, "Tools", "LauncherBaseline.Common.ps1");
        string command = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{EscapePowerShell(commonScript)}}'
            $one = Get-LauncherLogicalFingerprint -RepositoryRoot '{{EscapePowerShell(repositoryRoot)}}' -Runtime 'win-x64' -UpdateCheckUrl 'https://example.test/one'
            $same = Get-LauncherLogicalFingerprint -RepositoryRoot '{{EscapePowerShell(repositoryRoot)}}' -Runtime 'win-x64' -UpdateCheckUrl 'https://example.test/one'
            $otherUrl = Get-LauncherLogicalFingerprint -RepositoryRoot '{{EscapePowerShell(repositoryRoot)}}' -Runtime 'win-x64' -UpdateCheckUrl 'https://example.test/two'
            $otherRuntime = Get-LauncherLogicalFingerprint -RepositoryRoot '{{EscapePowerShell(repositoryRoot)}}' -Runtime 'win-arm64' -UpdateCheckUrl 'https://example.test/one'
            $files = @(Get-LauncherFingerprintFiles)
            if ($one -ne $same -or $one -eq $otherUrl -or $one -eq $otherRuntime -or
                $files -notcontains 'Tools\Install-LauncherPatch.cmd' -or
                $files -notcontains 'Tools\Apply-LauncherPatch.ps1' -or
                $files -notcontains 'ExpressPackingMonitoring.UpdateCore\UpdateMetadataClient.cs' -or
                $files -notcontains 'ExpressPackingMonitoring.UpdateCore\PackageDownloadRoutePolicy.cs') { exit 1 }
            """;

        ProcessResult result = RunPowerShell(command, repositoryRoot);

        Assert.True(result.ExitCode == 0, $"{result.Output}\n{result.Error}");
    }

    [Fact]
    public void CommandNormalizer_WritesAsciiCrLfRegardlessOfSourceLineEndings()
    {
        string repositoryRoot = FindRepositoryRoot();
        string commonScript = Path.Combine(repositoryRoot, "Tools", "LauncherBaseline.Common.ps1");
        string root = Path.Combine(Path.GetTempPath(), $"packingproof-command-normalize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source.cmd");
            string destination = Path.Combine(root, "destination.cmd");
            File.WriteAllText(source, "@echo off\nsetlocal\necho ok\n", new UTF8Encoding(false));
            string command = $$"""
                $ErrorActionPreference = 'Stop'
                . '{{EscapePowerShell(commonScript)}}'
                Copy-NormalizedCommandFile -SourcePath '{{EscapePowerShell(source)}}' -DestinationPath '{{EscapePowerShell(destination)}}'
                """;

            ProcessResult result = RunPowerShell(command, repositoryRoot);

            Assert.True(result.ExitCode == 0, $"{result.Output}\n{result.Error}");
            byte[] bytes = File.ReadAllBytes(destination);
            Assert.All(bytes, value => Assert.InRange(value, (byte)0, (byte)127));
            for (int index = 0; index < bytes.Length; index++)
            {
                if (bytes[index] == (byte)'\n')
                    Assert.True(index > 0 && bytes[index - 1] == (byte)'\r');
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LogicalFingerprint_ChangesWhenManualLauncherInstallerChanges()
    {
        string repositoryRoot = FindRepositoryRoot();
        string commonScript = Path.Combine(repositoryRoot, "Tools", "LauncherBaseline.Common.ps1");
        string root = Path.Combine(Path.GetTempPath(), $"packingproof-launcher-inputs-{Guid.NewGuid():N}");
        try
        {
            foreach (string relativePath in new[]
            {
                @"ExpressPackingMonitoring.Launcher\Program.cs",
                @"ExpressPackingMonitoring.Launcher\ExpressPackingMonitoring.Launcher.csproj",
                @"ExpressPackingMonitoring.UpdateCore\ExpressPackingMonitoring.UpdateCore.csproj",
                @"ExpressPackingMonitoring.UpdateCore\UpdateEndpointPolicy.cs",
                @"ExpressPackingMonitoring.UpdateCore\UpdateMetadataClient.cs",
                @"ExpressPackingMonitoring.UpdateCore\PackageDownloadRoutePolicy.cs",
                @"ExpressPackingMonitoring\app.ico",
                @"Tools\Install-LauncherPatch.cmd",
                @"Tools\Apply-LauncherPatch.ps1"
            })
            {
                string path = Path.Combine(root, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, relativePath, new UTF8Encoding(false));
            }

            string installer = Path.Combine(root, "Tools", "Install-LauncherPatch.cmd");
            string command = $$"""
                $ErrorActionPreference = 'Stop'
                . '{{EscapePowerShell(commonScript)}}'
                $before = Get-LauncherLogicalFingerprint -RepositoryRoot '{{EscapePowerShell(root)}}' -Runtime 'win-x64' -UpdateCheckUrl 'https://example.test/releases/latest'
                Add-Content -LiteralPath '{{EscapePowerShell(installer)}}' -Value 'echo changed' -Encoding ASCII
                $after = Get-LauncherLogicalFingerprint -RepositoryRoot '{{EscapePowerShell(root)}}' -Runtime 'win-x64' -UpdateCheckUrl 'https://example.test/releases/latest'
                if ($before -eq $after) { exit 1 }
                """;

            ProcessResult result = RunPowerShell(command, repositoryRoot);

            Assert.True(result.ExitCode == 0, $"{result.Output}\n{result.Error}");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly string _repositoryRoot;

        public Fixture(string repositoryRoot)
        {
            _repositoryRoot = repositoryRoot;
            _root = Path.Combine(Path.GetTempPath(), $"packingproof-launcher-baseline-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            LauncherBytes = Encoding.UTF8.GetBytes("locked-launcher-binary");
            PackagePath = Path.Combine(_root, "PackingProof_LauncherPatch_v1.2.3.zip");
            ExtractedLauncherPath = Path.Combine(_root, "extracted", "ExpressPackingMonitoring.exe");
            CreatePackage();
            CreateManifest();
        }

        public byte[] LauncherBytes { get; }
        public string PackagePath { get; }
        public string ExtractedLauncherPath { get; }
        private string ManifestPath => Path.Combine(_root, "launcher-baseline.json");

        public ProcessResult RunValidation()
        {
            string commonScript = Path.Combine(_repositoryRoot, "Tools", "LauncherBaseline.Common.ps1");
            string command = $$"""
                $ErrorActionPreference = 'Stop'
                . '{{EscapePowerShell(commonScript)}}'
                $baseline = Read-LauncherBaselineManifest -ManifestPath '{{EscapePowerShell(ManifestPath)}}'
                Expand-LauncherBaselinePackage -PackagePath '{{EscapePowerShell(PackagePath)}}' -DestinationPath '{{EscapePowerShell(ExtractedLauncherPath)}}' -Baseline $baseline
                """;
            return RunPowerShell(command, _repositoryRoot);
        }

        private void CreatePackage()
        {
            using ZipArchive archive = ZipFile.Open(PackagePath, ZipArchiveMode.Create);
            WriteEntry(archive, "ExpressPackingMonitoring.exe", LauncherBytes);
            WriteEntry(archive, "双击更新启动器.cmd", "cmd"u8.ToArray());
            WriteEntry(archive, "apply_launcher_patch.ps1", "script"u8.ToArray());
            WriteEntry(archive, "launcher_patch_manifest.json", "{}"u8.ToArray());
            WriteEntry(archive, "启动器更新说明.txt", "notice"u8.ToArray());
        }

        private void CreateManifest()
        {
            var manifest = new
            {
                schema_version = 1,
                protocol_version = 1,
                version = "1.2.3",
                tag = "launcher-v1.2.3",
                release_tag = "v1.2.3",
                runtime = "win-x64",
                update_check_url = "https://example.test/releases/latest",
                source_fingerprint = new string('a', 64),
                package = new
                {
                    file = Path.GetFileName(PackagePath),
                    size = new FileInfo(PackagePath).Length,
                    sha256 = ComputeSha256(PackagePath),
                    executable_size = LauncherBytes.Length,
                    executable_sha256 = Convert.ToHexString(SHA256.HashData(LauncherBytes)).ToLowerInvariant()
                }
            };
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest), Encoding.UTF8);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }

        private static void WriteEntry(ZipArchive archive, string name, byte[] content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name);
            using Stream stream = entry.Open();
            stream.Write(content);
        }
    }

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

    private static string ComputeSha256(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string EscapePowerShell(string path) => path.Replace("'", "''", StringComparison.Ordinal);

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
