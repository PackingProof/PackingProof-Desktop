using System.Text.Json.Serialization;
using ExpressPackingMonitoring.Config;

namespace ExpressPackingMonitoring.Services;

public sealed class PackingProofNodeInfo
{
    public const string ExpectedProtocol = "packingproof";
    public const int SupportedProtocolVersion = 1;

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "";

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; }

    [JsonPropertyName("nodeId")]
    public string NodeId { get; set; } = "";

    [JsonPropertyName("nodeName")]
    public string NodeName { get; set; } = "";

    [JsonPropertyName("preset")]
    public string Preset { get; set; } = "";

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = [];

    [JsonPropertyName("httpPort")]
    public int HttpPort { get; set; }

    [JsonPropertyName("backupCompatibility")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BackupCompatibilityInfo? BackupCompatibility { get; set; }

    [JsonIgnore]
    public string Address { get; set; } = "";

    [JsonIgnore]
    public string CapabilitySummary => string.Join("、", Capabilities);

    [JsonIgnore]
    public bool IsValidHost =>
        string.Equals(Protocol, ExpectedProtocol, StringComparison.Ordinal)
        && ProtocolVersion == SupportedProtocolVersion
        && Guid.TryParse(NodeId, out Guid nodeId)
        && nodeId != Guid.Empty
        && DeploymentPresets.IsKnown(Preset)
        && Capabilities.Contains(PackingProofCapabilities.Host, StringComparer.OrdinalIgnoreCase)
        && HttpPort is > 0 and <= 65535;
}

public sealed class BackupCompatibilityInfo
{
    [JsonPropertyName("hostVersion")]
    public string HostVersion { get; set; } = "";

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "";

    [JsonPropertyName("enrollmentVersion")]
    public int EnrollmentVersion { get; set; }

    [JsonPropertyName("authVersion")]
    public int AuthVersion { get; set; }

    [JsonPropertyName("minimumMobileVersion")]
    public string MinimumMobileVersion { get; set; } = "";

    [JsonPropertyName("minimumMobileBuildNumber")]
    public int MinimumMobileBuildNumber { get; set; }

    [JsonPropertyName("minimumWorkstationVersion")]
    public string MinimumWorkstationVersion { get; set; } = "";
}
