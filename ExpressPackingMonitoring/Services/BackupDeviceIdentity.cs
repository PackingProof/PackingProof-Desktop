namespace ExpressPackingMonitoring.Services;

internal static class BackupDeviceIdentity
{
    internal static bool IsRemote(string deviceId, string localNodeId)
    {
        return !string.IsNullOrWhiteSpace(deviceId) &&
            !string.Equals(deviceId, localNodeId, StringComparison.OrdinalIgnoreCase);
    }
}
