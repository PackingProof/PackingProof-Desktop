using ExpressPackingMonitoring.Config;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExpressPackingMonitoring.Services;

public sealed class UserscriptDescriptor
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Version { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string Author { get; set; } = "";
    public bool IsOfficial { get; set; }
    public string SourcePath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public List<string> Warnings { get; set; } = new();
    public DateTime ImportedAt { get; set; }
}

internal sealed class UserscriptCatalog
{
    internal const string OfficialId = "official-kdzs";
    private const string RegistryFileName = "registry.json";
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly string _registryPath;
    private List<UserscriptDescriptor> _items;

    internal UserscriptCatalog(string? directory = null)
    {
        _directory = directory ?? AppPaths.UserscriptsDir;
        _registryPath = Path.Combine(_directory, RegistryFileName);
        Directory.CreateDirectory(_directory);
        _items = Load();
    }

    internal IReadOnlyList<UserscriptDescriptor> GetAll(string officialPath)
    {
        lock (_sync)
        {
            _items = Load();
            var official = Inspect(officialPath, OfficialId, true);
            return new[] { official }.Concat(_items.Where(item => !item.IsOfficial)).ToList();
        }
    }

    internal IReadOnlyList<UserscriptDescriptor> GetCustomScripts()
    {
        lock (_sync)
        {
            _items = Load();
            return _items.Where(item => !item.IsOfficial).ToList();
        }
    }

    internal UserscriptDescriptor Import(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new InvalidDataException("脚本文件不存在");
        if (!string.Equals(Path.GetExtension(sourcePath), ".js", StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(sourcePath).EndsWith(".user.js", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 .user.js 油猴脚本文件");

        string source = File.ReadAllText(sourcePath, Encoding.UTF8);
        if (source.Length > 1024 * 1024) throw new InvalidDataException("脚本文件不能超过 1 MB");
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        lock (_sync)
        {
            UserscriptDescriptor? existing = _items.FirstOrDefault(item => item.Sha256 == hash);
            if (existing != null) return existing;
            UserscriptDescriptor descriptor = InspectText(source, "custom-" + hash[..16], false);
            descriptor.FileName = Path.GetFileName(sourcePath);
            descriptor.Sha256 = hash;
            descriptor.ImportedAt = DateTime.UtcNow;
            descriptor.SourcePath = Path.Combine(_directory, descriptor.Id + ".user.js");
            File.WriteAllText(descriptor.SourcePath, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _items.Add(descriptor);
            Save();
            return descriptor;
        }
    }

    internal string GetSourcePath(string id, string officialPath)
    {
        if (string.Equals(id, OfficialId, StringComparison.Ordinal)) return officialPath;
        lock (_sync)
        {
            _items = Load();
            UserscriptDescriptor? item = _items.FirstOrDefault(value => value.Id == id && !value.IsOfficial);
            return item != null && File.Exists(item.SourcePath) ? item.SourcePath : "";
        }
    }

    internal bool Remove(string id)
    {
        lock (_sync)
        {
            UserscriptDescriptor? item = _items.FirstOrDefault(value => value.Id == id && !value.IsOfficial);
            if (item == null) return false;
            _items.Remove(item);
            try { if (File.Exists(item.SourcePath)) File.Delete(item.SourcePath); } catch { }
            Save();
            return true;
        }
    }

    internal static UserscriptDescriptor Inspect(string path, string id, bool official)
    {
        return InspectText(File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "", id, official);
    }

    private static UserscriptDescriptor InspectText(string source, string id, bool official)
    {
        string Meta(string key) => Regex.Match(source, $@"^//\s*@{key}(?:\s+|\t+)(?<value>[^\r\n]+)", RegexOptions.Multiline).Groups["value"].Value.Trim();
        var warnings = new List<string>();
        string version = Meta("version");
        if (!Regex.IsMatch(version, @"^\d+\.\d+$")) warnings.Add("源版本应为 X.Y 格式");
        if (!source.Contains("PACKING_PROOF_RECORDERS", StringComparison.Ordinal)) warnings.Add("缺少录像设备注入占位符");
        if (!source.Contains("PACKING_PROOF_UPDATE_URLS", StringComparison.Ordinal)) warnings.Add("缺少自动更新地址占位符");
        if (Regex.IsMatch(source, @"(?i)(powershell|cmd\.exe|ffmpeg|sqlite|\.db)") ) warnings.Add("脚本包含需要人工确认的高风险文本");
        return new UserscriptDescriptor
        {
            Id = id, Name = Meta("name"), Namespace = Meta("namespace"), Version = version,
            Author = Meta("author"), IsOfficial = official, Warnings = warnings
        };
    }

    private List<UserscriptDescriptor> Load()
    {
        try { return JsonSerializer.Deserialize<List<UserscriptDescriptor>>(File.ReadAllText(_registryPath)) ?? new(); }
        catch { return new(); }
    }

    private void Save()
    {
        string temporary = _registryPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(true));
        File.Move(temporary, _registryPath, true);
    }
}
