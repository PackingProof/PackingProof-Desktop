using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using ExpressPackingMonitoring.Config;

namespace ExpressPackingMonitoring.Services;

public sealed class RecordingDeviceInfo
{
    [JsonPropertyName("nodeId")]
    public string NodeId { get; set; } = "";

    [JsonPropertyName("nodeName")]
    public string NodeName { get; set; } = "";

    [JsonPropertyName("deviceType")]
    public string DeviceType { get; set; } = "";

    [JsonPropertyName("address")]
    public string Address { get; set; } = "";

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = [];

    [JsonPropertyName("online")]
    public bool Online { get; set; }
}

internal static class RecordingDeviceCatalog
{
    internal static IReadOnlyList<RecordingDeviceInfo> Build(
        string deploymentPreset,
        string hostNodeId,
        string hostNodeName,
        int hostPort,
        string hostAddress,
        IEnumerable<MobileOrderReceiverInfo>? mobileOrderReceivers,
        IEnumerable<ConnectedClientInfo>? connectedClients,
        bool includeOffline = false)
    {
        var candidates = new List<RecordingDeviceInfo>();
        string normalizedPreset = DeploymentPresets.Normalize(deploymentPreset);
        if (string.Equals(normalizedPreset, DeploymentPresets.RecordingHost, StringComparison.Ordinal)
            || string.Equals(normalizedPreset, DeploymentPresets.RecordingWorkstation, StringComparison.Ordinal))
        {
            candidates.Add(new RecordingDeviceInfo
            {
                NodeId = hostNodeId,
                NodeName = string.IsNullOrWhiteSpace(hostNodeName) ? Environment.MachineName : hostNodeName.Trim(),
                DeviceType = "pc",
                Address = NormalizeLanHttpAddress(hostAddress, hostPort),
                Capabilities =
                [
                    PackingProofCapabilities.Recording,
                    PackingProofCapabilities.OrderReceiver,
                    PackingProofCapabilities.CameraBarcode,
                    PackingProofCapabilities.Scanner,
                    PackingProofCapabilities.Microphone
                ],
                Online = true
            });
        }

        foreach (MobileOrderReceiverInfo mobile in mobileOrderReceivers ?? [])
        {
            candidates.Add(new RecordingDeviceInfo
            {
                NodeId = mobile.NodeId,
                NodeName = mobile.NodeName,
                DeviceType = "mobile",
                Address = NormalizeLanHttpAddress(mobile.Address, mobile.Port),
                Capabilities = mobile.Capabilities.ToList(),
                Online = mobile.Online
            });
        }

        foreach (ConnectedClientInfo client in connectedClients ?? [])
        {
            if (!(string.Equals(client.ClientType, "mobile-app", StringComparison.Ordinal)
                    || string.Equals(client.ClientType, "recording-workstation", StringComparison.Ordinal))
                || client.Capabilities.Count == 0)
            {
                continue;
            }

            candidates.Add(new RecordingDeviceInfo
            {
                NodeId = client.NodeId,
                NodeName = client.DisplayName,
                DeviceType = string.IsNullOrWhiteSpace(client.DeviceType) ? "mobile" : client.DeviceType,
                Address = NormalizeLanHttpAddress(client.RemoteAddress, client.OrderReceiverPort),
                Capabilities = client.Capabilities.ToList(),
                Online = true
            });
        }

        var result = new List<RecordingDeviceInfo>();
        var nodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RecordingDeviceInfo device in candidates)
        {
            device.NodeId = device.NodeId?.Trim() ?? "";
            device.NodeName = device.NodeName?.Trim() ?? "";
            device.DeviceType = device.DeviceType?.Trim().ToLowerInvariant() ?? "";
            device.Address = NormalizeLanHttpAddress(device.Address, defaultPort: 5280);
            device.Capabilities = device.Capabilities
                .Where(capability => !string.IsNullOrWhiteSpace(capability))
                .Select(capability => capability.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if ((!includeOffline && !device.Online)
                || device.NodeId.Length == 0
                || device.NodeName.Length == 0
                || device.Address.Length == 0
                || !device.Capabilities.Contains(PackingProofCapabilities.Recording, StringComparer.OrdinalIgnoreCase)
                || !device.Capabilities.Contains(PackingProofCapabilities.OrderReceiver, StringComparer.OrdinalIgnoreCase)
                || !nodeIds.Add(device.NodeId)
                || !endpoints.Add(device.Address))
            {
                continue;
            }

            result.Add(device);
        }

        return result;
    }

    internal static string NormalizeLanHttpAddress(string? value, int defaultPort)
    {
        string input = value?.Trim() ?? "";
        if (input.Length == 0)
            return "";
        if (!input.Contains("://", StringComparison.Ordinal))
            input = $"http://{input}";
        if (!Uri.TryCreate(input, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !IPAddress.TryParse(uri.Host, out IPAddress? address))
        {
            return "";
        }

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (!IsUsablePrivateIpv4(address))
            return "";

        int port = uri.IsDefaultPort ? defaultPort : uri.Port;
        if (port is <= 0 or > 65535)
            return "";
        return $"http://{address}:{port}";
    }

    private static bool IsUsablePrivateIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
            return false;

        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }
}
