using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services;

internal sealed record OrderInfoRelayResult(
    string NodeId,
    string Name,
    string Type,
    string Address,
    bool Ok,
    int Status = 0,
    int TestCount = 0,
    string Error = "");

internal static class OrderInfoRelay
{
    // Keep the relay payload aligned with the browser/mobile protocol.  The
    // WebServer accepts both naming styles when reading, but remote clients
    // may use a strict camelCase contract when writing.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(1.5)
    })
    {
        Timeout = TimeSpan.FromSeconds(4)
    };

    internal static async Task<IReadOnlyList<OrderInfoRelayResult>> BroadcastAsync(
        IReadOnlyList<RecordingDeviceInfo> devices,
        string localNodeId,
        IReadOnlyList<OrderInfo> orders,
        Func<RecordingDeviceInfo, IReadOnlyList<OrderInfo>, CancellationToken, Task<OrderInfoRelayResult>> remoteSender,
        Func<RecordingDeviceInfo, OrderInfoRelayResult> localReceiver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(remoteSender);
        ArgumentNullException.ThrowIfNull(localReceiver);

        Task<OrderInfoRelayResult>[] tasks = devices.Select(device =>
            string.Equals(device.NodeId, localNodeId, StringComparison.OrdinalIgnoreCase)
                ? Task.FromResult(localReceiver(device))
                : SendAndVerifyAsync(device, orders, remoteSender, cancellationToken)).ToArray();
        return await Task.WhenAll(tasks);
    }

    internal static Task<OrderInfoRelayResult> SendAsync(
        RecordingDeviceInfo device,
        IReadOnlyList<OrderInfo> orders,
        CancellationToken cancellationToken) =>
        SendAsync(device, orders, Client, cancellationToken);

    internal static async Task<OrderInfoRelayResult> SendAsync(
        RecordingDeviceInfo device,
        IReadOnlyList<OrderInfo> orders,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            using var identityResponse = await client.GetAsync($"{device.Address}/api/node-info", cancellationToken);
            if (!identityResponse.IsSuccessStatusCode)
                return Failed(device, (int)identityResponse.StatusCode, "identity_unavailable");

            using JsonDocument identity = JsonDocument.Parse(
                await identityResponse.Content.ReadAsStringAsync(cancellationToken));
            string nodeId = GetString(identity.RootElement, "nodeId");
            if (!string.Equals(nodeId, device.NodeId, StringComparison.OrdinalIgnoreCase))
                return Failed(device, (int)identityResponse.StatusCode, "identity_mismatch");

            using var content = new StringContent(
                JsonSerializer.Serialize(orders, JsonOptions),
                Encoding.UTF8,
                "application/json");
            using var response = await client.PostAsync($"{device.Address}/api/orderinfo", content, cancellationToken);
            string responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return Failed(device, (int)response.StatusCode, "delivery_failed");

            using JsonDocument document = JsonDocument.Parse(responseText);
            // The identity probe above is authoritative.  Older order
            // receivers only returned { ok, testCount } from POST, so a
            // missing nodeId is compatible; an explicit, different nodeId
            // still indicates that the endpoint changed identity and must be
            // rejected.
            string reportedNodeId = GetString(document.RootElement, "nodeId");
            if (reportedNodeId.Length > 0 &&
                !string.Equals(reportedNodeId, device.NodeId, StringComparison.OrdinalIgnoreCase))
                return Failed(device, (int)response.StatusCode, "identity_mismatch");

            int testCount = TryGetInt32(document.RootElement, "testCount");
            return new OrderInfoRelayResult(
                device.NodeId,
                device.NodeName,
                device.DeviceType,
                device.Address,
                true,
                (int)response.StatusCode,
                testCount);
        }
        catch (OperationCanceledException)
        {
            return Failed(device, 0, "timeout");
        }
        catch (Exception)
        {
            return Failed(device, 0, "connect");
        }
    }

    private static async Task<OrderInfoRelayResult> SendAndVerifyAsync(
        RecordingDeviceInfo device,
        IReadOnlyList<OrderInfo> orders,
        Func<RecordingDeviceInfo, IReadOnlyList<OrderInfo>, CancellationToken, Task<OrderInfoRelayResult>> sender,
        CancellationToken cancellationToken)
    {
        try
        {
            OrderInfoRelayResult result = await sender(device, orders, cancellationToken);
            return !result.Ok || string.Equals(result.NodeId, device.NodeId, StringComparison.OrdinalIgnoreCase)
                ? result
                : Failed(device, result.Status, "identity_mismatch");
        }
        catch (OperationCanceledException)
        {
            return Failed(device, 0, "timeout");
        }
        catch (Exception)
        {
            return Failed(device, 0, "connect");
        }
    }

    private static string GetString(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out JsonElement property)
            ? property.ValueKind == JsonValueKind.String
                ? property.GetString()?.Trim() ?? ""
                : ""
            : "";

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
            return true;

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static int TryGetInt32(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out JsonElement value) &&
        value.TryGetInt32(out int result)
            ? result
            : 0;

    private static OrderInfoRelayResult Failed(RecordingDeviceInfo device, int status, string error) =>
        new(device.NodeId, device.NodeName, device.DeviceType, device.Address, false, status, 0, error);
}
