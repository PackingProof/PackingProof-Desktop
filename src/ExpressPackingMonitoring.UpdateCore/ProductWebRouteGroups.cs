using System.Collections.Frozen;

namespace ExpressPackingMonitoring.UpdateCore;

public enum WebAuthScope
{
    None,
    Loopback,
    WebAccessKey,
    IntegrationSigned,
    DeviceLease
}

public sealed record WebRouteGroup(
    string Name,
    bool StartsHost,
    bool RequiresHost,
    IReadOnlySet<ProductCapabilityId> RequiredAllOf,
    IReadOnlySet<ProductCapabilityId> RequiredAnyOf,
    WebAuthScope AuthScope)
{
    public bool IsSatisfiedBy(ProductFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);
        IReadOnlyDictionary<ProductCapabilityId, bool> values =
            ProductFeaturesJson.ToCapabilityMap(features);
        return RequiredAllOf.All(id => values[id])
            && (RequiredAnyOf.Count == 0 || RequiredAnyOf.Any(id => values[id]));
    }
}

public sealed record WebHostPlan(
    bool Enabled,
    IReadOnlyList<WebRouteGroup> EnabledGroups);

public static class ProductWebRouteGroups
{
    private static readonly IReadOnlySet<ProductCapabilityId> Empty =
        Array.Empty<ProductCapabilityId>().ToFrozenSet();

    public static IReadOnlyList<WebRouteGroup> All { get; } =
    [
        Group(
            "media-library",
            startsHost: true,
            WebAuthScope.WebAccessKey,
            [ProductCapabilityId.MediaLibrary, ProductCapabilityId.RemoteMediaAccess]),
        Group(
            "clip",
            startsHost: true,
            WebAuthScope.WebAccessKey,
            [
                ProductCapabilityId.MediaLibrary,
                ProductCapabilityId.RemoteMediaAccess,
                ProductCapabilityId.Clip
            ]),
        Group(
            "photos",
            startsHost: true,
            WebAuthScope.WebAccessKey,
            [
                ProductCapabilityId.MediaLibrary,
                ProductCapabilityId.RemoteMediaAccess,
                ProductCapabilityId.PhotoCapture
            ]),
        Group(
            "compress",
            startsHost: true,
            WebAuthScope.WebAccessKey,
            [
                ProductCapabilityId.MediaLibrary,
                ProductCapabilityId.RemoteMediaAccess,
                ProductCapabilityId.Compress
            ]),
        Group(
            "orders-v0",
            startsHost: true,
            WebAuthScope.IntegrationSigned,
            [ProductCapabilityId.OrderReceiver]),
        Group(
            "extensions-v1-orders",
            startsHost: false,
            WebAuthScope.IntegrationSigned,
            [ProductCapabilityId.ExtensionApi, ProductCapabilityId.OrderReceiver]),
        Group(
            "extensions-v1",
            startsHost: true,
            WebAuthScope.IntegrationSigned,
            [ProductCapabilityId.ExtensionApi]),
        Group(
            "userscript",
            startsHost: false,
            WebAuthScope.WebAccessKey,
            [ProductCapabilityId.Userscript],
            [ProductCapabilityId.OrderReceiver, ProductCapabilityId.ExtensionApi]),
        Group(
            "mobile-backup",
            startsHost: true,
            WebAuthScope.DeviceLease,
            [ProductCapabilityId.MobileBackupReceive]),
        Group(
            "computer-backup",
            startsHost: true,
            WebAuthScope.DeviceLease,
            [ProductCapabilityId.ComputerBackupReceive]),
        Group(
            "node-info",
            startsHost: false,
            WebAuthScope.None,
            [],
            [
                ProductCapabilityId.MediaLibrary,
                ProductCapabilityId.LanCollaboration,
                ProductCapabilityId.OrderReceiver,
                ProductCapabilityId.ExtensionApi
            ]),
        Group(
            "mobile-app",
            startsHost: false,
            WebAuthScope.None,
            [ProductCapabilityId.MobileApp]),
        Group(
            "local-settings",
            startsHost: false,
            WebAuthScope.Loopback,
            [])
    ];

    public static IReadOnlyList<string> ValidateDefinitions()
    {
        var errors = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (WebRouteGroup group in All)
        {
            if (!names.Add(group.Name))
            {
                errors.Add($"Web 路由组名称重复：{group.Name}");
            }
            if (!group.StartsHost && !group.RequiresHost)
            {
                errors.Add($"Web 路由组 {group.Name} 不能同时不启动 Host 且不要求 Host");
            }
            if (group.StartsHost && !group.RequiresHost)
            {
                errors.Add($"Web 路由组 {group.Name} 启动 Host 时必须同时要求 Host");
            }
        }
        return errors;
    }

    internal static WebHostPlan Resolve(
        ProductFeatures features,
        RoleCapabilities role)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(role);
        if (!role.CanStartWebHost)
        {
            return new WebHostPlan(false, Array.Empty<WebRouteGroup>());
        }

        bool startsHost = All.Any(
            group => role.AllowedWebRouteGroups.Contains(group.Name)
                && group.StartsHost
                && group.IsSatisfiedBy(features));
        if (!startsHost)
        {
            return new WebHostPlan(false, Array.Empty<WebRouteGroup>());
        }

        WebRouteGroup[] enabled = All
            .Where(group =>
                role.AllowedWebRouteGroups.Contains(group.Name)
                && group.RequiresHost
                && group.IsSatisfiedBy(features))
            .ToArray();
        return new WebHostPlan(true, enabled);
    }

    private static WebRouteGroup Group(
        string name,
        bool startsHost,
        WebAuthScope authScope,
        ProductCapabilityId[] requiredAllOf,
        ProductCapabilityId[]? requiredAnyOf = null) =>
        new(
            name,
            startsHost,
            RequiresHost: true,
            requiredAllOf.ToFrozenSet(),
            (requiredAnyOf ?? Array.Empty<ProductCapabilityId>()).ToFrozenSet(),
            authScope);
}
