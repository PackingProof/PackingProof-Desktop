using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services;

internal sealed record BackupPairingToken(
    string TokenId,
    string Secret,
    DateTimeOffset ExpiresAt);

internal sealed class BackupPairingTokenService
{
    private sealed class TokenState
    {
        public required string Secret { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
    }

    private sealed class StoredCredential
    {
        public string DeviceId { get; set; } = "";
        public string DeviceKind { get; set; } = "pc";
        public string CipherText { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Tag { get; set; } = "";
    }

    private readonly string _path;
    private readonly byte[] _encryptionKey;
    private readonly object _gate = new();
    private readonly Dictionary<string, TokenState> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Credential, string DeviceKind)> _deviceCredentials =
        new(StringComparer.OrdinalIgnoreCase);

    internal BackupPairingTokenService(string stateDirectory, string hostPairingKey)
    {
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, "backup-device-credentials.json");
        _encryptionKey = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"packingproof-device-credential-store-v1\n{hostPairingKey.Trim()}"));
        Load();
    }

    internal BackupPairingToken CreateToken(TimeSpan? lifetime = null)
    {
        lock (_gate)
        {
            RemoveExpiredTokens();
            string tokenId = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            DateTimeOffset expiresAt = DateTimeOffset.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(2));
            _tokens[tokenId] = new TokenState { Secret = secret, ExpiresAt = expiresAt };
            return new BackupPairingToken(tokenId, secret, expiresAt);
        }
    }

    internal bool TryGetTokenCredential(string tokenId, out string credential)
    {
        lock (_gate)
        {
            RemoveExpiredTokens();
            if (_tokens.TryGetValue(tokenId?.Trim() ?? "", out TokenState? token))
            {
                credential = token.Secret;
                return true;
            }
            credential = "";
            return false;
        }
    }

    internal bool TryClaim(
        string tokenId,
        string deviceId,
        string deviceKind,
        out string credential)
    {
        lock (_gate)
        {
            RemoveExpiredTokens();
            string normalizedTokenId = tokenId?.Trim() ?? "";
            string normalizedDeviceId = deviceId?.Trim() ?? "";
            string normalizedDeviceKind = string.Equals(
                deviceKind,
                "mobile",
                StringComparison.OrdinalIgnoreCase)
                    ? "mobile"
                    : "pc";
            if (normalizedDeviceId.Length == 0
                || !_tokens.Remove(normalizedTokenId, out TokenState? token))
            {
                credential = "";
                return false;
            }
            credential = token.Secret;
            _deviceCredentials[normalizedDeviceId] = (credential, normalizedDeviceKind);
            Save();
            return true;
        }
    }

    internal bool TryGetDeviceCredential(string deviceId, out string credential)
    {
        lock (_gate)
        {
            bool found = _deviceCredentials.TryGetValue(
                deviceId?.Trim() ?? "",
                out (string Credential, string DeviceKind) value);
            credential = found ? value.Credential : "";
            return found;
        }
    }

    internal bool TryGetDeviceCredential(
        string deviceId,
        out string credential,
        out string deviceKind)
    {
        lock (_gate)
        {
            bool found = _deviceCredentials.TryGetValue(
                deviceId?.Trim() ?? "",
                out (string Credential, string DeviceKind) value);
            credential = found ? value.Credential : "";
            deviceKind = found ? value.DeviceKind : "";
            return found;
        }
    }

    private void RemoveExpiredTokens()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (string key in _tokens.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray())
            _tokens.Remove(key);
    }

    private void Save()
    {
        var items = _deviceCredentials.Select(item =>
            Encrypt(item.Key, item.Value.Credential, item.Value.DeviceKind)).ToArray();
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(items), Encoding.UTF8);
        File.Move(temporary, _path, overwrite: true);
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            StoredCredential[] items = JsonSerializer.Deserialize<StoredCredential[]>(
                File.ReadAllText(_path, Encoding.UTF8)) ?? [];
            foreach (StoredCredential item in items)
            {
                string value = Decrypt(item);
                if (item.DeviceId.Length > 0 && value.Length >= 32)
                    _deviceCredentials[item.DeviceId] = (value, item.DeviceKind);
            }
        }
        catch
        {
            // 损坏的凭据文件不得阻止主机启动；既有设备需重新扫码。
        }
    }

    private StoredCredential Encrypt(string deviceId, string value, string deviceKind)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plain = Encoding.UTF8.GetBytes(value);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        using var aes = new AesGcm(_encryptionKey, tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(deviceId));
        return new StoredCredential
        {
            DeviceId = deviceId,
            DeviceKind = deviceKind,
            CipherText = Convert.ToBase64String(cipher),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag)
        };
    }

    private string Decrypt(StoredCredential item)
    {
        byte[] cipher = Convert.FromBase64String(item.CipherText);
        byte[] plain = new byte[cipher.Length];
        using var aes = new AesGcm(_encryptionKey, 16);
        aes.Decrypt(
            Convert.FromBase64String(item.Nonce),
            cipher,
            Convert.FromBase64String(item.Tag),
            plain,
            Encoding.UTF8.GetBytes(item.DeviceId));
        return Encoding.UTF8.GetString(plain);
    }
}
