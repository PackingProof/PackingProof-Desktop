using ExpressPackingMonitoring.Logging;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ExpressPackingMonitoring.Config;

/// <summary>
/// NodeId 的独立持久锚点。配置文件可能因迁移、回滚或损坏被替换，随机生成的 NodeId
/// 一旦丢失，同一台设备就会在主机上变成多台。这里把首次确定的 ID 单独原子保存，
/// 并在 Windows 完全没有历史 ID 时用系统 MachineGuid 派生可恢复身份。
/// </summary>
internal static class NodeIdentityAnchorStore
{
    private const int CurrentVersion = 1;
    private const string IdentityDirectoryName = "identity";
    private const string IdentityFileName = "node-id.json";
    private static readonly object Sync = new();

    internal static bool Apply(AppConfig config) =>
        Apply(config, AppPaths.UserDataDir, ReadWindowsMachineGuid());

    internal static bool Apply(
        AppConfig config,
        string userDataDirectory,
        string? machineIdentitySeed)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(userDataDirectory))
            throw new ArgumentException("用户数据目录不能为空", nameof(userDataDirectory));

        string path = Path.Combine(
            userDataDirectory,
            IdentityDirectoryName,
            IdentityFileName);
        lock (Sync)
        {
            string? anchoredNodeId = Read(path);
            string? configuredNodeId = NormalizeGuid(config.NodeId)
                ?? NormalizeGuid(config.MobileBackupComputerId);
            string stableNodeId = anchoredNodeId
                ?? configuredNodeId
                ?? CreateNodeIdFromSeed(machineIdentitySeed)
                ?? Guid.NewGuid().ToString("D");

            if (anchoredNodeId != null
                && configuredNodeId != null
                && !string.Equals(
                    anchoredNodeId,
                    configuredNodeId,
                    StringComparison.OrdinalIgnoreCase))
            {
                RuntimeLog.Warn(
                    "Config",
                    $"配置 NodeId 与独立身份锚点不一致，使用锚点恢复设备身份 configured={configuredNodeId}, anchored={anchoredNodeId}");
            }

            bool changed = !string.Equals(
                config.NodeId,
                stableNodeId,
                StringComparison.Ordinal);
            config.NodeId = stableNodeId;
            if (!string.Equals(
                    anchoredNodeId,
                    stableNodeId,
                    StringComparison.OrdinalIgnoreCase))
            {
                Write(path, stableNodeId);
            }
            return changed;
        }
    }

    internal static string? CreateNodeIdFromSeed(string? seed)
    {
        string normalizedSeed = seed?.Trim() ?? "";
        if (normalizedSeed.Length == 0)
            return null;

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            "PackingProof.NodeIdentity:" + normalizedSeed));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, bytes.Length).CopyTo(bytes);
        // RFC 4122 version 5 / variant bits，仍然是标准 GUID 文本。
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes).ToString("D");
    }

    private static string? ReadWindowsMachineGuid()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            return Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
                "MachineGuid",
                null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            IdentityState? state = JsonSerializer.Deserialize<IdentityState>(
                File.ReadAllText(path));
            return state is { Version: CurrentVersion }
                ? NormalizeGuid(state.NodeId)
                : null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(
                "Config",
                $"独立 NodeId 锚点读取失败，将按历史配置恢复：{ex.Message}");
            return null;
        }
    }

    private static void Write(string path, string nodeId)
    {
        string? directory = Path.GetDirectoryName(path);
        string temporaryPath =
            $"{path}.{Environment.ProcessId:X}.{Guid.NewGuid():N}.tmp";
        try
        {
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(new IdentityState
                {
                    Version = CurrentVersion,
                    NodeId = nodeId
                }));
            File.Move(temporaryPath, path, true);
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
            }
            RuntimeLog.Warn(
                "Config",
                $"独立 NodeId 锚点写入失败，设备身份仍在本进程内保持：{ex.Message}");
        }
    }

    private static string? NormalizeGuid(string? value) =>
        Guid.TryParse(value, out Guid parsed) && parsed != Guid.Empty
            ? parsed.ToString("D")
            : null;

    private sealed class IdentityState
    {
        public int Version { get; set; }
        public string NodeId { get; set; } = "";
    }
}
