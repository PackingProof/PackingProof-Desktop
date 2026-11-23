using System.Collections.Frozen;

namespace ExpressPackingMonitoring.UpdateCore;

public sealed record RoleCapabilities(
    string DeploymentPreset,
    bool IsHost,
    bool CanStartWebHost,
    IReadOnlySet<string> AllowedWebRouteGroups,
    IReadOnlySet<ProductCapabilityId> Allowed);

public sealed record RuntimeEnvironment(
    bool CameraAvailable,
    bool MicrophoneAvailable,
    bool ScannerAvailable,
    bool CameraBarcodeAvailable,
    bool VisionObbAvailable)
{
    public static RuntimeEnvironment FullyAvailable { get; } =
        new(true, true, true, true, true);
}

public sealed record CapabilityNarrowingReason(
    ProductCapabilityId CapabilityId,
    string ReasonCode);

public sealed record CapabilityDiagnosticSnapshot(
    string ProfileId,
    int FeaturesSchemaVersion,
    string FeaturesSha256,
    string SelectedDeploymentPreset,
    ProductFeatures EffectiveCapabilities,
    IReadOnlyList<CapabilityNarrowingReason> RuntimeNarrowingReasons,
    bool WebHostEnabled,
    IReadOnlyList<string> WebRouteGroups);

public sealed record ProductCapabilityIdentity(
    string ProfileId,
    int SchemaVersion,
    string FeaturesSha256);

public sealed class EffectiveProductCapabilities
{
    private EffectiveProductCapabilities(
        ProductFeatures features,
        bool isHost,
        WebHostPlan webHost,
        IReadOnlyList<CapabilityNarrowingReason> narrowingReasons,
        ProductCapabilityIdentity identity)
    {
        Features = features;
        IsHost = isHost;
        WebHost = webHost;
        NarrowingReasons = narrowingReasons.ToArray();
        Identity = identity;
    }

    public ProductFeatures Features { get; }
    public InputFeatures Inputs => Features.Inputs;
    public WebHostPlan WebHost { get; }
    public bool WebHostEnabled => WebHost.Enabled;
    public bool IsHost { get; }
    internal IReadOnlyList<CapabilityNarrowingReason> NarrowingReasons { get; }
    internal string SelectedDeploymentPreset { get; init; } = "";
    internal ProductCapabilityIdentity Identity { get; }

    public static EffectiveProductCapabilities Compose(
        ProductFeatures brand,
        RoleCapabilities role,
        RuntimeEnvironment environment,
        ProductCapabilityIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(brand);
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(environment);

        ProductFeatureValidator.ThrowIfRelationshipsInvalid(brand);

        var narrowingReasons = new List<CapabilityNarrowingReason>();
        ProductFeatures roleFeatures = ProductRoleCapabilityMapper.Apply(brand, role);
        ProductFeatures effective = ApplyRuntimeEnvironment(
            roleFeatures,
            environment,
            narrowingReasons);
        WebHostPlan webHost = ProductWebRouteGroups.Resolve(effective, role);
        ProductFeatureValidator.ThrowIfRelationshipsInvalid(effective);
        return new EffectiveProductCapabilities(
            effective,
            role.IsHost,
            webHost,
            narrowingReasons,
            identity)
        {
            SelectedDeploymentPreset = role.DeploymentPreset
        };
    }

    public CapabilityDiagnosticSnapshot CreateDiagnosticSnapshot() =>
        new(
            Identity.ProfileId,
            Identity.SchemaVersion,
            Identity.FeaturesSha256,
            SelectedDeploymentPreset,
            Features,
            NarrowingReasons.ToArray(),
            WebHostEnabled,
            WebHost.EnabledGroups.Select(group => group.Name).ToArray());

