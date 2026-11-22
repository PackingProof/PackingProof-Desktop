using System;
using System.Net;

namespace ExpressPackingMonitoring.Services;

public sealed partial class WebServer
{
    private bool RejectIncompatibleWorkstationMobileBackupRequest(
        HttpListenerContext ctx,
        string path,
        bool isDeviceEnrollment,
        bool hasDeviceAssetTicket)
    {
        if (isDeviceEnrollment
            || hasDeviceAssetTicket
            || !IsMobileBackupPath(path)
            || !_authenticatedDeviceKinds.TryGetValue(ctx, out string? deviceKind)
            || string.IsNullOrWhiteSpace(deviceKind)
            || !string.Equals(deviceKind, "pc", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string appVersion = ctx.Request.Headers[
            BackupCompatibilityPolicy.ClientVersionHeader]?.Trim() ?? "";
        BackupCompatibilityFailure? failure =
            BackupCompatibilityPolicy.ValidateWorkstationClientVersion(appVersion);
        if (failure == null)
            return false;

        SendBackupClientUpgradeRequired(ctx, failure);
        return true;
    }

    private bool RejectIncompatibleWorkstationHeartbeat(
        HttpListenerContext ctx,
        ConnectedClientHeartbeat heartbeat)
    {
        if (!string.Equals(
                heartbeat.ClientType,
                "recording-workstation",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string appVersion = ctx.Request.Headers[
                BackupCompatibilityPolicy.ClientVersionHeader]?.Trim()
            ?? heartbeat.AppVersion?.Trim()
            ?? "";
        BackupCompatibilityFailure? failure =
            BackupCompatibilityPolicy.ValidateWorkstationClientVersion(appVersion);
        if (failure == null)
            return false;

        SendBackupClientUpgradeRequired(ctx, failure);
        return true;
    }

    private void SendBackupClientUpgradeRequired(
        HttpListenerContext ctx,
        BackupCompatibilityFailure failure)
    {
        SendJson(ctx, 426, new
        {
            errorCode = "backup_client_upgrade_required",
            error = failure.Message,
            updateTarget = failure.UpdateTarget,
            minimumVersion = failure.MinimumVersion,
            minimumBuildNumber = failure.MinimumBuildNumber,
            downloadUrl = failure.DownloadUrl,
            protocol = BackupCompatibilityPolicy.BackupProtocol,
            enrollmentVersion = BackupCompatibilityPolicy.EnrollmentVersion,
            authVersion = BackupCompatibilityPolicy.AuthenticationVersion
        });
    }
}
