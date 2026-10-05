using System.Net;
using System.Net.Sockets;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 为启动 WebServer（HttpListener）的测试分配端口：先找空闲 TCP 端口，
/// 再验证 HttpListener 能真正绑定（HTTP.sys 可能为其他 URL ACL 预留了
/// 对 TCP 而言空闲的端口），不可用时重试。
/// </summary>
internal static class TestPortAllocator
{
    /// <summary>
    /// 本进程里已经发出去过的端口不再重复发：同一用例连着分配两个端口时，
    /// TCP 会立刻复用刚释放的那个，两个监听器就撞在同一个端口上
    /// （现场就是这样：第二个 HttpListener.Start() 报"与机器上已有注册冲突"）。
    /// </summary>
    private static readonly HashSet<int> HandedOut = [];
    private static readonly object Gate = new();

    public static int GetFreeTcpPort()
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            int port = FindFreeTcpPort();
            if (port <= 0)
                continue;

            lock (Gate)
            {
                if (HandedOut.Contains(port))
                    continue;
                if (!CanBindHttpListener(port))
                    continue;

                HandedOut.Add(port);
            }

            return port;
        }

        throw new InvalidOperationException(
            "Unable to find a loopback port available to HttpListener.");
    }

    /// <summary>
    /// 需要自己起 HttpListener 的用例走这里：分配与绑定一次做完，中间不留空档，
    /// 别的用例（哪怕在另一个集合里并行跑）也抢不走这个端口 —— HTTP.sys 的前缀是机器级的。
    /// </summary>
    public static HttpListener StartHttpListener(out int port)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            int candidate = FindFreeTcpPort();
            if (candidate <= 0)
                continue;

            lock (Gate)
            {
                if (HandedOut.Contains(candidate))
                    continue;

                HandedOut.Add(candidate);
            }

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
            try
            {
                listener.Start();
                port = candidate;
                return listener;
            }
            catch
            {
                listener.Close();
            }
        }

        throw new InvalidOperationException(
            "Unable to find a loopback port available to HttpListener.");
    }

    private static int FindFreeTcpPort()
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }
        catch
        {
            return -1;
        }
    }

    private static bool CanBindHttpListener(int port)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            listener.Start();
            return true;
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5)
        {
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { listener.Stop(); } catch { }
            listener.Close();
        }
    }
}
