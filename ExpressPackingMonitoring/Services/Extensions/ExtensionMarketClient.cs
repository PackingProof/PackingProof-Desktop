using ExpressPackingMonitoring.Config;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services.Extensions;

internal sealed class ExtensionMarketClient
{
    internal const string GiteeRegistryBase = "https://gitee.com/PackingProof/PackingProof-Extensions/raw/main/registry/";
    internal const string GithubRegistryBase = "https://github.com/PackingProof/PackingProof-Extensions/raw/refs/heads/main/registry/";
    private const string CatalogFileName = "catalog.v1.json";
    private const string SignatureFileName = "catalog.v1.sig";
    private const string PublicKeyId = "ffa7e958e397fdce";
    private const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEr5W8xBTX+9oC+nf81m5h1Ta1zVbM
        Z8i6KV8y4vWJjD6GuN0ZfXsg/DA2b5CMGShrHIVwokpgylS9YjzgkzP0kw==
        -----END PUBLIC KEY-----
        """;
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _client;
    private readonly string _cacheDirectory;

    internal ExtensionMarketClient()
        : this(SharedClient, AppPaths.ExtensionMarketCacheDir)
    {
    }

    internal ExtensionMarketClient(HttpClient client, string cacheDirectory)
    {
        _client = client;
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
    }

    internal async Task<ExtensionMarketSession> LoadCatalogAsync(CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        foreach (string registryBase in RegistryBases())
        {
            try
            {
                byte[] catalogBytes = await DownloadBytesAsync(registryBase + CatalogFileName, 5 * 1024 * 1024, cancellationToken);
                byte[] signatureBytes = await DownloadBytesAsync(registryBase + SignatureFileName, 64 * 1024, cancellationToken);
                VerifyCatalog(catalogBytes, signatureBytes);
                ExtensionMarketCatalog catalog = ParseCatalog(catalogBytes);
                await WriteCacheAsync(CatalogFileName, catalogBytes, cancellationToken);
                await WriteCacheAsync(SignatureFileName, signatureBytes, cancellationToken);
                return new ExtensionMarketSession(catalog, false, RegistryBases(registryBase));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or CryptographicException or TaskCanceledException)
            {
                errors.Add(ex.Message);
            }
        }

        try
        {
            byte[] catalogBytes = await File.ReadAllBytesAsync(Path.Combine(_cacheDirectory, CatalogFileName), cancellationToken);
            byte[] signatureBytes = await File.ReadAllBytesAsync(Path.Combine(_cacheDirectory, SignatureFileName), cancellationToken);
            VerifyCatalog(catalogBytes, signatureBytes);
            return new ExtensionMarketSession(ParseCatalog(catalogBytes), true, RegistryBases());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or CryptographicException)
        {
            errors.Add(ex.Message);
            throw new InvalidDataException($"无法读取可信扩展市场：{string.Join("；", errors)}", ex);
        }
    }

    internal async Task<ExtensionMarketDetails> LoadDetailsAsync(
        ExtensionMarketSession session,
        ExtensionMarketCatalogItem item,
        CancellationToken cancellationToken = default)
    {
        if (item.DetailsSha256.Length != 64 || !item.DetailsSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("扩展详情缺少可信 SHA-256");
        string cacheName = $"details-{item.Id}.json";
        foreach (string registryBase in session.RegistryBases)
        {
            try
            {
                byte[] bytes = await DownloadBytesAsync(registryBase + item.Details, 5 * 1024 * 1024, cancellationToken);
                VerifySha256(bytes, item.DetailsSha256, "扩展详情");
                await WriteCacheAsync(cacheName, bytes, cancellationToken);
                return ParseDetails(bytes);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or TaskCanceledException)
            {
            }
        }
        byte[] cached = await File.ReadAllBytesAsync(Path.Combine(_cacheDirectory, cacheName), cancellationToken);
        VerifySha256(cached, item.DetailsSha256, "缓存扩展详情");
        return ParseDetails(cached);
    }

    internal async Task<string> DownloadPackageAsync(
        ExtensionMarketRelease release,
        IProgress<ExtensionPackageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (release.Size <= 0 || release.Size > ExtensionPackageService.MaxPackageBytes)
            throw new InvalidDataException("扩展包大小无效");
        Directory.CreateDirectory(_cacheDirectory);
        var errors = new List<string>();
        foreach (ExtensionMarketDownload download in release.Downloads.InPreferredOrder())
        {
            string temporaryPath = Path.Combine(_cacheDirectory, $"download-{Guid.NewGuid():N}.partial");
            try
            {
                progress?.Report(new ExtensionPackageProgress($"正在从 {download.Provider} 下载"));
                using HttpResponseMessage response = await _client.GetAsync(download.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
                long received = 0;
                await using (FileStream target = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    byte[] buffer = new byte[81920];
                    while (true)
                    {
                        int read = await source.ReadAsync(buffer, cancellationToken);
                        if (read == 0) break;
                        received += read;
                        if (received > ExtensionPackageService.MaxPackageBytes)
                            throw new InvalidDataException("扩展包超过 200 MB");
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        progress?.Report(new ExtensionPackageProgress("正在下载扩展包", received, release.Size));
                    }
                    await target.FlushAsync(cancellationToken);
                }
                if (received != release.Size) throw new InvalidDataException("扩展包大小与市场记录不一致");
                string digest = await ComputeFileSha256Async(temporaryPath, cancellationToken);
                if (!string.Equals(digest, release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{download.Provider} 扩展包 SHA-256 不匹配");
                return temporaryPath;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
            {
                TryDeleteOwnedFile(temporaryPath);
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
                errors.Add($"{download.Provider}: {ex.Message}");
            }
        }
        throw new InvalidDataException($"所有发布源均无法提供通过校验的扩展包：{string.Join("；", errors)}");
    }

    internal static void VerifyCatalog(byte[] catalogBytes, byte[] signatureBytes)
    {
        ExtensionCatalogSignature signature = JsonSerializer.Deserialize<ExtensionCatalogSignature>(signatureBytes, JsonOptions)
            ?? throw new InvalidDataException("市场签名格式无效");
        if (signature.SchemaVersion != 1
            || signature.Algorithm != "ECDSA-P256-SHA256"
            || signature.KeyId != PublicKeyId)
            throw new InvalidDataException("市场签名算法不受支持");
        VerifySha256(catalogBytes, signature.CatalogSha256, "市场目录");
        using ECDsa verifier = ECDsa.Create();
        verifier.ImportFromPem(PublicKeyPem);
        byte[] rawSignature;
        try
        {
            rawSignature = Convert.FromBase64String(signature.Signature);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("市场签名编码无效", ex);
        }
        if (rawSignature.Length != 64 || !verifier.VerifyData(catalogBytes, rawSignature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException("市场目录签名无效");
    }

    internal static void VerifySha256(byte[] bytes, string expected, string label)
    {
        string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{label} SHA-256 不匹配");
    }

    private async Task<byte[]> DownloadBytesAsync(string url, int limit, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using HttpResponseMessage response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var memory = new MemoryStream();
        byte[] buffer = new byte[32768];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, timeout.Token);
            if (read == 0) break;
            if (memory.Length + read > limit) throw new InvalidDataException("市场元数据超过大小限制");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private async Task WriteCacheAsync(string fileName, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDirectory);
        string target = Path.Combine(_cacheDirectory, fileName);
        string temporary = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, target, true);
        }
        finally
        {
            TryDeleteOwnedFile(temporary);
        }
    }

    private static ExtensionMarketCatalog ParseCatalog(byte[] bytes)
    {
        ExtensionMarketCatalog catalog = JsonSerializer.Deserialize<ExtensionMarketCatalog>(bytes, JsonOptions)
            ?? throw new InvalidDataException("市场目录格式无效");
        if (catalog.SchemaVersion != 1) throw new InvalidDataException("市场目录版本不受支持");
        return catalog;
    }

    private static ExtensionMarketDetails ParseDetails(byte[] bytes) =>
        JsonSerializer.Deserialize<ExtensionMarketDetails>(bytes, JsonOptions)
        ?? throw new InvalidDataException("扩展详情格式无效");

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static IReadOnlyList<string> RegistryBases(string? preferred = null)
    {
        string[] values = { GiteeRegistryBase, GithubRegistryBase };
        return preferred == null
            ? values
            : values.OrderBy(value => value == preferred ? 0 : 1).ToArray();
    }

    private static void TryDeleteOwnedFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