    private static ProductFeatures ApplyRuntimeEnvironment(
        ProductFeatures features,
        RuntimeEnvironment environment,
        ICollection<CapabilityNarrowingReason> reasons)
    {
        bool camera = Narrow(
            features.Inputs.Camera,
            environment.CameraAvailable,
            ProductCapabilityId.InputCamera,
            "camera_unavailable",
            reasons);
        bool scanner = Narrow(
            features.Inputs.Scanner,
            environment.ScannerAvailable,
            ProductCapabilityId.InputScanner,
            "scanner_unavailable",
            reasons);
        bool cameraBarcode = Narrow(
            features.Inputs.CameraBarcode,
            camera && environment.CameraBarcodeAvailable,
            ProductCapabilityId.InputCameraBarcode,
            "camera_barcode_unavailable",
            reasons);
        bool visionObb = Narrow(
            features.Inputs.VisionObb,
            camera && environment.VisionObbAvailable,
            ProductCapabilityId.InputVisionObb,
            "vision_obb_unavailable",
            reasons);

        bool photoCapture = features.PhotoCapture && camera;
        bool videoRecording = features.VideoRecording && camera;
        bool microphone = features.Inputs.Microphone
            && videoRecording
            && environment.MicrophoneAvailable;
        if (features.Inputs.Microphone && !microphone)
        {
            reasons.Add(new(
                ProductCapabilityId.InputMicrophone,
                videoRecording ? "microphone_unavailable" : "recording_unavailable"));
        }
        if (features.PhotoCapture && !photoCapture)
        {
            reasons.Add(new(ProductCapabilityId.PhotoCapture, "camera_unavailable"));
        }
        if (features.VideoRecording && !videoRecording)
        {
            reasons.Add(new(ProductCapabilityId.VideoRecording, "camera_unavailable"));
        }

        return new ProductFeatures(
            videoRecording,
            photoCapture,
            features.MediaLibrary,
            features.RemoteMediaAccess,
            features.LanCollaboration,
            features.ComputerBackupReceive,
            features.MobileBackupReceive,
            features.MobileApp,
            features.NasArchive,
            features.Clip,
            features.Compress,
            features.ExtensionApi,
            features.ExtensionMarket,
            features.Userscript,
            features.OrderReceiver,
            features.Tts,
            new InputFeatures(camera, microphone, scanner, cameraBarcode, visionObb));
    }

    private static bool Narrow(
        bool configured,
        bool available,
        ProductCapabilityId capability,
        string reasonCode,
        ICollection<CapabilityNarrowingReason> reasons)
    {
        if (!configured || available)
        {
            return configured;
        }
        reasons.Add(new(capability, reasonCode));
        return false;
    }
}

public static class ProductRoleCapabilityMapper
{
    public const string RecordingHost = "RecordingHost";
    public const string RecordingWorkstation = "RecordingWorkstation";
    public const string ViewerClient = "ViewerClient";
    public const string MobileBackupHost = "MobileBackupHost";

    private static readonly IReadOnlySet<ProductCapabilityId> FixedCapabilities =
        new[]
        {
            ProductCapabilityId.MobileApp,
            ProductCapabilityId.ExtensionApi,
            ProductCapabilityId.ExtensionMarket,
            ProductCapabilityId.Tts
        }.ToFrozenSet();

