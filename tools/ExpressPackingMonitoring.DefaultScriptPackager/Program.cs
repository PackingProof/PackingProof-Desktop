using ExpressPackingMonitoring.Services.Extensions;
using System.Security.Cryptography;
using System.Text.Json;

return await ProgramEntry.RunAsync(args);

internal static class ProgramEntry
{
    private static readonly JsonSerializerOptions OutputJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            Options options = Options.Parse(args);
            DefaultScriptExtensionManifest source = DefaultScriptExtensionManifest.Load(options.ManifestPath);
            string packageDirectory = Path.Combine(
                Path.GetDirectoryName(options.OutputManifestPath) ?? "",
                DefaultScriptExtensionManifest.BundledPackageDirectoryName);
            Directory.CreateDirectory(packageDirectory);
            foreach (string oldPackage in Directory.EnumerateFiles(packageDirectory, "*.ppext"))
                File.Delete(oldPackage);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var market = new ExtensionMarketClient(
                http,
                Path.Combine(options.CacheDirectory, "_downloads"));
            ExtensionMarketSession session = await market.LoadCatalogAsync();
            Dictionary<string, ExtensionMarketCatalogItem> catalog = session.Catalog.Extensions
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            var packages = new List<DefaultScriptExtensionBundledPackage>();
            long totalBytes = 0;
            foreach (string extensionId in source.ScriptIds)
            {
                if (!catalog.TryGetValue(extensionId, out ExtensionMarketCatalogItem? item)
                    || !string.Equals(item.Type, "userscript", StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"{extensionId}: 市场缺少脚本条目");
                }

                ExtensionMarketDetails details = await market.LoadDetailsAsync(session, item);
                if (!string.Equals(details.Extension.Id, extensionId, StringComparison.Ordinal)
                    || !string.Equals(details.Extension.Type, "userscript", StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"{extensionId}: 市场身份校验失败");
                }

                ExtensionMarketRelease release = ExtensionMarketVersionPolicy.SelectInstallableRelease(
                    details,
                    item.LatestVersion ?? "")
                    ?? throw new InvalidDataException($"{extensionId}: 市场没有可安装版本");
                if (!ExtensionMarketVersionPolicy.IsAppCompatible(
                        release.Compatibility.MinPackingProofVersion,
                        options.AppVersion))
                {
                    throw new InvalidDataException($"{extensionId}: 需要更高版本的 PackingProof");
                }

                string fileName = ResolvePackageFileName(release, extensionId);
                string versionDirectory = Path.Combine(
                    options.CacheDirectory,
                    extensionId,
                    release.Version);
                Directory.CreateDirectory(versionDirectory);
                string cachedPath = Path.Combine(versionDirectory, fileName);
                if (!ReuseCache(cachedPath, release))
                {
                    string downloadedPath = await market.DownloadPackageAsync(release);
                    try
                    {
                        File.Copy(downloadedPath, cachedPath, overwrite: true);
                    }
                    finally
                    {
                        TryDelete(downloadedPath);
                    }
                }
                else
                {
                    Console.WriteLine($"复用默认脚本缓存：{extensionId} {release.Version}");
                }

                VerifyPackage(cachedPath, extensionId, release);
                long size = new FileInfo(cachedPath).Length;
                totalBytes = checked(totalBytes + size);
                if (totalBytes > DefaultScriptExtensionManifest.MaxBundledPackageBytes)
                    throw new InvalidDataException("默认脚本离线包超过 1 MiB 硬门限");

                File.Copy(cachedPath, Path.Combine(packageDirectory, fileName), overwrite: true);
                packages.Add(new DefaultScriptExtensionBundledPackage
                {
                    Id = extensionId,
                    Version = release.Version,
                    FileName = fileName,
                    Size = size,
                    Sha256 = release.Sha256
                });
            }

            Directory.CreateDirectory(Path.GetDirectoryName(options.OutputManifestPath) ?? "");
            File.WriteAllText(
                options.OutputManifestPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    scriptIds = source.ScriptIds,
                    packages
                }, OutputJsonOptions));
            Console.WriteLine(
                $"默认脚本离线包完成：{packages.Count} 个，{totalBytes} 字节 / {DefaultScriptExtensionManifest.MaxBundledPackageBytes} 字节");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"默认脚本打包失败：{ex.Message}");
            return 1;
        }
    }

    private static string ResolvePackageFileName(ExtensionMarketRelease release, string extensionId)
    {
        ExtensionMarketDownload download = release.Downloads.InPreferredOrder().FirstOrDefault()
            ?? throw new InvalidDataException($"{extensionId}: 市场没有下载地址");
        if (!Uri.TryCreate(download.Url, UriKind.Absolute, out Uri? uri))
            throw new InvalidDataException($"{extensionId}: 下载地址无效");
        string fileName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (!fileName.EndsWith(".ppext", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{extensionId}: 离线包文件名无效");
        }
        return fileName;
    }

    private static bool ReuseCache(string path, ExtensionMarketRelease release)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != release.Size)
            return false;
        string hash = ComputeSha256(path);
        return string.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void VerifyPackage(
        string packagePath,
        string extensionId,
        ExtensionMarketRelease release)
    {
        if (new FileInfo(packagePath).Length != release.Size
            || !string.Equals(
                ComputeSha256(packagePath),
                release.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{extensionId}: 缓存离线包完整性校验失败");
        }

        ExtensionPackageInspection inspection = new ExtensionPackageService().Inspect(packagePath);
        if (!string.Equals(inspection.Manifest.Id, extensionId, StringComparison.Ordinal)
            || !string.Equals(inspection.Manifest.Type, "userscript", StringComparison.Ordinal)
            || !string.Equals(inspection.Manifest.Version, release.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{extensionId}: 离线包清单与市场记录不一致");
        }
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record Options(
        string ManifestPath,
        string OutputManifestPath,
        string CacheDirectory,
        string AppVersion)
    {
        internal static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int index = 0; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("参数必须使用 --name value 形式");
                values[args[index][2..]] = args[index + 1];
            }

            return new Options(
                RequirePath(values, "manifest"),
                RequirePath(values, "output-manifest"),
                RequirePath(values, "cache-dir"),
                RequireValue(values, "app-version"));
        }

        private static string RequirePath(IReadOnlyDictionary<string, string> values, string name) =>
            values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
                ? Path.GetFullPath(value)
                : throw new ArgumentException($"缺少参数 --{name}");

        private static string RequireValue(IReadOnlyDictionary<string, string> values, string name) =>
            values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ArgumentException($"缺少参数 --{name}");
    }
}
