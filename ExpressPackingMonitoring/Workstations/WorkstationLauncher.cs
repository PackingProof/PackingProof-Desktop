using ExpressPackingMonitoring.Config;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.ViewModels;

namespace ExpressPackingMonitoring;

public static class WorkstationRoles
{
    public const string CameraMonitor = "CameraMonitor";
    public const string PrintStation = "PrintStation";

    public static bool IsKnown(string? role) =>
        string.Equals(role, CameraMonitor, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(role, PrintStation, StringComparison.OrdinalIgnoreCase);

    public static string GetDisplayName(string? role) =>
        string.Equals(role, PrintStation, StringComparison.OrdinalIgnoreCase) ? "我没有电脑摄像头" : "使用电脑摄像头录像";

    public static string GetOtherRole(string role) =>
        string.Equals(role, PrintStation, StringComparison.OrdinalIgnoreCase) ? CameraMonitor : PrintStation;
}

public static class WorkstationConfigStore
{
    private const string ConfigMutexName = @"Local\ExpressPackingMonitoring.Config";
    private static readonly object SaveLock = new();
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static AppConfig Load()
    {
        string backupPath = AppPaths.ConfigPath + ".bak";
        foreach (string path in new[] { AppPaths.ConfigPath, backupPath })
        {
            if (!File.Exists(path))
                continue;

            try
            {
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path, Encoding.UTF8)) ?? new AppConfig();
                bool changed = AppConfig.NormalizeAfterLoad(config);
                changed = EnsureAppRootDirectory(config, AppContext.BaseDirectory) || changed;
                if (changed)
                {
                    try { Save(config); }
                    catch (Exception ex) { RuntimeLog.Warn("Config", $"Normalized config save failed: {ex.Message}"); }
                }

                if (!string.Equals(path, AppPaths.ConfigPath, StringComparison.OrdinalIgnoreCase))
                {
                    RuntimeLog.Warn("Config", "Primary config invalid, loaded backup config");
                    try { Save(config); }
                    catch (Exception ex) { RuntimeLog.Warn("Config", $"Backup config restore failed: {ex.Message}"); }
                }
                return config;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Config", $"Config load failed file={Path.GetFileName(path)}, error={ex.Message}");
            }
        }

