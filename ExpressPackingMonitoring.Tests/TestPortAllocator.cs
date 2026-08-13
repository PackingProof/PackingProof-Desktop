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
    public static int GetFreeTcpPort()
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            int port = FindFreeTcpPort();
            if (port > 0 && CanBindHttpListener(port))
                return port;
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
