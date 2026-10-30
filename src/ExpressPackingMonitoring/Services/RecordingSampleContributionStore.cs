using System.IO;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services;

internal sealed class RecordingSampleContributionState
{
    public int SchemaVersion { get; set; } = RecordingSampleContributionStore.CurrentSchemaVersion;
    public string DeviceFingerprint { get; set; } = "";
    public List<string> SampledSourceFingerprints { get; set; } = [];
}

/// <summary>
/// 保存匿名贡献设备指纹和已采录像指纹。设备指纹由现有 NodeId 派生，
/// 不是硬件指纹，也不直接暴露局域网节点 ID。
/// </summary>
internal sealed class RecordingSampleContributionStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaxTrackedSources = 100_000;
    internal const string FileName = "recording-sample-contribution.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly string _deviceFingerprint;

    internal RecordingSampleContributionStore(string userDataDir, string nodeId)
    {
        _path = Path.Combine(
            userDataDir ?? throw new ArgumentNullException(nameof(userDataDir)),
            FileName);
        _deviceFingerprint = CreateDeviceFingerprint(nodeId);
    }

    internal RecordingSampleContributionState Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                RecordingSampleContributionState? state =
                    JsonSerializer.Deserialize<RecordingSampleContributionState>(
                        File.ReadAllText(_path),
                        JsonOptions);
                if (state != null
                    && state.SchemaVersion == CurrentSchemaVersion
                    && string.Equals(
                        state.DeviceFingerprint,
                        _deviceFingerprint,
                        StringComparison.Ordinal))
                {
                    state.SampledSourceFingerprints ??= [];
                    state.SampledSourceFingerprints = state.SampledSourceFingerprints
                        .Where(IsValidFingerprint)
                        .Distinct(StringComparer.Ordinal)
                        .TakeLast(MaxTrackedSources)
                        .ToList();
                    return state;
                }
            }
        }
        catch
        {
            // 损坏的本地贡献状态不影响重新生成样本包。
        }

        return new RecordingSampleContributionState
        {
            DeviceFingerprint = _deviceFingerprint
        };
    }

    internal void Save(RecordingSampleContributionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.SchemaVersion = CurrentSchemaVersion;
        state.DeviceFingerprint = _deviceFingerprint;
        state.SampledSourceFingerprints ??= [];
        state.SampledSourceFingerprints = state.SampledSourceFingerprints
            .Where(IsValidFingerprint)
            .Distinct(StringComparer.Ordinal)
            .TakeLast(MaxTrackedSources)
            .ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporaryPath =
            $"{_path}.{Environment.ProcessId:X}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    internal static string CreateSourceFingerprint(
        string deviceFingerprint,
        long sourceId,
        DateTime startTime,
        double durationSeconds)
    {
        string raw = string.Join(
            "\n",
            deviceFingerprint,
            sourceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            startTime.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            durationSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant()[..32];
    }

    internal static string CreateDeviceFingerprint(string nodeId)
    {
        string normalized = nodeId?.Trim() ?? "";
        if (Guid.TryParse(normalized, out Guid parsed) && parsed != Guid.Empty)
            normalized = parsed.ToString("D");
        if (normalized.Length == 0)
            throw new ArgumentException("本机节点 ID 不能为空", nameof(nodeId));

        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    "packingproof-sample-device-v1\n" + normalized)))
            .ToLowerInvariant()[..32];
    }

    private static bool IsValidFingerprint(string? value) =>
        value is { Length: 32 }
        && value.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f');
}
