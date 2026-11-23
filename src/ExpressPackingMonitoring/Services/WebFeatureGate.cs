using ExpressPackingMonitoring.UpdateCore;

namespace ExpressPackingMonitoring.Services;

internal sealed record WebFeatureGateDecision(
    bool Allowed,
    int StatusCode,
    string ErrorCode,
    string Capability,
    string RouteGroup);

internal sealed class WebFeatureGate
{
    private readonly IReadOnlySet<string> _enabledGroups;

    internal WebFeatureGate(EffectiveProductCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        Capabilities = capabilities;
        _enabledGroups = capabilities.WebHost.EnabledGroups
            .Select(group => group.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    internal EffectiveProductCapabilities Capabilities { get; }

    internal bool IsGroupEnabled(string groupName) =>
        _enabledGroups.Contains(groupName);

    internal WebFeatureGateDecision Evaluate(string path, string method)
    {
        if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                path,
                "/api/extensions/v1/capabilities",
                StringComparison.OrdinalIgnoreCase))
        {
            return Allowed("");
        }

        if (path is "" or "/"
            && Capabilities.WebHostEnabled
            && !_enabledGroups.Contains("media-library")
            && _enabledGroups.Count > 0)
        {
            return Allowed("integration-landing");
        }

        bool mobileBackupClip =
            path.StartsWith("/api/mobile-backup/clip", StringComparison.OrdinalIgnoreCase)
            || (path.StartsWith("/api/mobile-backup/videos/", StringComparison.OrdinalIgnoreCase)
                && path.Contains("/clip", StringComparison.OrdinalIgnoreCase));
        if (mobileBackupClip)
        {
            return Capabilities.Features.Clip
                && _enabledGroups.Contains("mobile-backup")
                && _enabledGroups.Contains("clip")
                ? Allowed("clip")
                : new WebFeatureGateDecision(
                    Allowed: false,
                    StatusCode: 403,
                    ErrorCode: "capability_disabled",
                    Capability: "clip",
                    RouteGroup: "clip");
        }

        string group = ResolveGroup(path, method);
        if (group.Length == 0
            || _enabledGroups.Contains(group)
            || (group == "clip"
                && _enabledGroups.Contains("compress")))
            return Allowed(group);

        bool integrationRoute =
            path.StartsWith("/api/extensions/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/orderinfo", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/order-lookup/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/mobile-backup/clip", StringComparison.OrdinalIgnoreCase)
            || (path.StartsWith("/api/mobile-backup/videos/", StringComparison.OrdinalIgnoreCase)
                && path.Contains("/clip", StringComparison.OrdinalIgnoreCase))
            || path.StartsWith("/api/userscripts/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/kuaidizs-install-guide", StringComparison.OrdinalIgnoreCase);
        return new WebFeatureGateDecision(
            Allowed: false,
            StatusCode: integrationRoute ? 403 : 404,
            ErrorCode: integrationRoute ? "capability_disabled" : "not_found",
            Capability: CapabilityForGroup(group),
            RouteGroup: group);
    }

    internal static string ResolveGroup(string path, string method)
    {
        if (string.Equals(path, "/api/node-info", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/connections/", StringComparison.OrdinalIgnoreCase))
            return "node-info";
        if (path.StartsWith("/api/extensions/v1/orders", StringComparison.OrdinalIgnoreCase))
            return "extensions-v1-orders";
        if (path.StartsWith("/api/extensions/", StringComparison.OrdinalIgnoreCase))
            return "extensions-v1";
        if (path.StartsWith("/api/orderinfo", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/order-lookup/", StringComparison.OrdinalIgnoreCase))
            return "orders-v0";
        if (path.StartsWith("/api/mobile-backup/clip", StringComparison.OrdinalIgnoreCase)
            || (path.StartsWith("/api/mobile-backup/videos/", StringComparison.OrdinalIgnoreCase)
                && path.Contains("/clip", StringComparison.OrdinalIgnoreCase)))
            return "clip";
        if (path.StartsWith("/api/mobile-backup", StringComparison.OrdinalIgnoreCase))
            return "mobile-backup";
        if (path.StartsWith("/api/computer-backup", StringComparison.OrdinalIgnoreCase))
            return "computer-backup";
        if (path.StartsWith("/api/userscripts/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/kuaidizs-install-guide", StringComparison.OrdinalIgnoreCase))
            return "userscript";
        if (path.StartsWith("/api/mobile-app-download", StringComparison.OrdinalIgnoreCase))
            return "mobile-app";
        if (path.StartsWith("/api/local-settings", StringComparison.OrdinalIgnoreCase))
            return "local-settings";
        if ((path.StartsWith("/api/videos/", StringComparison.OrdinalIgnoreCase)
                && path.Contains("/clip", StringComparison.OrdinalIgnoreCase))
            || path.StartsWith("/api/clip-tasks/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/clips/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/clip-previews/", StringComparison.OrdinalIgnoreCase))
            return "clip";
        if (path.StartsWith("/api/photos", StringComparison.OrdinalIgnoreCase))
            return "photos";
        if (path.StartsWith("/api/videos", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/video-sources", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/storage", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/recording-devices", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/mobile-connection", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/clip-tasks/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/logo.png", StringComparison.OrdinalIgnoreCase)
            || path is "" or "/")
            return "media-library";
        return "";
    }

    internal static object CreateBrowserSnapshot(
        EffectiveProductCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ProductFeatures features = capabilities.Features;
        return new
        {
            schemaVersion = ProductCapabilitySchemaVersion.Current,
            webHostEnabled = capabilities.WebHostEnabled,
            capabilities = new
            {
                videoRecording = features.VideoRecording,
                photoCapture = features.PhotoCapture,
                mediaLibrary = features.MediaLibrary,
                remoteMediaAccess = features.RemoteMediaAccess,
                lanCollaboration = features.LanCollaboration,
                computerBackupReceive = features.ComputerBackupReceive,
                mobileBackupReceive = features.MobileBackupReceive,
                mobileApp = features.MobileApp,
                nasArchive = features.NasArchive,
                clip = features.Clip,
                compress = features.Compress,
                extensionApi = features.ExtensionApi,
                extensionMarket = features.ExtensionMarket,
                userscript = features.Userscript,
                orderReceiver = features.OrderReceiver,
                tts = features.Tts
            },
            inputs = new
            {
                camera = features.Inputs.Camera,
                microphone = features.Inputs.Microphone,
                scanner = features.Inputs.Scanner,
                cameraBarcode = features.Inputs.CameraBarcode,
                visionObb = features.Inputs.VisionObb
            },
            routeGroups = capabilities.WebHost.EnabledGroups
                .Select(group => group.Name)
                .ToArray()
        };
    }

    private static WebFeatureGateDecision Allowed(string routeGroup) =>
        new(true, 200, "", "", routeGroup);

    private static string CapabilityForGroup(string group) => group switch
    {
        "media-library" => "remoteMediaAccess",
        "photos" => "photoCapture",
        "clip" => "clip",
        "orders-v0" => "orderReceiver",
        "extensions-v1" or "extensions-v1-orders" => "extensionApi",
        "userscript" => "userscript",
        "mobile-backup" => "mobileBackupReceive",
        "computer-backup" => "computerBackupReceive",
        "mobile-app" => "mobileApp",
        "orderReceiver" => "orderReceiver",
        _ => group
    };
}
