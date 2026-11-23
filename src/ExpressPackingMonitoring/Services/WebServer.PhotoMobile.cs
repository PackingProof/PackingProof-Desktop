#nullable disable
using ExpressPackingMonitoring.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ExpressPackingMonitoring.Services.Extensions;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.Services
{
    public sealed partial class WebServer
    {
        private void HandlePhotoSearch(HttpListenerContext ctx)
        {
            if (_photoStore == null)
            {
                SendJson(ctx, 404, new
                {
                    errorCode = "capability_disabled",
                    capability = "photoCapture"
                });
                return;
            }
            string keyword = ctx.Request.QueryString["keyword"] ?? "";
            int page = int.TryParse(ctx.Request.QueryString["page"], out int parsedPage)
                ? Math.Max(1, parsedPage)
                : 1;
            int pageSize = int.TryParse(ctx.Request.QueryString["pageSize"], out int parsedSize)
                ? Math.Clamp(parsedSize, 1, 200)
                : 100;
            PhotoQueryPage result = _photoStore.QueryPage(
                keyword,
                (page - 1) * pageSize,
                pageSize);
            SendJson(ctx, 200, new
            {
                page,
                pageSize,
                total = result.Total,
                hasMore = page * pageSize < result.Total,
                photos = result.Items.Select(photo => new
                {
                    id = photo.Id,
                    trackingNumber = photo.TrackingNumber,
                    mode = photo.Mode,
                    createdAt = photo.CreatedAt,
                    sourceDeviceId = photo.SourceDeviceId,
                    sourceDeviceName = photo.SourceDeviceName,
                    width = photo.Width,
                    height = photo.Height,
                    fileSizeBytes = photo.FileSizeBytes,
                    archiveStatus = photo.ArchiveStatus,
                    archiveError = photo.ArchiveError,
                    thumbnailUrl = $"/api/photos/{photo.Id}/thumbnail",
                    downloadUrl = $"/api/photos/{photo.Id}/download"
                })
            });
        }

        private void HandleServePhoto(
            HttpListenerContext ctx,
            long photoId,
            bool thumbnail)
        {
            PhotoRecord photo = _photoStore?.Get(photoId);
            if (photo == null || !File.Exists(photo.FilePath))
            {
                SendJson(ctx, 404, new
                {
                    errorCode = "photo_not_found",
                    error = "照片不存在"
                });
                return;
            }

            byte[] bytes = File.ReadAllBytes(photo.FilePath);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "image/jpeg";
            ctx.Response.Headers["Cache-Control"] = thumbnail
                ? "private, max-age=300"
                : "no-store";
            if (!thumbnail)
            {
                ctx.Response.AddHeader(
                    "Content-Disposition",
                    $"inline; filename*=UTF-8''{Uri.EscapeDataString(Path.GetFileName(photo.FilePath))}");
            }
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        private void HandleDeletePhoto(HttpListenerContext ctx, long photoId)
        {
            if (_photoDeleteHandler == null || _photoStore?.Get(photoId) == null)
            {
                SendJson(ctx, 404, new
                {
                    errorCode = "photo_not_found",
                    error = "照片不存在"
                });
                return;
            }
            try
            {
                _photoDeleteHandler(photoId).GetAwaiter().GetResult();
                SendJson(ctx, 200, new { success = true });
            }
            catch (Exception ex)
            {
                SendJson(ctx, 409, new
                {
                    errorCode = "photo_delete_failed",
                    error = ex.Message
                });
            }
        }

        private void HandleRetryPhotoArchive(HttpListenerContext ctx, long photoId)
        {
            if (_photoArchiveRetryAsync == null || _photoStore?.Get(photoId) == null)
            {
                SendJson(ctx, 404, new
                {
                    errorCode = "photo_not_found",
                    error = "照片不存在"
                });
                return;
            }
            try
            {
                // WebServer 只持有删除回调；归档重试走同一照片服务队列。
                _photoArchiveRetryAsync(photoId).GetAwaiter().GetResult();
                SendJson(ctx, 202, new { success = true, status = "Pending" });
            }
            catch (Exception ex)
            {
                SendJson(ctx, 409, new
                {
                    errorCode = "photo_archive_retry_failed",
                    error = ex.Message
                });
            }
        }

        private static bool TryParsePhotoPath(
            string path,
            string suffix,
            out long photoId)
        {
            photoId = 0;
            const string prefix = "/api/photos/";
            if (path == null
                || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string value = path[prefix.Length..^suffix.Length].Trim('/');
            return long.TryParse(value, out photoId) && photoId > 0;
        }

        private static bool TryParsePhotoId(string path, out long photoId)
        {
            photoId = 0;
            const string prefix = "/api/photos/";
            if (path == null
                || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string value = path[prefix.Length..].Trim('/');
            return value.Length > 0
                && !value.Contains('/')
                && long.TryParse(value, out photoId)
                && photoId > 0;
        }


        private void HandleMobileBackupCapabilities(HttpListenerContext ctx)
        {
            string deviceId = ctx.Request.Headers["X-EPM-Device-Id"];
            string deviceName = ctx.Request.Headers["X-EPM-Device-Name"];
            try { deviceName = Uri.UnescapeDataString(deviceName ?? ""); } catch { }
            MobileOrderReceiverInfo registeredDevice = _mobileOrderReceivers.Register(
                ctx.Request.RemoteEndPoint?.Address,
                deviceId,
                deviceName,
                deviceKind: VideoSourceNameResolver.ReadDeviceKind(ctx.Request.Headers),
                platform: VideoSourceNameResolver.ReadDevicePlatform(ctx.Request.Headers));
            HostUploadStorageAvailability storage =
                _mobileBackupService.GetStorageAvailability();
            SendJson(ctx, 200, new
            {
                protocol = MobileBackupService.ProtocolVersion,
                version = 2,
                authVersion = BackupRequestAuthentication.CurrentVersion,
                computerId = _mobileBackupComputerId,
                computerName = _mobileBackupComputerName,
                deviceName = registeredDevice?.NodeName ?? deviceName,
                maxChunkBytes = MobileBackupService.ChunkSizeBytes,
                supportedFormats = new[] { "video/mp4" },
                storageAvailability = new
                {
                    available = storage.IsAvailable,
                    state = storage.State,
                    retryAfterSeconds = storage.RetryAfterSeconds,
                    reservedBytes = storage.ReservedBytes
                },
                features = new
                    {
                    videoLibrary = true,
                    cursorVideoLibrary = true,
                    rangePlayback = true,
                    multipleSessionsPerFile = false,
                    libraryScope = "host",
                    deviceVideoClipping = true,
                    uploadVideoCodec = true,
                    storageBackpressure = true
                },
                retryPolicy = new
                {
                    chunkMaxAttempts = 5,
                    chunkBackoffSeconds = new[] { 1, 2, 4, 8, 16 },
                    fileMaxAttempts = 3
                }
            });
        }

        private void HandleConnectionHeartbeat(HttpListenerContext ctx)
        {
            try
            {
                bool hasSignature = !string.IsNullOrWhiteSpace(
                    ctx.Request.Headers[BackupRequestAuthentication.SignatureHeader]);
                if (hasSignature
                    && !TryAuthorizeSignedBackupRequest(
                        ctx,
                        out bool missingBackupKey,
                        registerAuthorizedClient: false))
                {
                    SendJson(ctx, missingBackupKey ? 401 : 403, new
                    {
                        errorCode = missingBackupKey
                            ? "enrollment_required"
                            : "device_token_invalid",
                        error = missingBackupKey
                            ? "需要重新连接备份主机"
                            : "设备令牌无效，请重新连接"
                    });
                    return;
                }
                ConnectedClientHeartbeat heartbeat = ReadJsonBody<ConnectedClientHeartbeat>(ctx);
                if (RejectIncompatibleWorkstationHeartbeat(ctx, heartbeat))
                    return;
                if (_authenticatedDeviceIds.TryGetValue(ctx, out string authenticatedDeviceId))
                {
                    heartbeat.ClientId = authenticatedDeviceId;
                    heartbeat.NodeId = authenticatedDeviceId;
                }
                string remoteAddress = ctx.Request.RemoteEndPoint?.Address.ToString() ?? "unknown";
                string assignedDisplayName = "";
                if (string.Equals(heartbeat.ClientType, "recording-workstation", StringComparison.OrdinalIgnoreCase))
                {
                    assignedDisplayName = _recordingComputerNicknames.Assign(
                        heartbeat.NodeId,
                        heartbeat.DisplayName,
                        heartbeat.NicknameCustomized);
                    heartbeat.DisplayName = assignedDisplayName;
                }
                if (heartbeat.Connected != false
                    && string.Equals(heartbeat.ClientType, "mobile-app", StringComparison.OrdinalIgnoreCase)
                    && IPAddress.TryParse(remoteAddress, out IPAddress mobileAddress))
                {
                    MobileOrderReceiverInfo registeredDevice = _mobileOrderReceivers.Register(
                        mobileAddress,
                        heartbeat.NodeId,
                        heartbeat.DisplayName,
                        heartbeat.OrderReceiverPort,
                        heartbeat.Capabilities,
                        deviceKind: heartbeat.DeviceType,
                        platform: heartbeat.Platform);
                    assignedDisplayName = registeredDevice?.NodeName
                        ?? heartbeat.DisplayName?.Trim()
                        ?? "";
                    heartbeat.DisplayName = assignedDisplayName;
                }
                _connectedClients.Heartbeat(heartbeat, remoteAddress);
                OfficialUserscriptMigrationService.Shared.ObserveHeartbeat(heartbeat);
                var heartbeatResponse = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["assignedDisplayName"] = assignedDisplayName.Length > 0
                        ? assignedDisplayName
                        : null,
                    // 与注册响应同一份电脑名：主机改名后手机端靠心跳就能刷新"已连接电脑"。
                    ["computerName"] = _nodeName,
                    ["heartbeatIntervalSeconds"] = ConnectedClientRegistry.HeartbeatIntervalSeconds,
                    ["expiresInSeconds"] = ConnectedClientRegistry.ExpirationSeconds
                };
                if (_effectiveCapabilities.Features.MobileApp)
                {
                    _ = _mobileAppUpdatePolicy.RefreshInBackground();
                    NotifyMobileAppUpdateIfNeeded(heartbeat);
                    MobileAppUpdatePolicy updatePolicy = MobileAppUpdatePolicyProvider.MinimumPolicy;
                    MobileAppReleaseInfo latestRelease =
                        MobileAppUpdatePolicyProvider.SelectLatestReleaseForPlatform(
                            heartbeat.Platform,
                            _mobileAppUpdatePolicy.LatestRelease,
                            _mobileAppUpdatePolicy.LatestIosRelease);
                    string platform = (heartbeat.Platform ?? "").Trim().ToLowerInvariant();
                    heartbeatResponse["mobileAppUpdate"] = new
                    {
                        schemaVersion = updatePolicy.SchemaVersion,
                        minimumVersion = updatePolicy.MinimumVersion,
                        minimumBuildNumber = updatePolicy.MinimumBuildNumber,
                        message = updatePolicy.Message,
                        latestVersion = latestRelease?.Version ?? "",
                        latestBuildNumber = latestRelease?.BuildNumber ?? 0,
                        latestTag = latestRelease?.TagName ?? "",
                        latestPlatform = latestRelease?.Platform ?? "",
                        downloadUrl = latestRelease?.DownloadUrl
                            ?? (platform == "ios"
                                ? MobileAppUpdatePolicyProvider.TestFlightJoinUrl
                                : MobileAppUpdatePolicyProvider.ReleasesUrl)
                    };
                }
                SendJson(ctx, 200, heartbeatResponse);
            }
            catch (ConnectedClientValidationException ex)
            {
                int statusCode = ex.ErrorCode is "connection_registry_full" or "too_many_clients" ? 429 : 400;
                SendJson(ctx, statusCode, new { errorCode = ex.ErrorCode, error = ex.Message });
            }
            catch (JsonException ex)
            {
                SendJson(ctx, 400, new { errorCode = "invalid_json", error = ex.Message });
            }
        }

        internal IReadOnlyList<ConnectedClientInfo> GetConnectedClients() => _connectedClients.GetSnapshot();

        internal HostUploadStorageAvailability GetMobileBackupStorageAvailability() =>
            _mobileBackupService.GetStorageAvailability();

        internal IReadOnlyList<MobileBackupDeviceDirectoryEntry> GetMobileBackupDeviceDirectory()
        {
            var devices = new Dictionary<string, MobileBackupDeviceDirectoryEntry>(
                StringComparer.OrdinalIgnoreCase);
            IReadOnlySet<string> removedDeviceIds =
                GetRemovedSourceDeviceIds();
            foreach (MobileOrderReceiverInfo mobile in _mobileOrderReceivers.GetKnownRecordingDevices())
            {
                if (!BackupDeviceIdentity.IsRemote(mobile.NodeId, _nodeId))
                    continue;
                DateTimeOffset? lastSeen = mobile.LastSeenUtc == default
                    ? null
                    : new DateTimeOffset(
                        DateTime.SpecifyKind(mobile.LastSeenUtc, DateTimeKind.Utc));
                devices[mobile.NodeId] = new MobileBackupDeviceDirectoryEntry(
                    mobile.NodeId,
                    mobile.NodeName,
                    mobile.Online,
                    lastSeen);
            }

            foreach (ConnectedClientInfo client in _connectedClients.GetSnapshot())
            {
                if (!string.Equals(client.ClientType, "mobile-app", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(
                        client.ClientType,
                        "recording-workstation",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string deviceId = string.IsNullOrWhiteSpace(client.NodeId)
                    ? client.ClientId
                    : client.NodeId;
                if (!BackupDeviceIdentity.IsRemote(deviceId, _nodeId)
                    || removedDeviceIds.Contains(deviceId))
                    continue;
                devices.TryGetValue(
                    deviceId,
                    out MobileBackupDeviceDirectoryEntry existing);
                string displayName = client.DisplayName;
                if (string.IsNullOrWhiteSpace(displayName) && existing != null)
                    displayName = existing.DisplayName;
                devices[deviceId] = new MobileBackupDeviceDirectoryEntry(
                    deviceId,
                    displayName,
                    true,
                    client.LastSeenUtc);
            }

            return devices.Values.ToArray();
        }

        internal IReadOnlyList<OrderIntegrationDeviceStatus> GetOrderIntegrationDeviceStatuses()
        {
            IReadOnlyDictionary<string, OrderIntegrationActivity> activities =
                _orderIntegrationActivities.GetSnapshot();
            var statuses = GetRecordingDevices("", includeKnown: true)
                .Where(device => device.Online || activities.ContainsKey(device.NodeId))
                .Select(device =>
                {
                    activities.TryGetValue(device.NodeId, out OrderIntegrationActivity activity);
                    return new OrderIntegrationDeviceStatus(
                        device.NodeId,
                        device.NodeName,
                        device.DeviceType,
                        device.Online,
                        activity?.LastActivityUtc,
                        activity?.ReceivedCount ?? 0);
                })
                .ToList();
            HashSet<string> knownNodeIds = statuses
                .Select(device => device.NodeId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            HashSet<string> onlineNodeIds = _connectedClients.GetSnapshot()
                .Where(client => string.Equals(client.ClientType, "recording-workstation", StringComparison.OrdinalIgnoreCase))
                .Select(client => client.NodeId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (RecordingComputerNicknameInfo computer in _recordingComputerNicknames.GetKnown())
            {
                if (!knownNodeIds.Add(computer.NodeId)) continue;
                activities.TryGetValue(computer.NodeId, out OrderIntegrationActivity activity);
                bool online = onlineNodeIds.Contains(computer.NodeId);
                if (!online && activity == null) continue;
                statuses.Add(new OrderIntegrationDeviceStatus(
                    computer.NodeId,
                    computer.DisplayName,
                    "pc",
                    online,
                    activity?.LastActivityUtc,
                    activity?.ReceivedCount ?? 0));
            }
            return statuses
                .OrderByDescending(device => device.Online)
                .ThenBy(device => device.DisplayName, StringComparer.CurrentCulture)
                .ToArray();
        }

    }
}
