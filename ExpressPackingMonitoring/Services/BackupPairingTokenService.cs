using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services;

internal sealed record BackupDeviceEnrollment(
    string DeviceId,
    string DeviceCredential,
    string DeviceKind,
    DateTimeOffset IssuedAt);

internal sealed class BackupPairingTokenService
{
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
    private readonly Dictionary<string, (string Credential, string DeviceKind)> _deviceCredentials =
        new(StringComparer.OrdinalIgnoreCase);

    internal BackupPairingTokenService(string stateDirectory, string _)
    {
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, "backup-device-credentials.json");
        _encryptionKey = LoadOrCreateRootKey(stateDirectory);
        Load();
    }

    internal BackupDeviceEnrollment Enroll(string deviceId, string deviceKind)
    {
        lock (_gate)
        {
            string normalizedDeviceId = deviceId?.Trim() ?? "";
            string normalizedDeviceKind = string.Equals(
                deviceKind,
                "mobile",
                StringComparison.OrdinalIgnoreCase)
                    ? "mobile"
                    : "pc";
            if (normalizedDeviceId.Length is < 8 or > 128)
                throw new ArgumentException("设备 ID 无效", nameof(deviceId));
            string credential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            _deviceCredentials[normalizedDeviceId] = (credential, normalizedDeviceKind);
            Save();
            return new BackupDeviceEnrollment(
                normalizedDeviceId,
                credential,
                normalizedDeviceKind,
                DateTimeOffset.UtcNow);
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

    private static byte[] LoadOrCreateRootKey(string stateDirectory)
    {
        string path = Path.Combine(stateDirectory, "backup-device-root.key");
        if (File.Exists(path))
        {
            try
            {
                byte[] existing = Convert.FromBase64String(File.ReadAllText(path, Encoding.UTF8).Trim());
                if (existing.Length == 32) return existing;
                RuntimeLog.Warn("BackupPairing", $"备份设备根密钥内容无效，将重新生成 path={path}");
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("BackupPairing", $"备份设备根密钥无法读取，将重新生成 path={path}, error={ex.Message}");
            }
        }
        else
        {
            RuntimeLog.Warn("BackupPairing", $"备份设备根密钥文件不存在，将新建 path={path}");
        }
        byte[] created = RandomNumberGenerator.GetBytes(32);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, Convert.ToBase64String(created), Encoding.UTF8);
        File.Move(temporary, path, overwrite: true);
        return created;
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
        if (!File.Exists(_path))
        {
            RuntimeLog.Warn("BackupPairing", $"备份设备凭据文件不存在，已连接设备需重新配对 path={_path}");
            return;
        }
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
        catch (Exception ex)
        {
            RuntimeLog.Warn("BackupPairing", $"备份设备凭据文件无法加载，已连接设备需重新配对 path={_path}, error={ex.Message}");
        }
        RuntimeLog.Info("BackupPairing", $"备份设备凭据已加载 count={_deviceCredentials.Count}");
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
