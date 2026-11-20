using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace ExpressPackingMonitoring.Services;

internal enum WindowsDevicePermissionState
{
    Unavailable,
    Allowed,
    DeniedByUser,
    DeniedBySystem,
    UserPromptRequired,
    NotDeclaredByApp
}

internal readonly record struct WindowsDevicePermissionSnapshot(
    WindowsDevicePermissionState Camera,
    WindowsDevicePermissionState Microphone)
{
    internal bool CameraNeedsRecovery =>
        WindowsDevicePermissionService.NeedsRecovery(Camera);

    internal bool MicrophoneNeedsRecovery =>
        WindowsDevicePermissionService.NeedsRecovery(Microphone);

    internal bool AnyNeedsRecovery =>
        CameraNeedsRecovery || MicrophoneNeedsRecovery;
}

internal static class WindowsDevicePermissionService
{
    internal const string CameraCapabilityName = "webcam";
    internal const string MicrophoneCapabilityName = "microphone";
    internal const string PrivacySettingsUri = "ms-settings:privacy";
    internal const string CameraSettingsUri = "ms-settings:privacy-webcam";
    internal const string MicrophoneSettingsUri = "ms-settings:privacy-microphone";

    private const string SdkAssemblyFile = "Microsoft.Windows.SDK.NET.dll";
    private const string AppCapabilityTypeName =
        "Windows.Security.Authorization.AppCapabilityAccess.AppCapability";

    private static readonly Lazy<Type?> AppCapabilityType = new(
        () => LoadAppCapabilityType(AppContext.BaseDirectory));

    internal static bool NeedsRecovery(WindowsDevicePermissionState state) =>
        state is WindowsDevicePermissionState.DeniedByUser
            or WindowsDevicePermissionState.DeniedBySystem;

    internal static Type? LoadAppCapabilityTypeForTests(string baseDirectory) =>
        LoadAppCapabilityType(baseDirectory);

    internal static async Task<WindowsDevicePermissionSnapshot> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        if (!PackagingIdentity.IsPackaged)
        {
            return new(
                WindowsDevicePermissionState.Unavailable,
                WindowsDevicePermissionState.Unavailable);
        }

        try
        {
            return await Task.Run(
                () => new WindowsDevicePermissionSnapshot(
                    QueryAccessState(CameraCapabilityName),
                    QueryAccessState(MicrophoneCapabilityName)),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(
                "DevicePermission",
                $"Failed to query Windows device access: {ex.Message}");
            return new(
                WindowsDevicePermissionState.Unavailable,
                WindowsDevicePermissionState.Unavailable);
        }
    }

    internal static Task<bool> OpenPrivacySettingsAsync() =>
        LaunchSettingsAsync(PrivacySettingsUri);

    internal static Task<bool> OpenCameraSettingsAsync() =>
        LaunchSettingsAsync(CameraSettingsUri);

    internal static Task<bool> OpenMicrophoneSettingsAsync() =>
        LaunchSettingsAsync(MicrophoneSettingsUri);

    private static WindowsDevicePermissionState QueryAccessState(
        string capabilityName)
    {
        try
        {
            Type? capabilityType = AppCapabilityType.Value;
            if (capabilityType == null)
                return WindowsDevicePermissionState.Unavailable;

            MethodInfo? create = capabilityType.GetMethod(
                "Create",
                BindingFlags.Public | BindingFlags.Static);
            MethodInfo? checkAccess = capabilityType.GetMethod(
                "CheckAccess",
                BindingFlags.Public | BindingFlags.Instance);
            if (create == null || checkAccess == null)
                return WindowsDevicePermissionState.Unavailable;

            object? capability = create.Invoke(null, [capabilityName]);
            object? status = capability == null
                ? null
                : checkAccess.Invoke(capability, null);
            return status?.ToString() switch
            {
                "Allowed" =>
                    WindowsDevicePermissionState.Allowed,
                "DeniedByUser" =>
                    WindowsDevicePermissionState.DeniedByUser,
                "DeniedBySystem" =>
                    WindowsDevicePermissionState.DeniedBySystem,
                "UserPromptRequired" =>
                    WindowsDevicePermissionState.UserPromptRequired,
                "NotDeclaredByApp" =>
                    WindowsDevicePermissionState.NotDeclaredByApp,
                _ => WindowsDevicePermissionState.Unavailable
            };
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(
                "DevicePermission",
                $"Failed to query {capabilityName} access: {ex.Message}");
            return WindowsDevicePermissionState.Unavailable;
        }
    }

    private static Type? LoadAppCapabilityType(string baseDirectory)
    {
        try
        {
            string sdkPath = Path.Combine(baseDirectory, SdkAssemblyFile);
            if (!File.Exists(sdkPath))
            {
                RuntimeLog.Warn(
                    "DevicePermission",
                    $"Windows SDK projection is missing: {sdkPath}");
                return null;
            }

            var context = new AssemblyLoadContext(
                "PackingProof.DevicePermissions",
                isCollectible: false);
            context.Resolving += (loadContext, assemblyName) =>
            {
                string dependencyPath = Path.Combine(
                    baseDirectory,
                    assemblyName.Name + ".dll");
                return File.Exists(dependencyPath)
                    ? loadContext.LoadFromAssemblyPath(dependencyPath)
                    : null;
            };
            Assembly sdkAssembly = context.LoadFromAssemblyPath(sdkPath);
            return sdkAssembly.GetType(AppCapabilityTypeName);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(
                "DevicePermission",
                $"Failed to load Windows SDK projection: {ex.Message}");
            return null;
        }
    }

    private static Task<bool> LaunchSettingsAsync(string uri) =>
        Task.Run(() =>
        {
            try
            {
                using Process? process = Process.Start(
                    new ProcessStartInfo(uri)
                    {
                        UseShellExecute = true
                    });
                return process != null;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn(
                    "DevicePermission",
                    $"Failed to open Windows settings {uri}: {ex.Message}");
                return false;
            }
        });
}
