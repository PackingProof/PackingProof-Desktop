using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class AppPatchRuntimeCompatibilityScriptTests
{
    [Theory]
    [InlineData("7.1.1")]
    [InlineData("8.1.2")]
    public void KnownFfmpegAndVlcSuperset_AreCompatible(string version)
    {
        using var fixture = new Fixture(version);
        fixture.AddVlcFile("plugins\\codec.dll", "codec");
        fixture.AddBaselineVlcFile("plugins\\unused-old-plugin.dll", "old");

        CompatibilityResult result = fixture.Check();

        Assert.True(result.Compatible, result.Reason);
        Assert.Contains(version, result.Reason);
    }

    [Fact]
    public void UnknownFfmpegHash_IsRejected()
    {
        using var fixture = new Fixture();
        fixture.ReplaceBaselineFfmpeg("unknown");
        fixture.AddVlcFile("plugins\\codec.dll", "codec");

        CompatibilityResult result = fixture.Check();

        Assert.False(result.Compatible);
        Assert.Contains("白名单", result.Reason);
    }

    [Fact]
    public void MissingFfmpeg_IsRejected()
    {
        using var fixture = new Fixture();
        File.Delete(fixture.BaselineFfmpegPath);
        fixture.AddVlcFile("plugins\\codec.dll", "codec");

        CompatibilityResult result = fixture.Check();

        Assert.False(result.Compatible);
        Assert.Contains("缺少 FFmpeg", result.Reason);
    }

    [Fact]
    public void MissingRequiredVlcFile_IsRejected()
    {
        using var fixture = new Fixture();
        fixture.AddCurrentVlcFile("plugins\\codec.dll", "codec");

        CompatibilityResult result = fixture.Check();

        Assert.False(result.Compatible);
        Assert.Contains("缺少 LibVLC 必需文件", result.Reason);
    }

    [Fact]
    public void ChangedRequiredVlcFile_IsRejected()
    {
        using var fixture = new Fixture();
        fixture.AddCurrentVlcFile("plugins\\codec.dll", "current");
        fixture.AddBaselineVlcFile("plugins\\codec.dll", "baseline");

        CompatibilityResult result = fixture.Check();

        Assert.False(result.Compatible);
        Assert.Contains("LibVLC 必需文件不兼容", result.Reason);
    }

    [Theory]
    [InlineData("tools/ffmpeg.exe", true)]
    [InlineData("TOOLS\\FFMPEG.EXE", true)]
    [InlineData("libvlc/win-x64/libvlc.dll", true)]
    [InlineData("libvlc2/libvlc.dll", false)]
    [InlineData("ExpressPackingMonitoring.dll", false)]
    public void ManagedRuntimePathClassification_IsExact(string path, bool expected)
    {
        string repositoryRoot = FindRepositoryRoot();
        string commonScript = Path.Combine(repositoryRoot, "Tools", "AppPatchRuntimeCompatibility.Common.ps1");
        string command = $$"""
            . '{{EscapePowerShell(commonScript)}}'
            [Console]::Write((Test-IsAppPatchManagedRuntimePath -RelativePath '{{EscapePowerShell(path)}}').ToString().ToLowerInvariant())
            """;

        ProcessResult result = RunPowerShell(command, repositoryRoot);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected.ToString().ToLowerInvariant(), result.Output.Trim());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly string _repositoryRoot;
        private readonly byte[] _currentFfmpeg;
        private readonly List<CompatibleExecutable> _compatibleExecutables = [];

        public Fixture(string baselineVersion = "7.1.1")
        {
            _repositoryRoot = FindRepositoryRoot();
            _root = Path.Combine(Path.GetTempPath(), $"packingproof-app-patch-runtime-{Guid.NewGuid():N}");
            CurrentAppDirectory = Path.Combine(_root, "current");
            BaselineAppDirectory = Path.Combine(_root, "baseline");
            Directory.CreateDirectory(Path.Combine(CurrentAppDirectory, "tools"));
            Directory.CreateDirectory(Path.Combine(BaselineAppDirectory, "tools"));
            Directory.CreateDirectory(Path.Combine(CurrentAppDirectory, "libvlc", "win-x64"));
            Directory.CreateDirectory(Path.Combine(BaselineAppDirectory, "libvlc", "win-x64"));

            _currentFfmpeg = Encoding.UTF8.GetBytes("current-essentials");
            byte[] fullFfmpeg = Encoding.UTF8.GetBytes("old-full");
            byte[] essentialsFfmpeg = _currentFfmpeg;
            _compatibleExecutables.Add(CreateCompatible("7.1.1", "full", fullFfmpeg));
            _compatibleExecutables.Add(CreateCompatible("8.1.2", "essentials", essentialsFfmpeg));
            File.WriteAllBytes(CurrentFfmpegPath, _currentFfmpeg);
            byte[] selected = baselineVersion == "8.1.2" ? essentialsFfmpeg : fullFfmpeg;
            File.WriteAllBytes(BaselineFfmpegPath, selected);
        }

        public string CurrentAppDirectory { get; }
        public string BaselineAppDirectory { get; }
        public string CurrentFfmpegPath => Path.Combine(CurrentAppDirectory, "tools", "ffmpeg.exe");
        public string BaselineFfmpegPath => Path.Combine(BaselineAppDirectory, "tools", "ffmpeg.exe");

        public void ReplaceBaselineFfmpeg(string content)
            => File.WriteAllText(BaselineFfmpegPath, content, Encoding.UTF8);

        public void AddVlcFile(string relativePath, string content)
        {
            AddCurrentVlcFile(relativePath, content);
            AddBaselineVlcFile(relativePath, content);
        }

        public void AddCurrentVlcFile(string relativePath, string content)
            => WriteFile(Path.Combine(CurrentAppDirectory, "libvlc", "win-x64", relativePath), content);

        public void AddBaselineVlcFile(string relativePath, string content)
            => WriteFile(Path.Combine(BaselineAppDirectory, "libvlc", "win-x64", relativePath), content);

        public CompatibilityResult Check()
        {
            string commonScript = Path.Combine(_repositoryRoot, "Tools", "AppPatchRuntimeCompatibility.Common.ps1");
            string manifestJson = JsonSerializer.Serialize(new
            {
                package = new
                {
                    executable_size = _currentFfmpeg.Length,
                    executable_sha256 = Hash(_currentFfmpeg)
                },
                app_patch_compatible_executables = _compatibleExecutables
            }).Replace("'", "''", StringComparison.Ordinal);
            string command = $$"""
                $ErrorActionPreference = 'Stop'
                . '{{EscapePowerShell(commonScript)}}'
                $manifest = '{{manifestJson}}' | ConvertFrom-Json
                $result = Test-AppPatchRuntimeCompatibility -CurrentAppDir '{{EscapePowerShell(CurrentAppDirectory)}}' -BaselineAppDir '{{EscapePowerShell(BaselineAppDirectory)}}' -FFmpegBaseline $manifest
                [Console]::Write(($result | ConvertTo-Json -Compress))
                """;
            ProcessResult result = RunPowerShell(command, _repositoryRoot);
            Assert.True(result.ExitCode == 0, $"{result.Output}\n{result.Error}");
            return JsonSerializer.Deserialize<CompatibilityResult>(result.Output.Trim(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            })!;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private static CompatibleExecutable CreateCompatible(string version, string variant, byte[] bytes)
            => new(version, variant, bytes.Length, Hash(bytes));

        private static void WriteFile(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, Encoding.UTF8);
        }
    }

    private static ProcessResult RunPowerShell(string command, string workingDirectory)
    {
        command = "$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)\n" + command;
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

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string EscapePowerShell(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

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

    private sealed record CompatibleExecutable(string version, string variant, long size, string sha256);
    private sealed record CompatibilityResult(bool Compatible, string Reason);
    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
