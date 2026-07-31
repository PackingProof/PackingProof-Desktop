using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

[CollectionDefinition("ManualPatchStaging", DisableParallelization = true)]
public sealed class ManualPatchStagingCollection;

[Collection("ManualPatchStaging")]
public sealed class ManualPatchStagingTests
{
    [Fact]
    public async Task Stager_CopiesVerifiedPackageToLauncherPendingWithoutConfig()
    {
        using var fixture = new ManualPatchStagingFixture();
        fixture.CreatePackage("0.0.18", "0.0.30", "new-content");

        ProcessResult result = await fixture.RunAsync();

        Assert.True(
            result.ExitCode == 0,
            $"exit={result.ExitCode}{Environment.NewLine}stdout={result.StandardOutput}{Environment.NewLine}stderr={result.StandardError}");
        Assert.Equal(
            File.ReadAllBytes(fixture.SourcePatchPath),
            File.ReadAllBytes(fixture.PendingPatchPath));
        Assert.Equal(
            File.ReadAllText(fixture.SourceManifestPath, Encoding.UTF8),
            File.ReadAllText(fixture.PendingManifestPath, Encoding.UTF8));
        Assert.False(File.Exists(Path.Combine(fixture.UserDataDirectory, "config.json")));
    }

    [Fact]
    public async Task Stager_RejectsTamperedPackageWithoutReplacingExistingPending()
    {
        using var fixture = new ManualPatchStagingFixture();
        fixture.CreateExistingPending("0.0.29", "existing-patch");
        fixture.CreatePackage("0.0.18", "0.0.30", "new-content");
        File.AppendAllText(fixture.SourcePatchPath, "tampered", Encoding.UTF8);

        ProcessResult result = await fixture.RunAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("大小校验失败", result.StandardOutput + result.StandardError);
        Assert.Equal("existing-patch", File.ReadAllText(fixture.PendingPatchPath, Encoding.UTF8));
    }

    [Fact]
    public async Task Stager_DoesNotReplaceNewerPendingPackage()
    {
        using var fixture = new ManualPatchStagingFixture();
        fixture.CreateExistingPending("0.0.31", "newer-patch");
        fixture.CreatePackage("0.0.18", "0.0.30", "new-content");

        ProcessResult result = await fixture.RunAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("已有更高版本", result.StandardOutput + result.StandardError);
        Assert.Equal("newer-patch", File.ReadAllText(fixture.PendingPatchPath, Encoding.UTF8));
    }

    private sealed class ManualPatchStagingFixture : IDisposable
    {
        private readonly string _root;
        private readonly string _packageRoot;
        private readonly string _scriptPath;

        public ManualPatchStagingFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "epm-patch-stage-tests", Guid.NewGuid().ToString("N"));
            _packageRoot = Path.Combine(_root, "package");
            UserDataDirectory = Path.Combine(_root, "user-data");
            Directory.CreateDirectory(_packageRoot);
            Directory.CreateDirectory(UserDataDirectory);
            _scriptPath = Path.Combine(FindRepositoryRoot(), "Tools", "Stage-AppPatch.ps1");
        }

        public string UserDataDirectory { get; }
        public string SourcePatchPath => Path.Combine(_packageRoot, "ExpressPackingMonitoring_AppPatch_v0.0.30.zip");
        public string SourceManifestPath => Path.Combine(_packageRoot, "update_manifest.json");
        public string PendingDirectory => Path.Combine(UserDataDirectory, "cache", "updates", "pending");
        public string PendingPatchPath => Directory
            .EnumerateFiles(PendingDirectory, "ExpressPackingMonitoring_AppPatch_v*.zip")
            .Single();
        public string PendingManifestPath => Path.Combine(PendingDirectory, "update_manifest.json");

        public void CreatePackage(string baselineVersion, string latestVersion, string content)
        {
            string payloadPath = Path.Combine(_root, "payload.txt");
            File.WriteAllText(payloadPath, content, Encoding.UTF8);
            byte[] payloadBytes = File.ReadAllBytes(payloadPath);
            string payloadHash = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant();
            string patchManifest = JsonSerializer.Serialize(new
            {
                type = "baseline_patch",
                patch_baseline_version = baselineVersion,
                latest_version = latestVersion,
                files = new[]
                {
                    new
                    {
                        path = "payload.txt",
                        sha256 = payloadHash,
                        size = payloadBytes.LongLength
                    }
                }
            });

            using (ZipArchive archive = ZipFile.Open(SourcePatchPath, ZipArchiveMode.Create))
            {
                ZipArchiveEntry manifestEntry = archive.CreateEntry("patch_manifest.json");
                using (var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false)))
                    writer.Write(patchManifest);

                ZipArchiveEntry payloadEntry = archive.CreateEntry("files/payload.txt");
                using Stream entryStream = payloadEntry.Open();
                entryStream.Write(payloadBytes);
            }

            byte[] patchBytes = File.ReadAllBytes(SourcePatchPath);
            string patchHash = Convert.ToHexString(SHA256.HashData(patchBytes)).ToLowerInvariant();
            string updateManifest = JsonSerializer.Serialize(new
            {
                latest_version = latestVersion,
                title = "测试更新",
                release_page = "https://example.invalid/release",
                patch_baseline_version = baselineVersion,
                patch_supported = true,
                patch_package = new
                {
                    type = "baseline_patch",
                    url = $"https://example.invalid/{Path.GetFileName(SourcePatchPath)}",
                    sha256 = patchHash,
                    size = patchBytes.LongLength
                },
                notes = Array.Empty<string>()
            });
            File.WriteAllText(SourceManifestPath, updateManifest, new UTF8Encoding(false));
        }

        public void CreateExistingPending(string latestVersion, string content)
        {
            Directory.CreateDirectory(PendingDirectory);
            string patchPath = Path.Combine(PendingDirectory, "ExpressPackingMonitoring_AppPatch_v0.0.30.zip");
            File.WriteAllText(patchPath, content, Encoding.UTF8);
            File.WriteAllText(
                PendingManifestPath,
                JsonSerializer.Serialize(new { latest_version = latestVersion }),
                new UTF8Encoding(false));
        }

        public async Task<ProcessResult> RunAsync()
        {
            string powershellPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = powershellPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.Environment["EPM_TEST_STAGE_SCRIPT"] = _scriptPath;
            startInfo.Environment["EPM_TEST_PACKAGE_ROOT"] = _packageRoot;
            startInfo.Environment["EPM_TEST_USER_DATA"] = UserDataDirectory;
            foreach (string argument in new[]
            {
                "-NoProfile",
                "-ExecutionPolicy", "Bypass",
                "-Command",
                "$text=[System.IO.File]::ReadAllText($env:EPM_TEST_STAGE_SCRIPT,[System.Text.Encoding]::UTF8); " +
                "& ([ScriptBlock]::Create($text)) -PackageRoot $env:EPM_TEST_PACKAGE_ROOT -UserDataDirectory $env:EPM_TEST_USER_DATA"
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(startInfo)!;
            string stdout = await process.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

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
}