    private static readonly IReadOnlyDictionary<string, RoleCapabilities> Roles =
        new Dictionary<string, RoleCapabilities>(StringComparer.OrdinalIgnoreCase)
        {
            [RecordingHost] = Role(
                RecordingHost,
                isHost: true,
                canStartWebHost: true,
                AllRouteGroups(),
                ProductCapabilityId.VideoRecording,
                ProductCapabilityId.PhotoCapture,
                ProductCapabilityId.MediaLibrary,
                ProductCapabilityId.RemoteMediaAccess,
                ProductCapabilityId.LanCollaboration,
                ProductCapabilityId.OrderReceiver,
                ProductCapabilityId.ComputerBackupReceive,
                ProductCapabilityId.MobileBackupReceive,
                ProductCapabilityId.NasArchive,
                ProductCapabilityId.Clip,
                ProductCapabilityId.Compress,
                ProductCapabilityId.Userscript,
                ProductCapabilityId.InputCamera,
                ProductCapabilityId.InputMicrophone,
                ProductCapabilityId.InputScanner,
                ProductCapabilityId.InputCameraBarcode,
                ProductCapabilityId.InputVisionObb),
            [RecordingWorkstation] = Role(
                RecordingWorkstation,
                isHost: false,
                canStartWebHost: true,
                RouteGroups(
                    "orders-v0",
                    "extensions-v1-orders",
                    "extensions-v1",
                    "node-info",
                    "userscript"),
                ProductCapabilityId.VideoRecording,
                ProductCapabilityId.PhotoCapture,
                ProductCapabilityId.MediaLibrary,
                ProductCapabilityId.LanCollaboration,
                ProductCapabilityId.OrderReceiver,
                ProductCapabilityId.Clip,
                ProductCapabilityId.Compress,
                ProductCapabilityId.InputCamera,
                ProductCapabilityId.InputMicrophone,
                ProductCapabilityId.InputScanner,
                ProductCapabilityId.InputCameraBarcode,
                ProductCapabilityId.InputVisionObb),
            [ViewerClient] = Role(
                ViewerClient,
                isHost: false,
                canStartWebHost: false,
                RouteGroups(),
                ProductCapabilityId.MediaLibrary,
                ProductCapabilityId.LanCollaboration,
                ProductCapabilityId.Userscript),
            [MobileBackupHost] = Role(
                MobileBackupHost,
                isHost: true,
                canStartWebHost: true,
                RouteGroups(
                    "media-library",
                    "clip",
                    "compress",
                    "orders-v0",
                    "extensions-v1-orders",
                    "extensions-v1",
                    "userscript",
                    "mobile-backup",
                    "computer-backup",
                    "node-info",
                    "mobile-app",
                    "local-settings"),
                ProductCapabilityId.MediaLibrary,
                ProductCapabilityId.RemoteMediaAccess,
                ProductCapabilityId.LanCollaboration,
                ProductCapabilityId.ComputerBackupReceive,
                ProductCapabilityId.MobileBackupReceive,
                ProductCapabilityId.NasArchive,
                ProductCapabilityId.OrderReceiver,
                ProductCapabilityId.Clip,
                ProductCapabilityId.Compress,
                ProductCapabilityId.Userscript)
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<RoleCapabilities> AllRoles { get; } =
        Roles.Values.OrderBy(role => role.DeploymentPreset, StringComparer.Ordinal).ToArray();

    public static IReadOnlySet<ProductCapabilityId> BrandFixedCapabilities =>
        FixedCapabilities;

    public static bool TryGet(string? deploymentPreset, out RoleCapabilities role)
    {
        if (!string.IsNullOrWhiteSpace(deploymentPreset)
            && Roles.TryGetValue(deploymentPreset, out RoleCapabilities? mapped))
        {
            role = mapped;
            return true;
        }
        role = null!;
        return false;
    }

    public static RoleCapabilities Get(string deploymentPreset)
    {
        if (!TryGet(deploymentPreset, out RoleCapabilities role))
        {
            throw new ArgumentException($"未知工位用途：{deploymentPreset}", nameof(deploymentPreset));
        }
        return role;
    }

    public static ProductFeatures Apply(ProductFeatures brand, RoleCapabilities role)
    {
        ArgumentNullException.ThrowIfNull(brand);
        ArgumentNullException.ThrowIfNull(role);

        bool Allowed(ProductCapabilityId id) =>
            FixedCapabilities.Contains(id) || role.Allowed.Contains(id);

        return new ProductFeatures(
            brand.VideoRecording && Allowed(ProductCapabilityId.VideoRecording),
            brand.PhotoCapture && Allowed(ProductCapabilityId.PhotoCapture),
            brand.MediaLibrary && Allowed(ProductCapabilityId.MediaLibrary),
            brand.RemoteMediaAccess && Allowed(ProductCapabilityId.RemoteMediaAccess),
            brand.LanCollaboration && Allowed(ProductCapabilityId.LanCollaboration),
            brand.ComputerBackupReceive && Allowed(ProductCapabilityId.ComputerBackupReceive),
            brand.MobileBackupReceive && Allowed(ProductCapabilityId.MobileBackupReceive),
            brand.MobileApp && Allowed(ProductCapabilityId.MobileApp),
            brand.NasArchive && Allowed(ProductCapabilityId.NasArchive),
            brand.Clip && Allowed(ProductCapabilityId.Clip),
            brand.Compress && Allowed(ProductCapabilityId.Compress),
            brand.ExtensionApi && Allowed(ProductCapabilityId.ExtensionApi),
            brand.ExtensionMarket && Allowed(ProductCapabilityId.ExtensionMarket),
            brand.Userscript && Allowed(ProductCapabilityId.Userscript),
            brand.OrderReceiver && Allowed(ProductCapabilityId.OrderReceiver),
            brand.Tts && Allowed(ProductCapabilityId.Tts),
            new InputFeatures(
                brand.Inputs.Camera && Allowed(ProductCapabilityId.InputCamera),
                brand.Inputs.Microphone && Allowed(ProductCapabilityId.InputMicrophone),
                brand.Inputs.Scanner && Allowed(ProductCapabilityId.InputScanner),
                brand.Inputs.CameraBarcode && Allowed(ProductCapabilityId.InputCameraBarcode),
                brand.Inputs.VisionObb && Allowed(ProductCapabilityId.InputVisionObb)));
    }

    private static RoleCapabilities Role(
        string deploymentPreset,
        bool isHost,
        bool canStartWebHost,
        IReadOnlySet<string> allowedWebRouteGroups,
        params ProductCapabilityId[] capabilities) =>
        new(
            deploymentPreset,
            isHost,
            canStartWebHost,
            allowedWebRouteGroups,
            capabilities.ToFrozenSet());

    private static IReadOnlySet<string> AllRouteGroups() =>
        ProductWebRouteGroups.All
            .Select(group => group.Name)
            .ToFrozenSet(StringComparer.Ordinal);

    private static IReadOnlySet<string> RouteGroups(params string[] names) =>
        names.ToFrozenSet(StringComparer.Ordinal);
}

public static class ProductCapabilityRuntime
{
    private static readonly RoleCapabilities UnconfiguredRole =
        new(
            "",
            IsHost: false,
            CanStartWebHost: false,
            Array.Empty<string>().ToFrozenSet(),
            Array.Empty<ProductCapabilityId>().ToFrozenSet());

