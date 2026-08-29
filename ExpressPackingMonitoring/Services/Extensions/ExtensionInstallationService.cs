using ExpressPackingMonitoring.Config;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services.Extensions;

internal sealed class InstalledExtensionRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Type { get; set; } = "";
    public string ManagedItemId { get; set; } = "";
    public string InstallDirectory { get; set; } = "";
    public DateTimeOffset InstalledAt { get; set; }
}

internal sealed record ExtensionInstallResult(
    InstalledExtensionRecord Record,
    IReadOnlyList<string> Warnings);

internal sealed class ExtensionInstallationService
{
    private const string RegistryFileName = "registry.json";
    private static readonly object Sync = new();
    private readonly string _extensionsDirectory;
    private readonly string _registryPath;
    private readonly UserscriptCatalog _userscripts;
    private readonly ExtensionPackageService _packages;

    internal ExtensionInstallationService()
        : this(AppPaths.ExtensionsDir, new UserscriptCatalog(), new ExtensionPackageService())
    {
    }

    internal ExtensionInstallationService(
        string extensionsDirectory,
        UserscriptCatalog userscripts,
        ExtensionPackageService packages)
    {
        _extensionsDirectory = Path.GetFullPath(extensionsDirectory);
        _registryPath = Path.Combine(_extensionsDirectory, RegistryFileName);
        _userscripts = userscripts;
        _packages = packages;
        Directory.CreateDirectory(_extensionsDirectory);
    }

    internal IReadOnlyList<InstalledExtensionRecord> GetInstalled()
    {
        lock (Sync) return LoadRegistry();
    }

    internal string GetInstalledLocationPath(InstalledExtensionRecord record)
    {
        if (record.Type == "userscript")
            return _userscripts.GetSourcePath(record.ManagedItemId);
        return record.Type == "external-adapter" && record.InstallDirectory.Length > 0
            ? Path.Combine(record.InstallDirectory, "manifest.json")
            : "";
    }

    internal ExtensionInstallResult Install(
        string packagePath,
        string displayName,
        string? expectedId = null,
        string? expectedVersion = null,
        string? expectedType = null,
        string? expectedSha256 = null)
    {
        if (expectedSha256 != null)
        {
            using FileStream stream = File.OpenRead(packagePath);
            string actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装前 PPEXT SHA-256 校验失败");
        }
        ExtensionPackageInspection initial = _packages.Inspect(packagePath);
        ValidateExpected(initial.Manifest, expectedId, expectedVersion, expectedType);
        ValidateCompatibility(initial.Manifest.Compatibility.MinPackingProofVersion);
        string stagingDirectory = Path.Combine(_extensionsDirectory, $".staging-{Guid.NewGuid():N}");
        ExtensionPackageInspection extracted = _packages.ExtractToOwnedDirectory(packagePath, stagingDirectory);
        try
        {
            return extracted.Manifest.Type == "userscript"
                ? InstallUserscript(extracted, stagingDirectory, displayName)
                : InstallExternalAdapter(extracted, stagingDirectory, displayName);
        }
        finally
        {
            ExtensionPackageService.TryDeleteOwnedDirectory(stagingDirectory);
        }
    }

