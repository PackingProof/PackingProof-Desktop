using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ManualLauncherPatchInstallerTests
{
    [Fact]
    public async Task Installer_ReplacesOnlyRootLauncherAndRetainsVerifiedBackup()
    {
        using var fixture = new Fixture();
        fixture.CreatePatch("new-launcher", validHash: true);

        ProcessResult result = await fixture.RunAsync();

        Assert.True(
            result.ExitCode == 0,
            $"exit={result.ExitCode}{Environment.NewLine}stdout={result.StandardOutput}{Environment.NewLine}stderr={result.StandardError}");
        Assert.Equal("new-launcher", File.ReadAllText(fixture.LauncherPath, Encoding.UTF8));
        Assert.Equal("app-unchanged", File.ReadAllText(fixture.AppMarkerPath, Encoding.UTF8));
        Assert.Single(Directory.GetFiles(fixture.BackupDirectory, "manual-launcher-*.bak"));
        Assert.Contains("启动器更新完成", result.StandardOutput);
    }

    [Fact]
    public async Task Installer_RejectsInvalidHashWithoutChangingLauncherOrApp()
    {
        using var fixture = new Fixture();
        fixture.CreatePatch("tampered-launcher", validHash: false);

        ProcessResult result = await fixture.RunAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("old-launcher", File.ReadAllText(fixture.LauncherPath, Encoding.UTF8));
        Assert.Equal("app-unchanged", File.ReadAllText(fixture.AppMarkerPath, Encoding.UTF8));
        Assert.Contains("SHA256 校验失败", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public async Task Installer_DoesNotDeleteUnrelatedBackupFilesWhenPruningLauncherBackups()
    {
        using var fixture = new Fixture();
        fixture.CreatePatch("new-launcher", validHash: true);
        Directory.CreateDirectory(fixture.BackupDirectory);
        string unrelatedBackup = Path.Combine(fixture.BackupDirectory, "recording-database.bak");
        File.WriteAllText(unrelatedBackup, "keep", Encoding.UTF8);
        for (int index = 0; index < 4; index++)
        {
            string launcherBackup = Path.Combine(fixture.BackupDirectory, $"manual-launcher-20260731-12000{index}.bak");
            File.WriteAllText(launcherBackup, $"old-{index}", Encoding.UTF8);
            File.SetLastWriteTimeUtc(launcherBackup, DateTime.UtcNow.AddMinutes(-10 - index));
        }

        ProcessResult result = await fixture.RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(unrelatedBackup));
        Assert.Equal("keep", File.ReadAllText(unrelatedBackup, Encoding.UTF8));
        Assert.Equal(3, Directory.GetFiles(fixture.BackupDirectory, "manual-launcher-*.bak").Length);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly string _patchRoot;
        private readonly string _configPath;
        private readonly string _scriptPath;

        public Fixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "epm-launcher-patch-tests", Guid.NewGuid().ToString("N"));
            _patchRoot = Path.Combine(_root, "patch");
            string installRoot = Path.Combine(_root, "installed");
            string appRoot = Path.Combine(installRoot, "app");
            string userDataRoot = Path.Combine(_root, "user-data");
            LauncherPath = Path.Combine(installRoot, "ExpressPackingMonitoring.exe");
            AppMarkerPath = Path.Combine(appRoot, "app-marker.txt");
            BackupDirectory = Path.Combine(userDataRoot, "cache", "launcher_backups");
            _configPath = Path.Combine(userDataRoot, "config.json");
            _scriptPath = Path.Combine(FindRepositoryRoot(), "Tools", "Apply-LauncherPatch.ps1");

            Directory.CreateDirectory(appRoot);
            Directory.CreateDirectory(userDataRoot);
            File.WriteAllText(LauncherPath, "old-launcher", Encoding.UTF8);
            File.WriteAllText(AppMarkerPath, "app-unchanged", Encoding.UTF8);
            File.WriteAllText(
                _configPath,
                JsonSerializer.Serialize(new { AppRootDirectory = appRoot }),
                Encoding.UTF8);
        }

        public string LauncherPath { get; }

        public string AppMarkerPath { get; }

        public string BackupDirectory { get; }

        public void CreatePatch(string content, bool validHash)
        {
            Directory.CreateDirectory(_patchRoot);
            string sourcePath = Path.Combine(_patchRoot, "ExpressPackingMonitoring.exe");
            File.WriteAllText(sourcePath, content, Encoding.UTF8);
            byte[] bytes = File.ReadAllBytes(sourcePath);
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!validHash)
                hash = new string('0', 64);
            File.WriteAllText(
                Path.Combine(_patchRoot, "launcher_patch_manifest.json"),
                JsonSerializer.Serialize(new
                {
                    type = "launcher_patch",
                    version = "0.0.99",
                    file = "ExpressPackingMonitoring.exe",
                    size = bytes.LongLength,
                    sha256 = hash
                }),
                Encoding.UTF8);
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
            startInfo.Environment["EPM_TEST_LAUNCHER_SCRIPT"] = _scriptPath;
            startInfo.Environment["EPM_TEST_LAUNCHER_PATCH_ROOT"] = _patchRoot;
            startInfo.Environment["EPM_TEST_LAUNCHER_CONFIG"] = _configPath;
            foreach (string argument in new[]
            {
                "-NoProfile",
                "-ExecutionPolicy", "Bypass",
                "-Command",
                "$text=[System.IO.File]::ReadAllText($env:EPM_TEST_LAUNCHER_SCRIPT,[System.Text.Encoding]::UTF8); & ([ScriptBlock]::Create($text)) -PatchRoot $env:EPM_TEST_LAUNCHER_PATCH_ROOT -ConfigPath $env:EPM_TEST_LAUNCHER_CONFIG -SkipProcessCheck"
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start Windows PowerShell");
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
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
