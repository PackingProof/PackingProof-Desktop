using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.UpdateCore;
using ExpressPackingMonitoring.Logging;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services.Extensions;

internal sealed class InstalledExtensionRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string Type { get; set; } = "";
    public string ManagedItemId { get; set; } = "";
    public string InstallDirectory { get; set; } = "";
    public DateTimeOffset InstalledAt { get; set; }
    public string LauncherPath { get; set; } = "";
}

internal sealed record ExtensionInstallResult(
    InstalledExtensionRecord Record,
    IReadOnlyList<string> Warnings);

internal sealed class ExtensionInstallationService : IExtensionInstallationTarget
{
    private const string RegistryFileName = "registry.json";
    /// <summary>启动外部适配器时注入的环境变量：扩展 id 与它独占的数据目录（见 docs\EXTENSION_API_V1.md）。</summary>
    internal const string ExtensionIdVariableName = "PACKINGPROOF_EXTENSION_ID";
    internal const string ExtensionDataDirectoryVariableName = "PACKINGPROOF_EXTENSION_DATA_DIR";
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

    public IReadOnlyList<InstalledExtensionRecord> GetInstalled()
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

    public ExtensionInstallResult Install(
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
                Name = displayName.Length > 0
                    ? displayName
                    : inspection.Manifest.Name.Length > 0 ? inspection.Manifest.Name : imported.Name,
                Summary = inspection.Manifest.Summary,
                Description = inspection.Manifest.Description,
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
            List<InstalledExtensionRecord> registry = LoadRegistry();
            InstalledExtensionRecord? previous = registry.FirstOrDefault(value =>
                string.Equals(value.Id, inspection.Manifest.Id, StringComparison.Ordinal));
            Directory.CreateDirectory(Path.GetDirectoryName(targetDirectory)!);
            PublishDirectory(stagingDirectory, targetDirectory);
            var record = new InstalledExtensionRecord
            {
                Id = inspection.Manifest.Id,
                Name = displayName.Length > 0
                    ? displayName
                    : inspection.Manifest.Name.Length > 0 ? inspection.Manifest.Name : inspection.Manifest.Id,
                Summary = inspection.Manifest.Summary,
                Description = inspection.Manifest.Description,
                Version = inspection.Manifest.Version,
                Type = inspection.Manifest.Type,
                InstallDirectory = targetDirectory,
                LauncherPath = inspection.Manifest.Launcher?.Path ?? "",
                InstalledAt = DateTimeOffset.UtcNow
            };
            ReplaceRecord(registry, record);
            SaveRegistry(registry);
            // 更新换到了新的版本目录：开机自启项如果指着旧目录里的启动器，赶紧改到新目录，
            // 否则下次开机还是拉起旧版本（旧版本不会知道新路径）。
            if (previous is { InstallDirectory.Length: > 0 }
                && record.LauncherPath.Length > 0
                && !string.Equals(previous.InstallDirectory, targetDirectory, StringComparison.OrdinalIgnoreCase))
            {
                ExtensionStartupRetarget.TryRetarget(
                    previous.InstallDirectory,
                    targetDirectory,
                    record.LauncherPath,
                    record.Version);
            }
            return new ExtensionInstallResult(record, inspection.Warnings);
        }
    }

    internal bool TryLaunch(InstalledExtensionRecord record, out string error)
    {
        error = "";
        if (record.Type != "external-adapter" || string.IsNullOrWhiteSpace(record.LauncherPath))
        {
            error = "此扩展没有声明可启动文件";
            return false;
        }
        string root = Path.GetFullPath(record.InstallDirectory) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(Path.Combine(record.InstallDirectory, record.LauncherPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(target))
        {
            error = "扩展启动文件不存在";
            return false;
        }
        // 数据目录和扩展包目录分开：包目录会随版本换掉、以后还会清理旧版本，配置写进去就会"丢"。
        string dataDirectory;
        try
        {
            dataDirectory = ResolveDataDirectory(AppPaths.ExtensionDataDir, record.Id);
            Directory.CreateDirectory(dataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error = $"扩展数据目录不可用：{ex.Message}";
            return false;
        }
        try
        {
            Process.Start(CreateLaunchStartInfo(
                target,
                SharedRuntimeLayout.GetDotNetRootForChildProcess(
                    AppContext.BaseDirectory,
                    Environment.GetEnvironmentVariable("DOTNET_ROOT")),
                record.Id,
                dataDirectory));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 外部适配器的数据目录：用户数据目录下按扩展 id 分目录，跨版本、跨更新都不变。
    /// id 来自 manifest（打包时已校验为 <c>[a-z0-9][a-z0-9.-]*[a-z0-9]</c>），这里再挡一次
    /// 路径分隔符与 ".."，避免拼出数据根目录之外的位置。
    /// </summary>
    internal static string ResolveDataDirectory(string dataRoot, string extensionId)
    {
        string id = (extensionId ?? "").Trim();
        if (id.Length == 0
            || id.Contains('\\')
            || id.Contains('/')
            || id.Contains("..", StringComparison.Ordinal)
            || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException($"扩展 ID 不能用作数据目录名：{extensionId}");
        }
        return Path.Combine(Path.GetFullPath(dataRoot), id);
    }

    /// <summary>
    /// 扩展进程的启动信息。桌面端把自己的共享运行时（DOTNET_ROOT）交给扩展，市场里那些框架依赖的
    /// .NET 扩展就不用自带运行时；没有布局时不给这个变量，扩展按系统 .NET 启动。
    /// .cmd/.bat 启动器经 cmd.exe 走，环境变量同样能传进去。
    /// </summary>
    internal static ProcessStartInfo CreateLaunchStartInfo(
        string target,
        string dotNetRoot,
        string extensionId = "",
        string extensionDataDirectory = "")
    {
        string extension = Path.GetExtension(target);
        bool shellScript = extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = shellScript ? "cmd.exe" : target,
            WorkingDirectory = Path.GetDirectoryName(target) ?? "",
            UseShellExecute = false
        };
        if (shellScript)
        {
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(target);
        }
        if (!string.IsNullOrWhiteSpace(dotNetRoot)) startInfo.Environment["DOTNET_ROOT"] = dotNetRoot;
        // 数据目录约定：扩展从这里读写自己的配置/缓存/日志，不要写进包目录（见 docs\EXTENSION_API_V1.md）。
        if (!string.IsNullOrWhiteSpace(extensionId))
            startInfo.Environment[ExtensionIdVariableName] = extensionId;
        if (!string.IsNullOrWhiteSpace(extensionDataDirectory))
            startInfo.Environment[ExtensionDataDirectoryVariableName] = extensionDataDirectory;
        return startInfo;
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
            throw new InvalidDataException(BrandText.Apply(
                $"此扩展需要 PackingProof {minimumVersion} 或更高版本"));
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

/// <summary>
/// 扩展更新换了版本目录之后，把指向旧目录的开机自启命令改到新目录。
/// Core 是跨平台工程、不允许碰注册表，具体实现由 Windows 桌面层在启动时挂进来
/// （见 <c>ExtensionStartupEntryRewriter</c>）。
/// </summary>
internal static class ExtensionStartupRetarget
{
    /// <summary>参数：旧安装目录、新安装目录、启动器相对路径；返回改过的注册表值名。</summary>
    internal static Func<string, string, string, IReadOnlyList<string>>? Hook { get; set; }

    internal static void TryRetarget(
        string previousInstallDirectory,
        string newInstallDirectory,
        string launcherRelativePath,
        string newVersion)
    {
        if (Hook == null
            || string.IsNullOrWhiteSpace(previousInstallDirectory)
            || string.IsNullOrWhiteSpace(newInstallDirectory)
            || string.IsNullOrWhiteSpace(launcherRelativePath))
        {
            return;
        }

        try
        {
            IReadOnlyList<string> changed = Hook(previousInstallDirectory, newInstallDirectory, launcherRelativePath);
            if (changed.Count > 0)
            {
                RuntimeLog.Info(
                    "Extensions",
                    $"已把开机自启项 {string.Join("、", changed)} 从旧版本目录改到 {newVersion}");
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            RuntimeLog.Warn("Extensions", $"改写扩展开机自启项失败：{exception.Message}");
        }
    }
}
