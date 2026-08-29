using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExpressPackingMonitoring.Services.Extensions;

internal sealed class ExtensionPackageManifest
{
    public int SchemaVersion { get; set; }
    public string Format { get; set; } = "";
    public int PackageFormatVersion { get; set; }
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Type { get; set; } = "";
    public ExtensionPackageInstallation Installation { get; set; } = new();
    public ExtensionMarketCompatibility Compatibility { get; set; } = new();

    internal string PayloadPath => Installation.PayloadPath.Length > 0
        ? Installation.PayloadPath
        : Installation.SuggestedPath;
}

internal sealed class ExtensionPackageInstallation
{
    public string Mode { get; set; } = "";
    public string PayloadPath { get; set; } = "";
    public string SuggestedPath { get; set; } = "";
}

internal sealed record ExtensionPackageInspection(
    ExtensionPackageManifest Manifest,
    long PackageSize,
    IReadOnlyList<string> Warnings);

internal sealed class ExtensionPackageService
{
    internal const long MaxPackageBytes = 200L * 1024 * 1024;
    internal const long MaxExpandedBytes = 500L * 1024 * 1024;
    internal const int MaxEntryCount = 2000;
    private const double MaxCompressionRatio = 200;
    private static readonly Regex IdentifierPattern = new("^[a-z0-9][a-z0-9.-]{1,126}[a-z0-9]$", RegexOptions.CultureInvariant);
    private static readonly Regex SemverPattern = new("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant);
    private static readonly Regex WindowsReservedName = new("^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal ExtensionPackageInspection Inspect(string packagePath)
    {
        string extension = Path.GetExtension(packagePath);
        if (!extension.Equals(".ppext", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".partial", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("仅支持 PackingProof .ppext 扩展包");

        FileInfo package = new(packagePath);
        if (!package.Exists || package.Length <= 0 || package.Length > MaxPackageBytes)
            throw new InvalidDataException("PPEXT 文件不存在或大小超过 200 MB");

        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        Dictionary<string, ZipArchiveEntry> entries = ValidateEntries(archive);
        if (!entries.TryGetValue("manifest.json", out ZipArchiveEntry? manifestEntry))
            throw new InvalidDataException("PPEXT 根目录缺少 manifest.json");
        if (manifestEntry.Length > 64 * 1024) throw new InvalidDataException("manifest.json 不能超过 64 KB");
        ExtensionPackageManifest manifest;
        using (Stream stream = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<ExtensionPackageManifest>(stream, JsonOptions)
                ?? throw new InvalidDataException("manifest.json 格式无效");
        }
        ValidateManifest(manifest, entries);
        var warnings = new List<string>();
        if (manifest.Type == "external-adapter" && !entries.ContainsKey("README.md"))
            warnings.Add("外部适配器未包含 README.md");
        return new ExtensionPackageInspection(manifest, package.Length, warnings);
    }

    internal ExtensionPackageInspection ExtractToOwnedDirectory(string packagePath, string destinationDirectory)
    {
        ExtensionPackageInspection inspection = Inspect(packagePath);
        if (Directory.Exists(destinationDirectory))
            throw new IOException("扩展 staging 目录已存在");
        Directory.CreateDirectory(destinationDirectory);
        string root = Path.GetFullPath(destinationDirectory) + Path.DirectorySeparatorChar;
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(packagePath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string normalized = NormalizeEntryPath(entry.FullName);
                string targetPath = Path.GetFullPath(Path.Combine(destinationDirectory, normalized.Replace('/', Path.DirectorySeparatorChar)));
                if (!targetPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"PPEXT 路径越界：{entry.FullName}");
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(targetPath);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                using Stream source = entry.Open();
                using FileStream target = new(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                source.CopyTo(target);
                if (target.Length != entry.Length) throw new InvalidDataException($"PPEXT 条目大小异常：{entry.FullName}");
            }
            return inspection;
        }
        catch
        {
            TryDeleteOwnedDirectory(destinationDirectory);
            throw;
        }
    }

    private static Dictionary<string, ZipArchiveEntry> ValidateEntries(ZipArchive archive)
    {
        if (archive.Entries.Count > MaxEntryCount) throw new InvalidDataException("PPEXT 文件数量超过 2000");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string normalized = NormalizeEntryPath(entry.FullName);
            foreach (string segment in normalized.Split('/'))
            {
                if (segment.EndsWith('.') || segment.EndsWith(' ') || WindowsReservedName.IsMatch(segment))
                    throw new InvalidDataException($"PPEXT 包含 Windows 不支持的文件名：{entry.FullName}");
            }
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"PPEXT 不允许符号链接：{entry.FullName}");
            if (!entries.TryAdd(normalized, entry))
                throw new InvalidDataException($"PPEXT 存在重复或大小写冲突路径：{entry.FullName}");
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaxExpandedBytes) throw new InvalidDataException("PPEXT 展开大小超过 500 MB");
            double ratio = entry.CompressedLength == 0
                ? entry.Length == 0 ? 1 : double.PositiveInfinity
                : (double)entry.Length / entry.CompressedLength;
            if (ratio > MaxCompressionRatio) throw new InvalidDataException($"PPEXT 压缩比超过 200:1：{entry.FullName}");
        }
        return entries;
    }

    private static void ValidateManifest(
        ExtensionPackageManifest manifest,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        if (manifest.SchemaVersion != 1
            || manifest.Format != "packingproof-extension"
            || manifest.PackageFormatVersion != 1)
            throw new InvalidDataException("PPEXT manifest 格式或版本不受支持");
        if (!IdentifierPattern.IsMatch(manifest.Id) || !SemverPattern.IsMatch(manifest.Version))
            throw new InvalidDataException("PPEXT 扩展 ID 或版本格式无效");
        if (!SemverPattern.IsMatch(manifest.Compatibility.MinPackingProofVersion))
            throw new InvalidDataException("PPEXT 最低 PackingProof 版本格式无效");
        if (manifest.Type is not ("userscript" or "external-adapter"))
            throw new InvalidDataException("PPEXT 扩展类型不受支持");
        string payloadPath = NormalizeEntryPath(manifest.PayloadPath);
        if (!payloadPath.StartsWith("payload/", StringComparison.Ordinal) || !entries.ContainsKey(payloadPath))
            throw new InvalidDataException("PPEXT payload 不存在或不在 payload/ 目录");
        if (manifest.Type == "userscript")
        {
            if (manifest.Installation.Mode != "userscript-import" || !payloadPath.EndsWith(".user.js", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("userscript 必须使用 .user.js payload");
            if (entries[payloadPath].Length > 1024 * 1024) throw new InvalidDataException("油猴脚本不能超过 1 MB");
        }
        else if (manifest.Installation.Mode != "manual-external")
        {
            throw new InvalidDataException("external-adapter 必须使用手动外部程序安装方式");
        }
    }

    private static string NormalizeEntryPath(string value)
    {
        string normalized = value.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0
            || normalized.StartsWith('/')
            || Regex.IsMatch(normalized, "^[A-Za-z]:")
            || normalized.Contains(':')
            || normalized.Split('/').Any(part => part.Length == 0 || part == ".." || part == "."))
        {
            throw new InvalidDataException($"PPEXT 包含不安全路径：{value}");
        }
        return normalized;
    }

    internal static void TryDeleteOwnedDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
