using System.Net;
using System.Text.Json;
using ExpressPackingMonitoring.Config;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 只允许本机操作的设置接口：用途、录像保存位置与自启状态。
///
/// 这些是改动这台机器行为的设置，局域网里的手机、其它电脑一律不能碰，
/// 因此每个请求都要求 HttpListener 判定为本机来源（request.IsLocal）。
/// 路由独立于 WebServer.cs，便于单独测试与后续扩展。
/// </summary>
internal static class LocalSettingsEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static bool CanHandle(string path, string method) =>
        string.Equals(path, "/api/local-settings", StringComparison.OrdinalIgnoreCase)
        && (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase));

    internal static bool TryHandle(HttpListenerContext ctx, string path, string method)
    {
        if (!CanHandle(path, method)) return false;

        // 只允许本机：局域网请求直接拒绝，且不透露任何设置内容
        if (!ctx.Request.IsLocal)
        {
            WriteJson(ctx, 403, new
            {
                errorCode = "local_only",
                error = "设置只能在保存主机本机修改"
            });
            return true;
        }

        AppConfig config = WorkstationConfigStore.Load();
        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            WriteJson(ctx, 200, Describe(config));
            return true;
        }

        LocalSettingsRequest? request;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
            request = JsonSerializer.Deserialize<LocalSettingsRequest>(reader.ReadToEnd(), JsonOptions);
        }
        catch (JsonException)
        {
            WriteJson(ctx, 400, new { errorCode = "invalid_json", error = "请求内容不是合法 JSON" });
            return true;
        }

        if (request == null)
        {
            WriteJson(ctx, 400, new { errorCode = "invalid_request", error = "请求内容为空" });
            return true;
        }

        if (!TryApply(config, request.Purpose, request.StoragePath, out string error, out bool changed))
        {
            WriteJson(ctx, 400, new { errorCode = "invalid_request", error });
            return true;
        }

        if (changed && !WorkstationConfigStore.TrySave(config, out error))
        {
            WriteJson(ctx, 500, new { errorCode = "save_failed", error });
            return true;
        }

        WriteJson(ctx, 200, new
        {
            saved = changed,
            restartRequired = changed,
            settings = Describe(WorkstationConfigStore.Load())
        });
        return true;
    }

    /// <summary>把配置整理成界面需要的字段；可单独测试。</summary>
    internal static object Describe(AppConfig config) => new
    {
        purpose = DeploymentPresets.Normalize(config.DeploymentPreset),
        purposeName = DeploymentPresets.GetDisplayName(config.DeploymentPreset),
        storagePath = config.StorageLocations?.FirstOrDefault()?.Path ?? "",
        storageAvailable = IsStorageUsable(config.StorageLocations?.FirstOrDefault()?.Path),
        autostartInstalled = AutostartPlistExists(),
        hostUrl = "",
        version = AppVersion.Current
    };

    /// <summary>
    /// 校验并写入用途与存储位置；返回 false 时 error 说明原因。
    /// changed 表示是否真的改了配置（用于决定要不要提示重启）。
    /// </summary>
    internal static bool TryApply(
        AppConfig config,
        string? purpose,
        string? storagePath,
        out string error,
        out bool changed)
    {
        error = "";
        changed = false;
        ArgumentNullException.ThrowIfNull(config);

        string normalizedPurpose = (purpose ?? "").Trim();
        if (normalizedPurpose.Length > 0)
        {
            string mapped = normalizedPurpose.ToLowerInvariant() switch
            {
                "host" => DeploymentPresets.MobileBackupHost,
                "viewer" => DeploymentPresets.ViewerClient,
                _ => ""
            };
            if (mapped.Length == 0)
            {
                error = "用途只能是保存主机或查看端";
                return false;
            }

            if (!string.Equals(config.DeploymentPreset, mapped, StringComparison.Ordinal))
            {
                config.DeploymentPreset = mapped;
                AppConfig.NormalizeAfterLoad(config);
                changed = true;
            }
        }

        string path = (storagePath ?? "").Trim();
        if (path.Length > 0)
        {
            if (!Path.IsPathRooted(path))
            {
                error = "存储位置必须是绝对路径";
                return false;
            }

            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                error = $"无法使用该目录：{ex.Message}";
                return false;
            }

            string current = config.StorageLocations?.FirstOrDefault()?.Path ?? "";
            if (!string.Equals(current, path, StringComparison.Ordinal))
            {
                config.StorageLocations =
                [
                    new StorageLocation { Path = path, Priority = 1, IsBackupTarget = false }
                ];
                changed = true;
            }
        }

        if (changed) AppConfig.MarkDeploymentSetupCompleted(config);
        return true;
    }

    private static bool IsStorageUsable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return Path.IsPathRooted(path) && Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static bool AutostartPlistExists()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "LaunchAgents",
                "com.packingproof.host.plist");
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteJson(HttpListenerContext ctx, int statusCode, object payload)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    internal sealed class LocalSettingsRequest
    {
        public string? Purpose { get; set; }
        public string? StoragePath { get; set; }
    }
}
