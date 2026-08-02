using System.Diagnostics;
using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ManualPatchCommandTests
{
    [Theory]
    [InlineData("Install-AppPatch.cmd", "apply_app_patch.ps1", "app-command-ok.txt")]
    [InlineData("Install-LauncherPatch.cmd", "apply_launcher_patch.ps1", "launcher-command-ok.txt")]
    public async Task CommandWrapper_IsAsciiAndInvokesUtf8PowerShellScript(
        string commandFileName,
        string scriptFileName,
        string markerFileName)
    {
        string repositoryRoot = FindRepositoryRoot();
        string sourceCommand = Path.Combine(repositoryRoot, "Tools", commandFileName);
        byte[] sourceBytes = File.ReadAllBytes(sourceCommand);
        Assert.All(sourceBytes, value => Assert.InRange(value, (byte)0, (byte)127));

        string root = Path.Combine(Path.GetTempPath(), "packingproof-cmd-tests", $"中文 路径 {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string commandPath = Path.Combine(root, commandFileName);
            File.Copy(sourceCommand, commandPath);
            string scriptPath = Path.Combine(root, scriptFileName);
            File.WriteAllText(
                scriptPath,
                $$"""
                param([string]$PatchRoot)
                [System.IO.File]::WriteAllText(
                    (Join-Path $PatchRoot '{{markerFileName}}'),
                    $PatchRoot,
                    [System.Text.UTF8Encoding]::new($false))
                exit 0
                """,
                new UTF8Encoding(false));

            ProcessResult result = await RunCommandAsync(commandPath);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(Path.Combine(root, markerFileName)));
            Assert.DoesNotContain("not recognized", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("不是内部或外部命令", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<ProcessResult> RunCommandAsync(string commandPath)
    {
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(commandPath);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start cmd.exe");
        process.StandardInput.Close();
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        return new ProcessResult(process.ExitCode, (await outputTask) + (await errorTask));
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

    private sealed record ProcessResult(int ExitCode, string Output);
}