        var defaultConfig = new AppConfig();
        AppConfig.NormalizeAfterLoad(defaultConfig);
        EnsureAppRootDirectory(defaultConfig, AppContext.BaseDirectory);
        try { Save(defaultConfig); }
        catch (Exception ex) { RuntimeLog.Warn("Config", $"Initial config save failed: {ex.Message}"); }
        return defaultConfig;
    }

    public static void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        ExecuteWithSaveLock(() =>
        {
            if (TryReadConfig(AppPaths.ConfigPath, out AppConfig latest))
                config.PrintStationMonitorAddress = latest.PrintStationMonitorAddress;
            SaveCore(config);
        });
    }

    public static bool TrySave(AppConfig config, out string error)
    {
        try
        {
            Save(config);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            RuntimeLog.Error("Config", "Config save failed", ex);
            return false;
        }
    }

    public static bool TryUpdate(Action<AppConfig> update, out AppConfig savedConfig, out string error)
    {
        ArgumentNullException.ThrowIfNull(update);
        try
        {
            AppConfig result = new();
            ExecuteWithSaveLock(() =>
            {
                result = ReadCurrentConfig();
                update(result);
                AppConfig.NormalizeAfterLoad(result);
                SaveCore(result);
            });
            savedConfig = result;
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            savedConfig = new AppConfig();
            error = ex.Message;
            RuntimeLog.Error("Config", "Config update failed", ex);
            return false;
        }
    }

    private static AppConfig ReadCurrentConfig()
    {
        if (TryReadConfig(AppPaths.ConfigPath, out AppConfig config))
            return config;
        if (TryReadConfig(AppPaths.ConfigPath + ".bak", out config))
            return config;

        config = new AppConfig();
        AppConfig.NormalizeAfterLoad(config);
        return config;
    }

    private static bool TryReadConfig(string path, out AppConfig config)
    {
        config = new AppConfig();
        if (!File.Exists(path)) return false;
        try
        {
            var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path, Encoding.UTF8));
            if (loaded == null) return false;
            config = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SaveCore(AppConfig config)
    {
        EnsureAppRootDirectory(config, AppContext.BaseDirectory);
        string configPath = AppPaths.ConfigPath;
        string directory = Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory;
        string tempPath = $"{configPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        string backupPath = configPath + ".bak";
        Directory.CreateDirectory(directory);

        try
        {
            string json = JsonSerializer.Serialize(config, Options);
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(configPath))
                File.Replace(tempPath, configPath, backupPath, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, configPath);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    internal static bool EnsureAppRootDirectory(AppConfig config, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(config);
        string normalized = NormalizeAppRootDirectory(baseDirectory);
        if (string.Equals(config.AppRootDirectory, normalized, StringComparison.OrdinalIgnoreCase))
            return false;

        config.AppRootDirectory = normalized;
        return true;
    }

    internal static string NormalizeAppRootDirectory(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new ArgumentException("应用根目录不能为空", nameof(baseDirectory));

        string fullPath = Path.GetFullPath(baseDirectory);
        string root = Path.GetPathRoot(fullPath) ?? "";
        return fullPath.Length > root.Length
            ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : fullPath;
    }

    private static void ExecuteWithSaveLock(Action action)
    {
        lock (SaveLock)
        {
            using var mutex = new Mutex(false, ConfigMutexName);
            bool ownsMutex = false;
            try
            {
                try { ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { ownsMutex = true; }
                if (!ownsMutex)
                    throw new TimeoutException("等待其他程序保存配置超时");
                action();
            }
            finally
            {
                if (ownsMutex)
                    mutex.ReleaseMutex();
            }
        }
    }
}

public static class WorkstationNetwork
{
    private const int DefaultHttpPort = 5280;
    private const int MaxSubnetDiscoveryHosts = 1022;
    private sealed record PendingRestart(string ExecutablePath, string WorkingDirectory, string Reason);

    private static readonly HttpClient Client = CreateLanHttpClient(TimeSpan.FromSeconds(3));
    private static readonly HttpClient TestOrderClient = CreateLanHttpClient(TimeSpan.FromSeconds(3));
    private static readonly HttpClient LoopbackClient = CreateLanHttpClient(TimeSpan.FromSeconds(3));
    private static readonly JsonSerializerOptions NetworkJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly object RestartLock = new();
    private static PendingRestart? _pendingRestart;

    private static HttpClient CreateLanHttpClient(TimeSpan timeout) =>
        new(CreateLanHttpMessageHandler()) { Timeout = timeout };

    internal static SocketsHttpHandler CreateLanHttpMessageHandler() =>
        new() { UseProxy = false };

    public static string NormalizeAddress(string input, int defaultPort = 5280)
    {
        input = (input ?? "").Trim();
        if (Uri.TryCreate(input, UriKind.Absolute, out Uri? uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            int port = uri.IsDefaultPort ? defaultPort : uri.Port;
            return $"{uri.Host}:{port}";
        }
        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            input = input[7..];
        if (input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            input = input[8..];
        int suffixIndex = input.IndexOfAny(['/', '?', '#']);
        if (suffixIndex >= 0)
            input = input[..suffixIndex];
        input = input.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(input)) return "";
        return input.Contains(':') ? input : $"{input}:{defaultPort}";
    }

    public static string ToUrl(string address) => $"http://{NormalizeAddress(address)}";

    public static void ParseHostConnectionInput(
        string input,
        out string address,
        out string accessKey)
    {
        input = (input ?? "").Trim();
        accessKey = "";
        if (Uri.TryCreate(input, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            address = $"{uri.Host}:{(uri.IsDefaultPort ? DefaultHttpPort : uri.Port)}";
            foreach (string item in uri.Query.TrimStart('?').Split(
                         '&',
                         StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = item.Split('=', 2);
                if (pair.Length == 2
                    && string.Equals(
                        Uri.UnescapeDataString(pair[0]),
                        "key",
                        StringComparison.OrdinalIgnoreCase))
                {
                    accessKey = Uri.UnescapeDataString(pair[1]).Trim();
                    break;
                }
            }
            return;
        }
        address = NormalizeAddress(input);
    }

    public static async Task<bool> CanConnectAsync(string address)
        => await GetNodeInfoAsync(address) != null;

    public static async Task<PackingProofNodeInfo?> GetNodeInfoAsync(
        string address,
        CancellationToken token = default)
    {
        address = NormalizeAddress(address);
        if (string.IsNullOrWhiteSpace(address)) return null;

        try
        {
            // 局域网扫描使用短超时快速跳过不可达地址；回环主机是本机服务，
            // 首次请求在慢速/高负载环境（如 CI）可能超过 800ms，使用更宽松的超时。
            HttpClient client = IsLoopbackAddress(address) ? LoopbackClient : Client;
            using var response = await client.GetAsync($"{ToUrl(address)}/api/node-info", token);
            if (!response.IsSuccessStatusCode)
                return null;

            await using Stream stream = await response.Content.ReadAsStreamAsync(token);
            PackingProofNodeInfo? node = await JsonSerializer.DeserializeAsync<PackingProofNodeInfo>(
                stream,
                NetworkJsonOptions,
                token);
            if (node?.IsValidHost != true)
                return null;

            // 地址以「实际请求连接的 IP + node-info 返回的权威 httpPort」为准，
            // 不信任请求时用的候选端口，避免端口不一致/多网卡时把身份和地址混在一起。
            string host = NormalizeAddress(address).Split(':')[0];
            node.Address = ToUrl($"{host}:{node.HttpPort}");
            return node;
        }
        catch (Exception ex) when (ex is HttpRequestException
            or TaskCanceledException
            or JsonException
            or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsLoopbackAddress(string address)
    {
        string host = NormalizeAddress(address).Split(':')[0];
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(host, out IPAddress? ip) && IPAddress.IsLoopback(ip);
    }

    public static async Task<IReadOnlyList<RecordingDeviceInfo>> GetRecordingDevicesAsync(
        string address,
        bool includeKnown = false,
        CancellationToken token = default)
    {
        address = NormalizeAddress(address);
        if (address.Length == 0)
            return Array.Empty<RecordingDeviceInfo>();

        try
        {
            string scope = includeKnown ? "?scope=known" : "";
            using var response = await Client.GetAsync(
                $"{ToUrl(address)}/api/recording-devices{scope}",
                token);
            if (!response.IsSuccessStatusCode)
                return Array.Empty<RecordingDeviceInfo>();

            await using Stream stream = await response.Content.ReadAsStreamAsync(token);
            RecordingDevicesResponse? payload = await JsonSerializer.DeserializeAsync<RecordingDevicesResponse>(
                stream,
                NetworkJsonOptions,
                token);
            return payload?.Devices?
                .Where(device => device != null)
                .ToArray() ?? Array.Empty<RecordingDeviceInfo>();
        }
        catch (Exception ex) when (ex is HttpRequestException
            or TaskCanceledException
            or JsonException
            or InvalidOperationException)
        {
            return Array.Empty<RecordingDeviceInfo>();
        }
    }

    private sealed class RecordingDevicesResponse
    {
        public List<RecordingDeviceInfo> Devices { get; set; } = [];
    }

    public static async Task<bool> SendConnectionHeartbeatAsync(
        string address,
        string clientId,
        bool connected = true,
        CancellationToken token = default)
    {
        address = NormalizeAddress(address);
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(clientId)) return false;
        try
        {
            var payload = new
            {
                clientId,
                clientType = "print-station",
                displayName = "手机录像备份",
                connected
            };
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await Client.PostAsync($"{ToUrl(address)}/api/connections/heartbeat", content, token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public sealed class TestOrderSendResult
    {
        public bool Sent { get; init; }
        public bool MonitorConfirmed { get; init; }
        public int TestCount { get; init; }
        public string ErrorMessage { get; init; } = "";
    }

    public sealed class TestOrderDeviceResult
    {
        public string NodeId { get; init; } = "";
        public string NodeName { get; init; } = "";
        public string Address { get; init; } = "";
        public bool Sent { get; init; }
        public bool MonitorConfirmed { get; init; }
        public int TestCount { get; init; }
        public string ErrorMessage { get; init; } = "";
        public bool Succeeded => Sent && MonitorConfirmed;
    }

    public sealed class TestOrderBroadcastResult
    {
        public IReadOnlyList<TestOrderDeviceResult> Devices { get; init; } = [];
        public string ErrorMessage { get; init; } = "";
        public int SuccessCount => Devices.Count(device => device.Succeeded);
        public int FailureCount => Devices.Count - SuccessCount;
        public bool HasTargets => Devices.Count > 0;
    }

    public static async Task<TestOrderSendResult> SendTestOrderAsync(
        string address,
        CancellationToken token = default)
    {
        address = NormalizeAddress(address);
        if (string.IsNullOrWhiteSpace(address))
            return new TestOrderSendResult { ErrorMessage = "本机服务地址为空" };

        var order = new[]
        {
            new
            {
                trackingNumber = $"TEST{DateTime.Now:HHmmss}",
                orderId = "测试订单",
                buyerMessage = "这是一条测试买家留言",
                sellerMemo = "这是一条测试卖家备注",
                productInfo = "测试商品",
                isTest = true
            }
        };

        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(order), Encoding.UTF8, "application/json");
            using var response = await TestOrderClient.PostAsync(
                $"{ToUrl(address)}/api/orderinfo",
                content,
                token);
            if (!response.IsSuccessStatusCode)
                return new TestOrderSendResult { ErrorMessage = $"HTTP {(int)response.StatusCode}" };

            string body = await response.Content.ReadAsStringAsync(token);
            int testCount;
            try
            {
                using var doc = JsonDocument.Parse(body);
                testCount = doc.RootElement.TryGetProperty("testCount", out JsonElement countElement)
                    && countElement.TryGetInt32(out int value)
                    ? value
                    : 0;
            }
            catch
            {
                return new TestOrderSendResult
                {
                    Sent = true,
                    ErrorMessage = "设备返回无效响应"
                };
            }

            return new TestOrderSendResult
            {
                Sent = true,
                MonitorConfirmed = testCount > 0,
                TestCount = testCount,
                ErrorMessage = testCount > 0 ? "" : "设备未确认收到测试订单"
            };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new TestOrderSendResult { ErrorMessage = "请求超时" };
        }
        catch (HttpRequestException)
        {
            return new TestOrderSendResult { ErrorMessage = "无法连接设备" };
        }
        catch (Exception ex)
        {
            return new TestOrderSendResult { ErrorMessage = ex.Message };
        }
    }

    public static async Task<RecordingWorkstationHeartbeatResult> SendRecordingWorkstationHeartbeatAsync(
        string address,
        string nodeId,
        string nodeName,
        int orderReceiverPort,
        bool connected = true,
        bool nicknameCustomized = false,
        CancellationToken token = default)
    {
        address = NormalizeAddress(address);
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(nodeId))
            return new RecordingWorkstationHeartbeatResult(false, "");

        try
        {
            var payload = new
            {
                clientId = nodeId,
                clientType = "recording-workstation",
                displayName = string.IsNullOrWhiteSpace(nodeName) ? Environment.MachineName : nodeName.Trim(),
                nicknameCustomized,
                connected,
                nodeId,
                deviceType = "pc",
                orderReceiverPort,
                capabilities = new[] { "recording", "order-receiver" }
            };
            using var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");
            using var response = await Client.PostAsync(
                $"{ToUrl(address)}/api/connections/heartbeat",
                content,
                token);
            if (!response.IsSuccessStatusCode)
                return new RecordingWorkstationHeartbeatResult(false, "");

            string assignedDisplayName = "";
            try
            {
                string responseBody = await response.Content.ReadAsStringAsync(token);
                using JsonDocument document = JsonDocument.Parse(responseBody);
                if (document.RootElement.TryGetProperty("assignedDisplayName", out JsonElement assignedElement)
                    && assignedElement.ValueKind == JsonValueKind.String)
                {
                    assignedDisplayName = assignedElement.GetString()?.Trim() ?? "";
                }
            }
            catch
            {
                // 旧主机可能不返回该字段或返回空响应，在线状态仍然有效。
            }
            return new RecordingWorkstationHeartbeatResult(true, assignedDisplayName);
        }
        catch
        {
            return new RecordingWorkstationHeartbeatResult(false, "");
        }
    }

    internal static async Task<BackupDeviceEnrollmentResult> EnrollBackupDeviceAsync(
        string address,
        string deviceId,
        string deviceName,
        string deviceKind,
        CancellationToken token = default,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
        string? clientVersion = null,
        string? platform = null)
    {
        address = NormalizeAddress(address);
        if (address.Length == 0 || string.IsNullOrWhiteSpace(deviceId))
            throw new InvalidOperationException("保存主机地址或本机身份无效");
        using var client = CreateLanHttpClient(TimeSpan.FromSeconds(90));
        retryDelay ??= Task.Delay;
        string resolvedClientVersion = string.IsNullOrWhiteSpace(clientVersion)
            ? BackupCompatibilityPolicy.MinimumDesktopVersion
            : clientVersion.Trim();
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{ToUrl(address)}/api/mobile-backup/enroll")
            {
                Content = JsonContent.Create(new
                {
                    deviceId = deviceId.Trim(),
                    deviceName = deviceName?.Trim() ?? "",
                    deviceKind = string.Equals(deviceKind, "pc", StringComparison.OrdinalIgnoreCase)
                        ? "pc"
                        : string.Equals(deviceKind, "viewer", StringComparison.OrdinalIgnoreCase)
                            ? "viewer"
                            : "mobile",
                    platform = platform ?? "",
                    clientVersion = resolvedClientVersion,
                    clientBuildNumber = 0,
                    backupProtocol = BackupCompatibilityPolicy.BackupProtocol,
                    enrollmentVersion = BackupCompatibilityPolicy.EnrollmentVersion,
                    authVersion = BackupCompatibilityPolicy.AuthenticationVersion
                })
            };
            using HttpResponseMessage response = await client.SendAsync(request, token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException("保存主机版本过旧，请先更新电脑端");
            if ((int)response.StatusCode == 429)
            {
                BackupCompatibilityError? busy = await response.Content.ReadFromJsonAsync<BackupCompatibilityError>(
                    NetworkJsonOptions,
                    token);
                if (string.Equals(busy?.ErrorCode, "enrollment_approval_busy", StringComparison.OrdinalIgnoreCase)
                    && attempt < 24)
                {
                    await retryDelay(TimeSpan.FromSeconds(3), token);
                    continue;
                }
                throw new InvalidOperationException("连接请求过于频繁，请稍后重试");
            }
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new InvalidOperationException("保存主机未允许本机连接，可重新申请并在保存主机上点“允许连接”");
            if (response.StatusCode == HttpStatusCode.Conflict)
                throw new InvalidOperationException("这台电脑当前不是录像文件备份主机");
            if ((int)response.StatusCode == 426)
            {
                BackupCompatibilityError? error = await response.Content.ReadFromJsonAsync<BackupCompatibilityError>(
                    NetworkJsonOptions,
                    token);
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error?.Error)
                        ? "当前录制工位版本过低，请更新电脑端后重新连接"
                        : error.Error);
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("保存主机暂时无法处理连接申请，请稍后重试");
            BackupDeviceEnrollmentResult? result = await response.Content.ReadFromJsonAsync<BackupDeviceEnrollmentResult>(
                NetworkJsonOptions,
                token);
            if (result == null
                || result.Protocol != "mobile-backup-v2"
                || result.Version != 2
                || result.AuthVersion != BackupRequestAuthentication.CurrentVersion
                || result.DeviceToken.Length < 32
                || !string.Equals(result.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("保存主机返回的设备令牌无效");
            }
            return result;
        }
    }

    public static async Task<TestOrderBroadcastResult> SendTestOrderToRecordingDevicesAsync(
        string hostAddress,
        CancellationToken token = default)
    {
        PackingProofNodeInfo? host = await GetNodeInfoAsync(hostAddress, token);
        if (host == null)
        {
            return new TestOrderBroadcastResult
            {
                ErrorMessage = "PackingProof 主机离线或无法访问"
            };
        }

        IReadOnlyList<RecordingDeviceInfo> devices = await GetRecordingDevicesAsync(
            host.Address,
            includeKnown: false,
            token);
        return await SendTestOrderToRecordingDevicesAsync(devices, token);
    }

    public static async Task<TestOrderBroadcastResult> SendTestOrderToRecordingDevicesAsync(
        IEnumerable<RecordingDeviceInfo>? recordingDevices,
        CancellationToken token = default)
    {
        RecordingDeviceInfo[] devices = (recordingDevices ?? [])
            .Where(device => device != null && device.Online)
            .Select(device => new
            {
                Device = device,
                Address = NormalizeAddress(device.Address)
            })
            .Where(item => item.Address.Length > 0)
            .GroupBy(item => item.Address, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First().Device)
            .ToArray();
        if (devices.Length == 0)
        {
            return new TestOrderBroadcastResult
            {
                ErrorMessage = "当前没有在线的录像设备"
            };
        }

        Task<TestOrderDeviceResult>[] tasks = devices
            .Select(async device =>
            {
                TestOrderSendResult result = await SendTestOrderAsync(device.Address, token);
                return new TestOrderDeviceResult
                {
                    NodeId = device.NodeId,
                    NodeName = device.NodeName,
                    Address = device.Address,
                    Sent = result.Sent,
                    MonitorConfirmed = result.MonitorConfirmed,
                    TestCount = result.TestCount,
                    ErrorMessage = result.ErrorMessage
                };
            })
            .ToArray();
        TestOrderDeviceResult[] results = await Task.WhenAll(tasks);
        return new TestOrderBroadcastResult { Devices = results };
    }

    public static string FormatTestOrderBroadcastResult(TestOrderBroadcastResult result)
    {
        if (!result.HasTargets)
            return string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? "当前没有在线的录像设备"
                : result.ErrorMessage;

        var lines = new List<string>
        {
            $"成功 {result.SuccessCount} 台，失败 {result.FailureCount} 台"
        };
        lines.AddRange(result.Devices.Select(device =>
        {
            string name = string.IsNullOrWhiteSpace(device.NodeName)
                ? device.Address
                : device.NodeName;
            string status = device.Succeeded
                ? "成功"
                : $"失败：{(string.IsNullOrWhiteSpace(device.ErrorMessage) ? "未确认收到测试订单" : device.ErrorMessage)}";
            return $"{name}（{NormalizeAddress(device.Address)}）：{status}";
        }));
        return string.Join(Environment.NewLine, lines);
    }

    public static async Task<string?> FindMonitorAsync(int port, IProgress<string>? progress = null, CancellationToken token = default)
    {
        IReadOnlyList<PackingProofNodeInfo> hosts = await FindHostsAsync(
            lastKnownAddress: null,
            port,
            progress,
            hostProgress: null,
            token: token);
        return hosts.Count == 0 ? null : NormalizeAddress(hosts[0].Address);
    }

    internal sealed class HostReporter
    {
        private readonly object _lock = new();
        private readonly HashSet<string> _seenNodeIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenAddresses = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<PackingProofNodeInfo> _hosts = new();
        private readonly IProgress<PackingProofNodeInfo>? _progress;

        public HostReporter(IProgress<PackingProofNodeInfo>? progress) => _progress = progress;

        public void Report(PackingProofNodeInfo? node)
        {
            if (node?.IsValidHost != true)
                return;

            string address = NormalizeAddress(node.Address);
            lock (_lock)
            {
                if (!_seenNodeIds.Add(node.NodeId))
                    return;
                if (!_seenAddresses.Add(address))
                    return;
                _hosts.Add(node);
            }
            _progress?.Report(node);
        }

        public IReadOnlyList<PackingProofNodeInfo> ToList()
        {
            lock (_lock)
            {
                return _hosts.ToList();
            }
        }
    }

    public static async Task<IReadOnlyList<PackingProofNodeInfo>> FindHostsAsync(
        string? lastKnownAddress,
        int port,
        IProgress<string>? progress = null,
        IProgress<PackingProofNodeInfo>? hostProgress = null,
        CancellationToken token = default)
    {
        IEnumerable<string> candidates = GetLocalIpv4ScanAddresses()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(address => GetDiscoveryPorts(port).Select(discoveryPort => $"{address}:{discoveryPort}"));

        var reporter = new HostReporter(hostProgress);
        Task udpTask = DiscoverUdpHostsAsync(reporter, progress, token);
        Task httpTask = DiscoverHostsAsync(
            lastKnownAddress,
            candidates,
            GetNodeInfoAsync,
            reporter,
            progress,
            token);

        await Task.WhenAll(udpTask, httpTask).ConfigureAwait(false);
        return reporter.ToList();
    }

    public static Task<PackingProofNodeInfo?> FindHostByNodeIdAsync(
        string nodeId,
        string? lastKnownAddress,
        int port,
        CancellationToken token = default) =>
        FindHostByNodeIdAsync(
            nodeId,
            lastKnownAddress,
            GetNodeInfoAsync,
            (progress, discoveryToken) => FindHostsAsync(
                lastKnownAddress: null,
                port,
                progress: null,
                hostProgress: progress,
                token: discoveryToken),
            token);

    internal static async Task<PackingProofNodeInfo?> FindHostByNodeIdAsync(
        string nodeId,
        string? lastKnownAddress,
        Func<string, CancellationToken, Task<PackingProofNodeInfo?>> probe,
        Func<IProgress<PackingProofNodeInfo>, CancellationToken, Task<IReadOnlyList<PackingProofNodeInfo>>> discover,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(discover);
        string targetNodeId = nodeId?.Trim() ?? "";
        if (targetNodeId.Length == 0)
            return null;

        string savedAddress = NormalizeAddress(lastKnownAddress ?? "");
        if (savedAddress.Length > 0)
        {
            PackingProofNodeInfo? savedNode = await probe(savedAddress, token).ConfigureAwait(false);
            if (IsMatchingHost(savedNode, targetNodeId))
                return savedNode;
        }

        using var discoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var matchSource = new TaskCompletionSource<PackingProofNodeInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var matchProgress = new CallbackProgress<PackingProofNodeInfo>(node =>
        {
            if (IsMatchingHost(node, targetNodeId))
                matchSource.TrySetResult(node);
        });
        Task<IReadOnlyList<PackingProofNodeInfo>> discoveryTask = discover(
            matchProgress,
            discoveryCancellation.Token);
        await Task.WhenAny(discoveryTask, matchSource.Task).ConfigureAwait(false);

        if (matchSource.Task.IsCompletedSuccessfully)
        {
            discoveryCancellation.Cancel();
            try
            {
                await discoveryTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // 已解析到目标节点，无需继续扫描其余地址
            }
            return await matchSource.Task.ConfigureAwait(false);
        }

        IReadOnlyList<PackingProofNodeInfo> hosts = await discoveryTask.ConfigureAwait(false);
        return hosts.FirstOrDefault(node => IsMatchingHost(node, targetNodeId));
    }

    private static bool IsMatchingHost(PackingProofNodeInfo? node, string nodeId) =>
        node?.IsValidHost == true
        && string.Equals(node.NodeId, nodeId, StringComparison.OrdinalIgnoreCase);

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        private readonly Action<T> _callback = callback ?? throw new ArgumentNullException(nameof(callback));

        public void Report(T value) => _callback(value);
    }

    private static async Task DiscoverUdpHostsAsync(
        HostReporter reporter,
        IProgress<string>? progress,
        CancellationToken token)
    {
        progress?.Report("正在通过局域网广播查找主机");
        try
        {
            await foreach (UdpDiscoveryClient.Announce announce in UdpDiscoveryClient
                .DiscoverAsync(token)
                .ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                PackingProofNodeInfo? node = await GetNodeInfoAsync(
                    $"{announce.SourceIp}:{announce.HttpPort}",
                    token).ConfigureAwait(false);
                reporter.Report(node);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // UDP 探测失败不影响 HTTP 兜底。
        }
    }

    internal static Task<IReadOnlyList<PackingProofNodeInfo>> DiscoverHostsAsync(
        string? lastKnownAddress,
        IEnumerable<string> candidates,
        Func<string, CancellationToken, Task<PackingProofNodeInfo?>> probe,
        IProgress<string>? progress = null,
        CancellationToken token = default)
    {
        return DiscoverHostsAsync(lastKnownAddress, candidates, probe, null, progress, token);
    }

    internal static async Task<IReadOnlyList<PackingProofNodeInfo>> DiscoverHostsAsync(
        string? lastKnownAddress,
        IEnumerable<string> candidates,
        Func<string, CancellationToken, Task<PackingProofNodeInfo?>> probe,
        HostReporter? reporter,
        IProgress<string>? progress = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(probe);

        var hosts = new List<PackingProofNodeInfo>();
        var seenNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var discoveryLock = new object();

        async Task ProbeAndAddAsync(string candidate)
        {
            token.ThrowIfCancellationRequested();
            string normalized = NormalizeAddress(candidate);
            if (normalized.Length == 0)
                return;
            lock (discoveryLock)
            {
                if (!seenAddresses.Add(normalized))
                    return;
            }

            PackingProofNodeInfo? node = await probe(normalized, token);
            if (node?.IsValidHost == true)
            {
                reporter?.Report(node);
                lock (discoveryLock)
                {
                    if (seenNodeIds.Add(node.NodeId))
                        hosts.Add(node);
                }
            }
        }

        string savedAddress = NormalizeAddress(lastKnownAddress ?? "");
        if (savedAddress.Length > 0)
        {
            progress?.Report("正在验证上次连接的主机");
            await ProbeAndAddAsync(savedAddress);
        }

        string[] pending = candidates
            .Select(NormalizeAddress)
            .Where(address => address.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        for (int start = 0; start < pending.Length; start += 32)
        {
            token.ThrowIfCancellationRequested();
            string[] batch = pending.Skip(start).Take(32).ToArray();
            if (batch.Length > 0)
            {
                string prefix = batch[0].Split(':')[0];
                int lastDot = prefix.LastIndexOf('.');
                progress?.Report(lastDot > 0
                    ? $"正在查找 {prefix[..lastDot]}.x"
                    : "正在搜索局域网主机");
            }
            await Task.WhenAll(batch.Select(ProbeAndAddAsync));
        }

        return hosts;
    }

    public static string GetBestLocalAccessAddress(int port)
    {
        string ip = GetLocalNetworkCandidates().FirstOrDefault()?.Address.ToString() ?? "127.0.0.1";
        return $"{ip}:{port}";
    }

    public static async Task<string> GetVerifiedLocalAccessAddressAsync(int port, CancellationToken token = default)
    {
        string fallback = GetBestLocalAccessAddress(port);
        foreach (var candidate in GetLocalNetworkCandidates())
        {
            token.ThrowIfCancellationRequested();
            string address = $"{candidate.Address}:{port}";
            if (await CanConnectAsync(address))
                return address;
        }

        return fallback;
    }

    public static bool TryOpenUrl(string url, out string error)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static void OpenUrl(string url)
    {
        TryOpenUrl(url, out _);
    }

    internal enum WebAccessProbeResult
    {
        Authorized,
        Unauthorized,
        Failed
    }

    /// <summary>
    /// 构造网页访问地址；有 key 时带 ?key=，由服务端校验并种 cookie。
    /// </summary>
    internal static string BuildWebAccessUrl(string address, string? accessKey)
    {
        string url = ToUrl(address).TrimEnd('/');
        return string.IsNullOrWhiteSpace(accessKey)
            ? url
            : $"{url}/?key={Uri.EscapeDataString(accessKey.Trim())}";
    }

    /// <summary>
    /// 网页访问预检：自动跟随重定向。只有最终状态码 200 且最终 URL 仍严格为
    /// 主机根路径（scheme/host/端口相同、path 为 "/"、无 query、无 fragment）
    /// 才算主机明确接受；401 视为 key 无效；其余情况一律视为失败。
    /// </summary>
    internal static async Task<WebAccessProbeResult> ProbeWebAccessAsync(
        string address,
        string? accessKey = null,
        CancellationToken token = default)
    {
        string normalized = NormalizeAddress(address);
        if (normalized.Length == 0)
            return WebAccessProbeResult.Failed;

        string url = BuildWebAccessUrl(normalized, accessKey);
        try
        {
            // 每次预检使用独立客户端：服务端在带 key 请求上种 cookie，
            // 共享客户端会把 cookie 泄漏到下一次裸地址探测。
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                Timeout = TimeSpan.FromSeconds(8)
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                token);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return WebAccessProbeResult.Unauthorized;
            if (!response.IsSuccessStatusCode
                || !IsWebRoot(response.RequestMessage?.RequestUri, url))
                return WebAccessProbeResult.Failed;
            return WebAccessProbeResult.Authorized;
        }
        catch (Exception ex) when (ex is HttpRequestException
            or TaskCanceledException
            or InvalidOperationException)
        {
            return WebAccessProbeResult.Failed;
        }
    }

    internal static bool IsWebRoot(Uri? finalUri, string expectedUrl)
    {
        if (finalUri == null
            || !Uri.TryCreate(expectedUrl, UriKind.Absolute, out Uri? expected))
            return false;

        return string.Equals(finalUri.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(finalUri.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
            && finalUri.Port == expected.Port
            && string.Equals(finalUri.AbsolutePath, "/", StringComparison.Ordinal)
            && string.IsNullOrEmpty(finalUri.Query)
            && string.IsNullOrEmpty(finalUri.Fragment);
    }

    public static bool TryRestartApplication(string reason = "unspecified", Window? owner = null)
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return false;

            if (!TryScheduleRestart(exePath, AppContext.BaseDirectory, reason))
                return false;

            RuntimeLog.RecordShutdownRequest("ApplicationRestart", reason);
            RuntimeLog.Info("Restart",
                $"Replacement process scheduled after resource cleanup currentPid={Environment.ProcessId}, reason={reason}");
            try
            {
                if (owner != null)
                    owner.Close();
                else
                    Application.Current.Shutdown();
            }
            catch
            {
                CancelPendingRestart();
                throw;
            }
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("Restart", $"Failed to restart application reason={reason}", ex);
            return false;
        }
    }

    public static bool TryScheduleRootLauncherRestart(string reason = "app-update")
    {
        if (!LauncherUpdateService.TryResolveInstalledLauncher(
                AppContext.BaseDirectory,
                out string launcherPath))
        {
            RuntimeLog.Warn("Restart", "Cannot schedule update restart outside clean package layout");
            return false;
        }

        string workingDirectory = Path.GetDirectoryName(launcherPath) ?? AppContext.BaseDirectory;
        if (!TryScheduleRestart(launcherPath, workingDirectory, reason))
            return false;

        RuntimeLog.RecordShutdownRequest("ApplicationUpdateRestart", reason);
        RuntimeLog.Info(
            "Restart",
            $"Root launcher scheduled after resource cleanup currentPid={Environment.ProcessId}, reason={reason}");
        return true;
    }

    internal static bool TryScheduleRestart(
        string executablePath,
        string workingDirectory,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return false;

        lock (RestartLock)
        {
            _pendingRestart = new PendingRestart(
                executablePath,
                workingDirectory,
                reason);
        }
        return true;
    }

    internal static bool IsRestartPending
    {
        get
        {
            lock (RestartLock)
                return _pendingRestart != null;
        }
    }

    internal static void CancelPendingRestart()
    {
        lock (RestartLock)
            _pendingRestart = null;
    }

    internal static bool TryStartPendingRestart(Func<ProcessStartInfo, int?>? startProcess = null)
    {
        PendingRestart? pending;
        lock (RestartLock)
        {
            pending = _pendingRestart;
            _pendingRestart = null;
        }

        if (pending == null)
            return false;

        try
        {
            int? newProcessId;
            var startInfo = new ProcessStartInfo
            {
                FileName = pending.ExecutablePath,
                WorkingDirectory = pending.WorkingDirectory,
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add("--wait-for-process-exit");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            if (startProcess != null)
            {
                newProcessId = startProcess(startInfo);
            }
            else
            {
                using Process? process = Process.Start(startInfo);
                newProcessId = process?.Id;
            }

            if (newProcessId == null)
                return false;

            RuntimeLog.Info("Restart",
                $"Started replacement process after cleanup oldPid={Environment.ProcessId}, newPid={newProcessId}, reason={pending.Reason}");
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("Restart", $"Failed to start replacement process reason={pending.Reason}", ex);
            return false;
        }
    }

    internal static bool WaitForRestartParentExit(
        IReadOnlyList<string> arguments,
        int timeoutMilliseconds,
        out string error)
    {
        error = "";
        int optionIndex = -1;
        for (int i = 0; i < arguments.Count; i++)
        {
            if (string.Equals(arguments[i], "--wait-for-process-exit", StringComparison.OrdinalIgnoreCase))
            {
                optionIndex = i;
                break;
            }
        }

        if (optionIndex < 0)
            return true;
        if (optionIndex + 1 >= arguments.Count ||
            !int.TryParse(arguments[optionIndex + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int processId) ||
            processId <= 0 ||
            processId == Environment.ProcessId)
        {
            error = "自动重启参数无效，请手动关闭程序后重新打开";
            return false;
        }

        try
        {
            using Process parent = Process.GetProcessById(processId);
            if (parent.WaitForExit(timeoutMilliseconds))
                return true;

            error = $"旧程序进程（PID {processId}）未能正常退出，请先在任务管理器中关闭旧程序再重新打开";
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (Exception ex)
        {
            error = $"等待旧程序退出失败：{ex.Message}";
            return false;
        }
    }

    public static bool RestartAfterPurposeChange(Window? owner = null)
    {
        if (TryRestartApplication("workstation-role-change", owner))
            return true;

        AppDialog.Error(
            owner,
            "自动重启失败，请手动关闭后重新打开程序",
            "切换用途");
        return false;
    }

    internal static IReadOnlyList<int> GetDiscoveryPorts(int configuredPort)
    {
        var ports = new List<int>(2);
        if (configuredPort is > 0 and <= 65535)
            ports.Add(configuredPort);
        if (!ports.Contains(DefaultHttpPort))
            ports.Add(DefaultHttpPort);
        return ports;
    }

    private static IEnumerable<string> GetLocalIpv4ScanAddresses()
    {
        foreach (var candidate in GetLocalNetworkCandidates())
        {
            foreach (IPAddress address in EnumerateSubnetAddresses(candidate.Address, candidate.IPv4Mask))
                yield return address.ToString();
        }
    }

    internal static IReadOnlyList<IPAddress> EnumerateSubnetAddresses(
        IPAddress address,
        IPAddress? subnetMask)
    {
        ArgumentNullException.ThrowIfNull(address);
        byte[] addressBytes = address.GetAddressBytes();
        byte[]? maskBytes = subnetMask?.GetAddressBytes();
        if (addressBytes.Length != 4)
            return [];
        if (maskBytes?.Length != 4 || !IsContiguousSubnetMask(maskBytes))
            maskBytes = [255, 255, 255, 0];

        uint addressValue = ToUInt32(addressBytes);
        uint maskValue = ToUInt32(maskBytes);
        uint network = addressValue & maskValue;
        uint broadcast = network | ~maskValue;
        ulong hostCount = broadcast > network ? (ulong)broadcast - network - 1 : 0;
        if (hostCount > MaxSubnetDiscoveryHosts)
        {
            maskValue = 0xffffff00;
            network = addressValue & maskValue;
            broadcast = network | ~maskValue;
            hostCount = broadcast > network ? (ulong)broadcast - network - 1 : 0;
        }

        var result = new List<IPAddress>((int)hostCount);
        for (uint value = network + 1; value < broadcast; value++)
            result.Add(FromUInt32(value));
        return result;
    }

    private static bool IsContiguousSubnetMask(byte[] bytes)
    {
        uint mask = ToUInt32(bytes);
        uint inverted = ~mask;
        return (inverted & (inverted + 1)) == 0;
    }

    private static uint ToUInt32(byte[] bytes) =>
        ((uint)bytes[0] << 24) |
        ((uint)bytes[1] << 16) |
        ((uint)bytes[2] << 8) |
        bytes[3];

    private static IPAddress FromUInt32(uint value) =>
        new(
        [
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        ]);

    private sealed record LocalNetworkCandidate(
        IPAddress Address,
        IPAddress? IPv4Mask,
        NetworkInterface Interface,
        bool HasGateway,
        int Score);

    private static IEnumerable<LocalNetworkCandidate> GetLocalNetworkCandidates()
    {
        var candidates = new List<LocalNetworkCandidate>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                continue;

            var properties = nic.GetIPProperties();
            bool hasGateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                                                   !IPAddress.Equals(g.Address, IPAddress.Any));
            foreach (var addr in properties.UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (!IsUsableLanAddress(addr.Address)) continue;

                int score = 0;
                if (hasGateway) score += 100;
                if (IsPrivateLanAddress(addr.Address)) score += 60;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    score += 25;
                if (addr.IPv4Mask != null && addr.IPv4Mask.ToString() == "255.255.255.0")
                    score += 5;

                candidates.Add(new LocalNetworkCandidate(addr.Address, addr.IPv4Mask, nic, hasGateway, score));
            }
        }

        return candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Address.ToString(), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsUsableLanAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;

        // 0.0.0.0, APIPA, multicast, broadcast, and the RFC 2544 benchmark block are not useful here.
        if (bytes[0] == 0) return false;
        if (bytes[0] == 169 && bytes[1] == 254) return false;
        if (bytes[0] >= 224) return false;
        if (bytes[0] == 255) return false;
        if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19)) return false;

        return true;
    }

    private static bool IsPrivateLanAddress(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;

        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }
}

public readonly record struct RecordingWorkstationHeartbeatResult(
    bool Online,
    string AssignedDisplayName);

internal sealed class BackupDeviceEnrollmentResult
{
    public string Protocol { get; set; } = "";
    public int Version { get; set; }
    public int AuthVersion { get; set; }
    public string ComputerId { get; set; } = "";
    public string ComputerName { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DeviceToken { get; set; } = "";
    public string? WebAccessUrl { get; set; }
    public string HostVersion { get; set; } = "";
}

internal sealed class BackupCompatibilityError
{
    public string ErrorCode { get; set; } = "";
    public string Error { get; set; } = "";
    public string UpdateTarget { get; set; } = "";
    public string MinimumVersion { get; set; } = "";
    public int MinimumBuildNumber { get; set; }
    public string DownloadUrl { get; set; } = "";
}