    public static EffectiveProductCapabilities ForPreset(
        string? deploymentPreset,
        RuntimeEnvironment? environment = null)
        => ForPreset(
            BrandMetadata.ProductFeatures,
            deploymentPreset,
            environment,
            BrandMetadata.CapabilityIdentity);

    public static EffectiveProductCapabilities ForPreset(
        ProductFeatures brand,
        string? deploymentPreset,
        RuntimeEnvironment? environment = null,
        ProductCapabilityIdentity? identity = null)
    {
        ArgumentNullException.ThrowIfNull(brand);
        RoleCapabilities role = ProductRoleCapabilityMapper.TryGet(
            deploymentPreset,
            out RoleCapabilities mapped)
                ? mapped
                : UnconfiguredRole;
        return EffectiveProductCapabilities.Compose(
            brand,
            role,
            environment ?? RuntimeEnvironment.FullyAvailable,
            identity ?? new ProductCapabilityIdentity("", 0, ""));
    }

    public static ProductFeatures CurrentFeatures => BrandMetadata.ProductFeatures;

    public static IReadOnlyList<RoleCapabilities> AvailableRoles(
        RuntimeEnvironment? environment = null) =>
        ProductRoleCapabilityMapper.AllRoles
            .Where(role => ProductRoleAvailabilityPolicy.IsAvailable(
                BrandMetadata.ProductFeatures,
                role,
                environment ?? RuntimeEnvironment.FullyAvailable))
            .ToArray();

    public static bool IsRoleAvailable(
        RoleCapabilities role,
        RuntimeEnvironment? environment = null) =>
        ProductRoleAvailabilityPolicy.IsAvailable(
            BrandMetadata.ProductFeatures,
            role,
            environment ?? RuntimeEnvironment.FullyAvailable);

    public static string? SingleAvailablePreset(
        RuntimeEnvironment? environment = null) =>
        AvailableRoles(environment) is [RoleCapabilities role]
            ? role.DeploymentPreset
            : null;
}

public static class ProductRoleAvailabilityPolicy
{
    public static IReadOnlyList<RoleCapabilities> AvailableRoles(
        ProductFeatures brand,
        RuntimeEnvironment? environment = null) =>
        ProductRoleCapabilityMapper.AllRoles
            .Where(role => IsAvailable(
                brand,
                role,
                environment ?? RuntimeEnvironment.FullyAvailable))
            .ToArray();

    public static bool IsAvailable(
        ProductFeatures brand,
        RoleCapabilities role,
        RuntimeEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(brand);
        ArgumentNullException.ThrowIfNull(role);
        ProductFeatures features = EffectiveProductCapabilities.Compose(
            brand,
            role,
            environment ?? RuntimeEnvironment.FullyAvailable,
            BrandMetadata.CapabilityIdentity).Features;
        return role.DeploymentPreset switch
        {
            ProductRoleCapabilityMapper.RecordingHost =>
                features.VideoRecording || features.PhotoCapture,
            ProductRoleCapabilityMapper.RecordingWorkstation =>
                features.VideoRecording && features.LanCollaboration,
            ProductRoleCapabilityMapper.ViewerClient =>
                features.MediaLibrary && features.LanCollaboration,
            ProductRoleCapabilityMapper.MobileBackupHost =>
                features.MediaLibrary
                && (features.MobileBackupReceive || features.ComputerBackupReceive),
            _ => false
        };
    }
}

public static class ProductCapabilityProtocolMapper
{
    public static IReadOnlyList<string> ToLegacyCapabilities(
        EffectiveProductCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ProductFeatures features = capabilities.Features;
        var result = new List<string>();
        Add(result, capabilities.IsHost, "host");
        Add(result,
            features.MediaLibrary && features.RemoteMediaAccess,
            "web-playback");
        Add(result, features.VideoRecording, "pc-recording");
        Add(result, features.VideoRecording, "recording");
        Add(result, features.OrderReceiver, "order-receiver");
        Add(result,
            features.MobileBackupReceive || features.ComputerBackupReceive,
            "mobile-backup");
        Add(result, features.Inputs.CameraBarcode, "camera-barcode");
        Add(result, features.Inputs.Scanner, "scanner");
        Add(result, features.Inputs.Microphone, "microphone");
        return result;
    }

    private static void Add(ICollection<string> values, bool enabled, string value)
    {
        if (enabled)
        {
            values.Add(value);
        }
    }
}