    internal bool Remove(string extensionId)
    {
        lock (Sync)
        {
            List<InstalledExtensionRecord> registry = LoadRegistry();
            InstalledExtensionRecord? record = registry.FirstOrDefault(value =>
                string.Equals(value.Id, extensionId, StringComparison.Ordinal));
            if (record == null) return false;
            if (record.Type == "userscript")
            {
                _userscripts.Remove(record.ManagedItemId);
            }
            else
            {
                string packageRoot = Path.GetFullPath(Path.Combine(_extensionsDirectory, "packages", record.Id));
                string allowedRoot = Path.GetFullPath(Path.Combine(_extensionsDirectory, "packages")) + Path.DirectorySeparatorChar;
                if (!packageRoot.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("扩展安装目录越界");
                ExtensionPackageService.TryDeleteOwnedDirectory(packageRoot);
                if (Directory.Exists(packageRoot)) throw new IOException("扩展安装目录删除失败");
            }
            registry.Remove(record);
            SaveRegistry(registry);
            return true;
        }
    }

    private ExtensionInstallResult InstallUserscript(
        ExtensionPackageInspection inspection,
        string stagingDirectory,
        string displayName)
    {
        string scriptPath = ResolveStagedPayload(stagingDirectory, inspection.Manifest.PayloadPath);
        lock (Sync)
        {
            List<InstalledExtensionRecord> registry = LoadRegistry();
            InstalledExtensionRecord? previous = registry.FirstOrDefault(value => value.Id == inspection.Manifest.Id);
            UserscriptDescriptor imported = _userscripts.Import(scriptPath);
            if (previous?.ManagedItemId.Length > 0 && previous.ManagedItemId != imported.Id)
                _userscripts.Remove(previous.ManagedItemId);
            var record = new InstalledExtensionRecord
            {
                Id = inspection.Manifest.Id,
                Name = displayName.Length > 0 ? displayName : imported.Name,
                Version = inspection.Manifest.Version,
                Type = inspection.Manifest.Type,
                ManagedItemId = imported.Id,
                InstalledAt = DateTimeOffset.UtcNow
            };
            ReplaceRecord(registry, record);
            SaveRegistry(registry);
            return new ExtensionInstallResult(record, inspection.Warnings.Concat(imported.Warnings).Distinct().ToList());
        }
    }

    private ExtensionInstallResult InstallExternalAdapter(
        ExtensionPackageInspection inspection,
        string stagingDirectory,
        string displayName)
    {
        string targetDirectory = Path.Combine(
            _extensionsDirectory,
            "packages",
            inspection.Manifest.Id,
            inspection.Manifest.Version);
        lock (Sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetDirectory)!);
            PublishDirectory(stagingDirectory, targetDirectory);
            var record = new InstalledExtensionRecord
            {
                Id = inspection.Manifest.Id,
                Name = displayName.Length > 0 ? displayName : inspection.Manifest.Id,
                Version = inspection.Manifest.Version,
                Type = inspection.Manifest.Type,
                InstallDirectory = targetDirectory,
                InstalledAt = DateTimeOffset.UtcNow
            };
            List<InstalledExtensionRecord> registry = LoadRegistry();
            ReplaceRecord(registry, record);
            SaveRegistry(registry);
            return new ExtensionInstallResult(record, inspection.Warnings);
        }
    }

    private static void ValidateExpected(
        ExtensionPackageManifest manifest,
        string? expectedId,
        string? expectedVersion,
        string? expectedType)
    {
        if (expectedId != null && manifest.Id != expectedId)
            throw new InvalidDataException("PPEXT 扩展 ID 与市场记录不一致");
        if (expectedVersion != null && manifest.Version != expectedVersion)
            throw new InvalidDataException("PPEXT 版本与市场记录不一致");
        if (expectedType != null && manifest.Type != expectedType)
            throw new InvalidDataException("PPEXT 类型与市场记录不一致");
    }

    private static void ValidateCompatibility(string minimumVersion)
    {
        if (!Version.TryParse(minimumVersion, out Version? minimum)
            || !Version.TryParse(NormalizeVersion(AppVersion.Current), out Version? current)
            || current < minimum)
        {
            throw new InvalidDataException($"此扩展需要 PackingProof {minimumVersion} 或更高版本");
        }
    }

    private static string NormalizeVersion(string value)
    {
        string normalized = value.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        string[] parts = normalized.Split('.');
        return parts.Length switch
        {
            1 => normalized + ".0.0",
            2 => normalized + ".0",
            _ => normalized
        };
    }

    private static string ResolveStagedPayload(string stagingDirectory, string payloadPath)
    {
        string root = Path.GetFullPath(stagingDirectory) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(Path.Combine(stagingDirectory, payloadPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(target))
            throw new InvalidDataException("PPEXT payload 路径无效");
        return target;
    }

    private List<InstalledExtensionRecord> LoadRegistry()
    {
        try
        {
            return JsonSerializer.Deserialize<List<InstalledExtensionRecord>>(
                File.ReadAllText(_registryPath, Encoding.UTF8)) ?? new();
        }
        catch (FileNotFoundException)
        {
            return new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private void SaveRegistry(List<InstalledExtensionRecord> registry)
    {
        Directory.CreateDirectory(_extensionsDirectory);
        string temporary = _registryPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(registry, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.Move(temporary, _registryPath, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static void ReplaceRecord(List<InstalledExtensionRecord> registry, InstalledExtensionRecord record)
    {
        registry.RemoveAll(value => value.Id == record.Id);
        registry.Add(record);
    }

    private static void PublishDirectory(string stagingDirectory, string targetDirectory)
    {
        string backupDirectory = targetDirectory + $".replaced-{Guid.NewGuid():N}";
        bool movedExisting = false;
        try
        {
            if (Directory.Exists(targetDirectory))
            {
                Directory.Move(targetDirectory, backupDirectory);
                movedExisting = true;
            }
            Directory.Move(stagingDirectory, targetDirectory);
        }
        catch
        {
            if (!Directory.Exists(targetDirectory) && movedExisting && Directory.Exists(backupDirectory))
                Directory.Move(backupDirectory, targetDirectory);
            throw;
        }
        if (movedExisting) ExtensionPackageService.TryDeleteOwnedDirectory(backupDirectory);
    }
}
