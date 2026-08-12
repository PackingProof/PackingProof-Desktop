using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class FFmpegBaselineScriptTests
{
    [Fact]
    public void ValidPackage_ExtractsPinnedExecutable()
    {
        using var fixture = new Fixture();

        ProcessResult result = fixture.RunExpand();

        Assert.True(result.ExitCode == 0, $"{result.Output}\n{result.Error}");
        Assert.Equal(fixture.ExecutableBytes, File.ReadAllBytes(fixture.DestinationPath));
    }

    [Fact]
    public void CachedExecutable_IsUsedWithoutDownload()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.CacheDirectory);
        File.WriteAllBytes(Path.Combine(fixture.CacheDirectory, "ffmpeg.exe"), fixture.ExecutableBytes);

        ProcessResult result = fixture.RunResolve("{ param($url, $path) throw 'download must not run' }");

        Assert.True(result.ExitCode == 0, $"{result.Output}\n{result.Error}");
        Assert.Equal(fixture.ExecutableBytes, File.ReadAllBytes(fixture.DestinationPath));
    }

    [Fact]
    public void DownloadFailure_UsesSecondManifestUrl()
    {
        using var fixture = new Fixture();
        string package = EscapePowerShell(fixture.PackagePath);
        string downloader = $$"""
            {
                param($url, $path)
                if ($url -eq 'https://www.gyan.dev/first.7z') { throw 'first source unavailable' }
                Copy-Item -LiteralPath '{{package}}' -Destination $path -Force
            }
            """;

        ProcessResult result = fixture.RunResolve(downloader, "-MaxAttemptsPerUrl 1");

        Assert.True(result.ExitCode == 0, $"{result.Output}\n{result.Error}");
        Assert.Equal(fixture.ExecutableBytes, File.ReadAllBytes(fixture.DestinationPath));
    }

    [Fact]
    public void TransientDownloadFailure_RetriesSameUrlBeforeSucceeding()
    {
        using var fixture = new Fixture();
        string package = EscapePowerShell(fixture.PackagePath);
        string downloader = $$"""
            {
                param($url, $path)
                $marker = "$path.attempt"
                if (-not (Test-Path -LiteralPath $marker)) {
                    Set-Content -LiteralPath $marker -Value '1' -Encoding UTF8
                    throw 'transient download failure'
                }
                if ((Get-Content -LiteralPath $marker -Raw -Encoding UTF8) -eq '1') {
                    Set-Content -LiteralPath $marker -Value '2' -Encoding UTF8
                    throw 'transient download failure'
                }
                Copy-Item -LiteralPath '{{package}}' -Destination $path -Force
            }
            """;

        ProcessResult result = fixture.RunResolve(downloader, "-MaxAttemptsPerUrl 3 -RetryDelaySeconds 0");

        Assert.True(result.ExitCode == 0, $"{result.Output}\n{result.Error}");
        Assert.Equal(fixture.ExecutableBytes, File.ReadAllBytes(fixture.DestinationPath));
    }

    [Fact]
    public void TamperedPackage_IsRejectedWithoutPublishingExecutable()
    {
        using var fixture = new Fixture();
        File.AppendAllText(fixture.PackagePath, "tampered", Encoding.UTF8);

        ProcessResult result = fixture.RunExpand();

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(fixture.DestinationPath));
    }

    [Fact]
    public void UnsafeArchivePath_IsRejected()
    {
        using var fixture = new Fixture(extraEntries: [("../outside.txt", "unsafe"u8.ToArray())]);

        ProcessResult result = fixture.RunExpand();

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(fixture.DestinationPath));
    }

    [Fact]
    public void DuplicateExecutableEntry_IsRejected()
    {
        using var fixture = new Fixture(duplicateExecutable: true);

        ProcessResult result = fixture.RunExpand();

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(fixture.DestinationPath));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly string _repositoryRoot;
        private readonly string _manifestPath;
        private readonly string _sevenZipPath;

        public Fixture(
            IReadOnlyList<(string Name, byte[] Content)>? extraEntries = null,
            bool duplicateExecutable = false)
        {
            _repositoryRoot = FindRepositoryRoot();
            _sevenZipPath = FindSevenZip();
            _root = Path.Combine(Path.GetTempPath(), $"packingproof-ffmpeg-baseline-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            ExecutableBytes = Encoding.UTF8.GetBytes("pinned-ffmpeg-executable");
            PackagePath = Path.Combine(_root, "ffmpeg-test.zip");
            DestinationPath = Path.Combine(_root, "output", "ffmpeg.exe");
            CacheDirectory = Path.Combine(_root, "cache");
            _manifestPath = Path.Combine(_root, "ffmpeg-baseline.json");
            CreatePackage(extraEntries, duplicateExecutable);
            CreateManifest();
        }

        public byte[] ExecutableBytes { get; }
        public string PackagePath { get; }
        public string DestinationPath { get; }
        public string CacheDirectory { get; }

        public ProcessResult RunExpand()
        {
            string commonScript = Path.Combine(_repositoryRoot, "Tools", "FFmpegBaseline.Common.ps1");
            string command = $$"""
                $ErrorActionPreference = 'Stop'
                . '{{EscapePowerShell(commonScript)}}'
                $baseline = Read-FFmpegBaselineManifest -ManifestPath '{{EscapePowerShell(_manifestPath)}}'
                Expand-FFmpegBaselinePackage -PackagePath '{{EscapePowerShell(PackagePath)}}' -DestinationPath '{{EscapePowerShell(DestinationPath)}}' -Baseline $baseline -SevenZipExecutable '{{EscapePowerShell(_sevenZipPath)}}'
                """;
            return RunPowerShell(command, _repositoryRoot);
        }

        public ProcessResult RunResolve(string downloader, string? resolveArguments = null)
        {
            string commonScript = Path.Combine(_repositoryRoot, "Tools", "FFmpegBaseline.Common.ps1");
            string arguments = string.IsNullOrWhiteSpace(resolveArguments) ? "" : " " + resolveArguments;
            string command = $$"""
                $ErrorActionPreference = 'Stop'
                . '{{EscapePowerShell(commonScript)}}'
                $baseline = Read-FFmpegBaselineManifest -ManifestPath '{{EscapePowerShell(_manifestPath)}}'
                $download = {{downloader}}
                Resolve-FFmpegBaselineExecutable -Baseline $baseline -CacheDirectory '{{EscapePowerShell(CacheDirectory)}}' -DestinationPath '{{EscapePowerShell(DestinationPath)}}' -SevenZipExecutable '{{EscapePowerShell(_sevenZipPath)}}' -DownloadFile $download{{arguments}}
                """;
            return RunPowerShell(command, _repositoryRoot);
        }

        private void CreatePackage(
            IReadOnlyList<(string Name, byte[] Content)>? extraEntries,
            bool duplicateExecutable)
        {
            using ZipArchive archive = ZipFile.Open(PackagePath, ZipArchiveMode.Create);
            WriteEntry(archive, "ffmpeg-test/bin/ffmpeg.exe", ExecutableBytes);
            if (duplicateExecutable)
                WriteEntry(archive, "ffmpeg-test/bin/ffmpeg.exe", ExecutableBytes);
            foreach ((string name, byte[] content) in extraEntries ?? [])
                WriteEntry(archive, name, content);
        }

        private void CreateManifest()
        {
            var manifest = new
            {
                schema_version = 1,
                version = "test",
                runtime = "win-x64",
                provider = "test",
                app_patch_compatible_executables = new[]
                {
                    new
                    {
                        version = "test",
                        variant = "test",
                        size = ExecutableBytes.Length,
                        sha256 = Convert.ToHexString(SHA256.HashData(ExecutableBytes)).ToLowerInvariant()
                    }
                },
                package = new
                {
                    file = Path.GetFileName(PackagePath),
                    size = new FileInfo(PackagePath).Length,
                    sha256 = ComputeSha256(PackagePath),
                    entry = "ffmpeg-test\\bin\\ffmpeg.exe",
                    executable_size = ExecutableBytes.Length,
                    executable_sha256 = Convert.ToHexString(SHA256.HashData(ExecutableBytes)).ToLowerInvariant(),
                    urls = new[]
                    {
                        "https://www.gyan.dev/first.7z",
                        "https://github.com/GyanD/codexffmpeg/releases/download/test/second.7z"
                    }
                }
            };
            File.WriteAllText(_manifestPath, JsonSerializer.Serialize(manifest), Encoding.UTF8);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
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

    private static string FindSevenZip()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe")
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("7z.exe is required for FFmpeg baseline tests");
    }

    private static string ComputeSha256(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

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

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
