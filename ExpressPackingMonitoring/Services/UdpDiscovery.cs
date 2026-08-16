using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 跨端 UDP 广播主机发现的协议常量与报文编解码。
/// 与手机端 / MacViewer 端保持一致：固定 UDP 5281，广播 255.255.255.255，
/// 报文为 UTF-8 JSON，单包不超过 512 字节。
/// </summary>
internal static class UdpDiscoveryProtocol
{
    public const int Port = 5281;
    public const int MaxPacketBytes = 512;
    public const string Protocol = PackingProofNodeInfo.ExpectedProtocol;
    public const int ProtocolVersion = PackingProofNodeInfo.SupportedProtocolVersion;
    public const string ActionDiscover = "discover";
    public const string ActionAnnounce = "announce";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed class DiscoveryRequest
    {
        public string Protocol { get; set; } = "";
        public int ProtocolVersion { get; set; }
        public string Action { get; set; } = "";
    }

    private sealed class AnnounceResponse
    {
        public string Protocol { get; set; } = "";
        public int ProtocolVersion { get; set; }
        public string Action { get; set; } = "";
        public string NodeId { get; set; } = "";
        public int HttpPort { get; set; }
    }

    public static byte[] EncodeDiscover()
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            new DiscoveryRequest
            {
                Protocol = Protocol,
                ProtocolVersion = ProtocolVersion,
                Action = ActionDiscover
            },
            JsonOptions);
    }

    public static bool IsDiscover(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0 || data.Length > MaxPacketBytes)
            return false;

        try
        {
            DiscoveryRequest? request = JsonSerializer.Deserialize<DiscoveryRequest>(data, JsonOptions);
            return request != null
                && string.Equals(request.Protocol, Protocol, StringComparison.Ordinal)
                && request.ProtocolVersion == ProtocolVersion
                && string.Equals(request.Action, ActionDiscover, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static byte[] EncodeAnnounce(string nodeId, int httpPort)
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            new AnnounceResponse
            {
                Protocol = Protocol,
                ProtocolVersion = ProtocolVersion,
                Action = ActionAnnounce,
                NodeId = nodeId,
                HttpPort = httpPort
            },
            JsonOptions);
    }

    public static bool TryParseAnnounce(
        ReadOnlySpan<byte> data,
        out string nodeId,
        out int httpPort)
    {
        nodeId = "";
        httpPort = 0;
        if (data.Length == 0 || data.Length > MaxPacketBytes)
            return false;

        try
        {
            AnnounceResponse? response = JsonSerializer.Deserialize<AnnounceResponse>(data, JsonOptions);
            if (response == null
                || !string.Equals(response.Protocol, Protocol, StringComparison.Ordinal)
                || response.ProtocolVersion != ProtocolVersion
                || !string.Equals(response.Action, ActionAnnounce, StringComparison.Ordinal)
                || !Guid.TryParse(response.NodeId, out Guid nodeGuid)
                || nodeGuid == Guid.Empty
                || response.HttpPort is <= 0 or > 65535)
            {
                return false;
            }

            nodeId = nodeGuid.ToString("D");
            httpPort = response.HttpPort;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// 主机端 UDP 发现响应器：绑定 0.0.0.0:5281 监听广播，
/// 收到合法 discover 且本机为 host 预设时，单播回 announce（不广播回发）。
/// </summary>
internal sealed class UdpDiscoveryResponder : IDisposable
{
    private readonly string _nodeId;
    private readonly Func<int> _httpPortProvider;
    private readonly Func<bool> _isHostProvider;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private int _disposed;

    public UdpDiscoveryResponder(
        string nodeId,
        Func<int> httpPortProvider,
        Func<bool> isHostProvider)
    {
        _nodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
        _httpPortProvider = httpPortProvider ?? throw new ArgumentNullException(nameof(httpPortProvider));
        _isHostProvider = isHostProvider ?? throw new ArgumentNullException(nameof(isHostProvider));
    }

    public void Start()
    {
        if (_udp != null)
            return;

        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, UdpDiscoveryProtocol.Port));
        }
        catch (SocketException ex)
        {
            RuntimeLog.Warn("UdpDiscovery", $"无法监听 UDP {UdpDiscoveryProtocol.Port}：{ex.Message}");
            _udp = null;
            return;
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoop(_cts.Token));
    }

    private async Task ReceiveLoop(CancellationToken token)
    {
        UdpClient? udp = _udp;
        if (udp == null)
            return;

        while (!token.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult result = await udp.ReceiveAsync(token).ConfigureAwait(false);
                Handle(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            catch { }
        }
    }

    private void Handle(byte[] data, IPEndPoint remote)
    {
        if (!UdpDiscoveryProtocol.IsDiscover(data) || !_isHostProvider())
            return;

        int httpPort = _httpPortProvider();
        if (httpPort is <= 0 or > 65535)
            return;

        byte[] response = UdpDiscoveryProtocol.EncodeAnnounce(_nodeId, httpPort);
        if (response.Length > UdpDiscoveryProtocol.MaxPacketBytes)
            return;

        try
        {
            _udp?.Send(response, response.Length, remote);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("UdpDiscovery", $"回复 announce 失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { _cts?.Cancel(); } catch { }
        try { _udp?.Close(); } catch { }
        try { _cts?.Dispose(); } catch { }
    }
}

/// <summary>
/// 客户端 UDP 发现探测：绑定临时端口、开启 SO_BROADCAST，
/// 广播 discover 并在约 600ms 内收集 announce 候选。
/// </summary>
internal static class UdpDiscoveryClient
{
    internal sealed record Announce(string NodeId, int HttpPort, string SourceIp);

    public static readonly TimeSpan CollectTimeout = TimeSpan.FromMilliseconds(600);

    public static async IAsyncEnumerable<Announce> DiscoverAsync(
        [EnumeratorCancellation] CancellationToken token)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.EnableBroadcast = true;

        byte[] request = UdpDiscoveryProtocol.EncodeDiscover();
        if (request.Length > UdpDiscoveryProtocol.MaxPacketBytes)
            yield break;

        try
        {
            udp.Send(
                request,
                request.Length,
                new IPEndPoint(IPAddress.Broadcast, UdpDiscoveryProtocol.Port));
        }
        catch
        {
            yield break;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(CollectTimeout);
        while (true)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { yield break; }
            catch (ObjectDisposedException) { yield break; }
            catch (SocketException) { yield break; }

            if (!UdpDiscoveryProtocol.TryParseAnnounce(
                    result.Buffer,
                    out string nodeId,
                    out int httpPort))
            {
                continue;
            }

            string sourceIp = result.RemoteEndPoint.Address.ToString();
            yield return new Announce(nodeId, httpPort, sourceIp);
        }
    }
}
