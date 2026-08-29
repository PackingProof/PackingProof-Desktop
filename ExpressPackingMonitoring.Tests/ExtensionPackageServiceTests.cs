using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Services.Extensions;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ExtensionPackageServiceTests
{
    [Fact]
    public void RunningProcessDetectionRequiresExecutableInsideExactInstallDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "packingproof-extension-process-tests");
        string installDirectory = Path.Combine(root, "sample.adapter", "1.0.0");

        Assert.True(ExtensionProcessManager.IsExecutableInDirectory(
            Path.Combine(installDirectory, "payload", "adapter.exe"),
            installDirectory));
        Assert.False(ExtensionProcessManager.IsExecutableInDirectory(
            Path.Combine(root, "sample.adapter-other", "adapter.exe"),
            installDirectory));
        Assert.False(ExtensionProcessManager.IsExecutableInDirectory("", installDirectory));
    }

    [Fact]
    public void RunningExtensionProcessCanBeDetectedAndTerminated()
    {
        string root = CreateTemporaryDirectory();
        string installDirectory = Path.Combine(root, "sample.adapter", "1.0.0");
        Directory.CreateDirectory(installDirectory);
        string executablePath = Path.Combine(installDirectory, "adapter.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), executablePath);
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = "/d /q /k",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;

        try
        {
            IReadOnlyList<RunningExtensionProcess> running = [];
            Assert.True(SpinWait.SpinUntil(() =>
            {
                running = ExtensionProcessManager.FindRunningProcesses(installDirectory)
                    .Where(candidate => candidate.ProcessId == process.Id)
                    .ToList();
                return running.Count > 0;
            }, TimeSpan.FromSeconds(3)));

            Assert.True(ExtensionProcessManager.TryTerminateProcesses(
                installDirectory,
                running,
                out string error), error);
            Assert.True(process.WaitForExit(5000));
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void RejectsPathTraversalBeforeExtraction()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string package = Path.Combine(root, "bad.ppx");
            using (ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "manifest.json", Manifest("sample.adapter", "external-adapter", "payload/adapter.exe"));
                WriteEntry(archive, "payload/adapter.exe", "safe");
                WriteEntry(archive, "../outside.txt", "unsafe");
            }
            Assert.Throws<InvalidDataException>(() => new ExtensionPackageService().Inspect(package));
            Assert.False(File.Exists(Path.Combine(root, "outside.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void RejectsSymbolicLinksAndSuspiciousCompressionRatios()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string symlinkPackage = Path.Combine(root, "symlink.ppx");
            using (ZipArchive archive = ZipFile.Open(symlinkPackage, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "manifest.json", Manifest("sample.adapter", "external-adapter", "payload/adapter.exe"));
                ZipArchiveEntry link = archive.CreateEntry("payload/adapter.exe");
                link.ExternalAttributes = 0xA000 << 16;
                using var writer = new StreamWriter(link.Open(), new UTF8Encoding(false));
                writer.Write("target");
            }
            Assert.Throws<InvalidDataException>(() => new ExtensionPackageService().Inspect(symlinkPackage));

            string bombPackage = Path.Combine(root, "ratio.ppx");
            using (ZipArchive archive = ZipFile.Open(bombPackage, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "manifest.json", Manifest("sample.adapter", "external-adapter", "payload/adapter.exe"));
                ZipArchiveEntry payload = archive.CreateEntry("payload/adapter.exe", CompressionLevel.SmallestSize);
                using Stream stream = payload.Open();
                stream.Write(new byte[300_000]);
            }
            Assert.Throws<InvalidDataException>(() => new ExtensionPackageService().Inspect(bombPackage));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void InstallsExternalAdapterWithoutExecutingPayloadAndPreservesExternalState()
    {
        string root = CreateTemporaryDirectory();
        string extensions = Path.Combine(root, "extensions");
        string scripts = Path.Combine(root, "scripts");
        string marker = Path.Combine(root, "executed.txt");
        string externalState = Path.Combine(root, "QQBot", "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(externalState)!);
            File.WriteAllText(externalState, "keep", Encoding.UTF8);
            string package = Path.Combine(root, "adapter.ppx");
            using (ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "manifest.json", Manifest("sample.adapter", "external-adapter", "payload/adapter.cmd"));
                WriteEntry(archive, "README.md", "Run the adapter manually");
                WriteEntry(archive, "payload/adapter.cmd", $"@echo executed>\"{marker}\"");
            }
            var installation = new ExtensionInstallationService(
                extensions,
                new UserscriptCatalog(scripts),
                new ExtensionPackageService());
            ExtensionInstallResult result = installation.Install(package, "Sample Adapter");
            Assert.True(Directory.Exists(result.Record.InstallDirectory));
            Assert.False(File.Exists(marker));
            string installedPayload = Path.Combine(result.Record.InstallDirectory, "payload", "adapter.cmd");
            File.WriteAllText(installedPayload, "corrupted", Encoding.UTF8);
            installation.Install(package, "Sample Adapter");
            Assert.DoesNotContain("corrupted", File.ReadAllText(installedPayload, Encoding.UTF8), StringComparison.Ordinal);
            Assert.Throws<InvalidDataException>(() =>
                installation.Install(package, "Sample Adapter", expectedSha256: new string('0', 64)));
            Assert.True(installation.Remove("sample.adapter"));
            Assert.True(File.Exists(externalState));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ImportsUserscriptThroughExistingCatalog()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string package = Path.Combine(root, "script.ppx");
            using (ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "manifest.json", Manifest("sample.demo", "userscript", "payload/main.user.js"));
                WriteEntry(archive, "payload/main.user.js", """
                    // ==UserScript==
                    // @name Sample Demo
                    // @namespace https://example.com/sample
                    // @version 1.0
                    // @author Sample
                    // ==/UserScript==
                    // PACKING_PROOF_RECORDERS
                    // PACKING_PROOF_UPDATE_URLS
                    """);
            }
            string scripts = Path.Combine(root, "scripts");
            var catalog = new UserscriptCatalog(scripts);
            var installation = new ExtensionInstallationService(
                Path.Combine(root, "extensions"),
                catalog,
                new ExtensionPackageService());
            ExtensionInstallResult result = installation.Install(package, "Sample Demo");
            Assert.Equal("userscript", result.Record.Type);
            Assert.Single(catalog.GetCustomScripts());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void RejectsPackageThatRequiresANewerPackingProofVersion()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string package = Path.Combine(root, "future.ppx");
            using (ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "manifest.json", Manifest(
                    "sample.future",
                    "external-adapter",
                    "payload/adapter.exe",
                    "999.0.0"));
                WriteEntry(archive, "payload/adapter.exe", "future");
            }
            var installation = new ExtensionInstallationService(
                Path.Combine(root, "extensions"),
                new UserscriptCatalog(Path.Combine(root, "scripts")),
                new ExtensionPackageService());
            Assert.Throws<InvalidDataException>(() => installation.Install(package, "Future Adapter"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string Manifest(
        string id,
        string type,
        string payload,
        string minimumVersion = "0.0.62") => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        packageFormatVersion = 1,
        id,
        version = "1.0.0",
        type,
        installation = type == "userscript"
            ? (object)new { mode = "userscript-import", payloadPath = payload }
            : new { mode = "manual-external", suggestedPath = payload },
        compatibility = new
        {
            minPackingProofVersion = minimumVersion,
            platforms = type == "userscript"
                ? (object)new { userscript = new[] { "any" } }
                : new { windows = new[] { "any" } }
        },
        access = new
        {
            packingProofPermissions = Array.Empty<string>(),
            packingProofCapabilities = Array.Empty<string>(),
            systemAccess = Array.Empty<string>()
        }
    });

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "packingproof-extension-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
