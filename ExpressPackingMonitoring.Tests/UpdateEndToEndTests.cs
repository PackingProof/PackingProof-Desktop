using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using ExpressPackingMonitoring.UpdateCore;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 更新检查的"真 HTTP"回归：本地起一个假发布源，走真实 HttpClient 与真实 JSON 解析。
/// 单元测试换掉的是传输层（HttpMessageHandler），换不掉 URL 拼装与列表筛选，
/// 而"只发 macOS 的版本不能推给 Windows"这类规则一旦写错，用户端就是"提示有新版本却下错包"。
/// </summary>
public sealed class UpdateEndToEndTests
{
    [Fact]
    public async Task RealHttpCheck_SkipsMacOnlyNewestRelease()
    {
        using var source = new LocalReleaseSource();
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        var metadata = new UpdateMetadataClient(client);

        // 应用内检查在 Windows 上走的就是这条：按"带更新清单"挑版本
        using ResolvedUpdateRelease release = await metadata.FetchLatestReleaseWithAssetAsync(
            [$"{source.BaseUrl}/releases?per_page=30"],
            UpdateReleaseSelection.IsUpdateManifest,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            "v999.0.98",
            release.Release.RootElement.GetProperty("tag_name").GetString());

        // 启动器的自动更新：自己把 /releases/latest 换成列表接口再挑版本
        using ResolvedUpdateManifest manifest = await metadata.FetchLatestManifestAsync(
            source.CheckUrls,
            TestContext.Current.CancellationToken);
        Assert.Equal("999.0.98", manifest.LatestVersion);
        Assert.Equal(
            $"{source.BaseUrl}/update_v999.0.98.json",
            manifest.ManifestUrl);

        // 真的按"release 列表"接口取，并且没有碰只发 macOS 的那个版本
        Assert.Contains("/releases?per_page=30", source.RequestedPaths);
        Assert.DoesNotContain(
            source.RequestedPaths,
            path => path.Contains("999.0.99", StringComparison.Ordinal));

        // 对照：只看 /releases/latest 的老逻辑会落到只发 macOS 的那个版本上
        using ResolvedUpdateRelease legacy = await metadata.FetchLatestReleaseAsync(
            source.CheckUrls,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            "v999.0.99",
            legacy.Release.RootElement.GetProperty("tag_name").GetString());
    }

    /// <summary>
    /// 极简假发布源：最新版本只有 DMG，上一版带更新清单。
    /// 只处理 GET，返回写死的 JSON。
    /// </summary>
    private sealed class LocalReleaseSource : IDisposable
    {
        private const string ReleaseList = """
            [
              {
                "tag_name": "v999.0.99",
                "name": "mac only",
                "html_url": "http://127.0.0.1/mac-only",
                "assets": [
                  {
                    "name": "PackingProof-macOS-999.0.99.dmg",
                    "browser_download_url": "http://127.0.0.1/PackingProof-macOS-999.0.99.dmg"
                  }
                ]
              },
              {
                "tag_name": "v999.0.98",
                "name": "windows",
                "html_url": "http://127.0.0.1/windows",
                "assets": [
                  {
                    "name": "update_v999.0.98.json",
                    "browser_download_url": "http://127.0.0.1/update_v999.0.98.json"
                  }
                ]
              }
            ]
            """;

        private const string Manifest = """{"latest_version":"999.0.98"}""";

        /// <summary>只发 macOS 的版本会成为 /releases/latest，也就是老逻辑会踩到的那一个。</summary>
        private const string MacOnlyRelease = """
            {
              "tag_name": "v999.0.99",
              "name": "mac only",
              "html_url": "http://127.0.0.1/mac-only",
              "assets": [
                {
                  "name": "PackingProof-macOS-999.0.99.dmg",
                  "browser_download_url": "http://127.0.0.1/PackingProof-macOS-999.0.99.dmg"
                }
              ]
            }
            """;

        private readonly TcpListener _listener;
        private readonly Thread _thread;
        private volatile bool _stopped;

        internal LocalReleaseSource()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
            _thread = new Thread(Serve) { IsBackground = true };
            _thread.Start();
        }

        internal string BaseUrl { get; }

        internal IReadOnlyList<string> CheckUrls => [$"{BaseUrl}/releases/latest"];

        internal List<string> RequestedPaths { get; } = [];

        public void Dispose()
        {
            _stopped = true;
            try { _listener.Stop(); } catch { }
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        private void Serve()
        {
            while (!_stopped)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { return; }

                using (client)
                using (NetworkStream stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true))
                {
                    string requestLine = reader.ReadLine() ?? "";
                    string path = requestLine.Split(' ') is { Length: >= 2 } parts
                        ? parts[1]
                        : "";
                    RequestedPaths.Add(path);

                    string body = path.Split('?', 2)[0] switch
                    {
                        "/releases" => ReleaseList.Replace("http://127.0.0.1", BaseUrl, StringComparison.Ordinal),
                        "/releases/latest" => MacOnlyRelease.Replace("http://127.0.0.1", BaseUrl, StringComparison.Ordinal),
                        "/update_v999.0.98.json" => Manifest,
                        _ => "{}"
                    };
                    byte[] payload = Encoding.UTF8.GetBytes(body);
                    byte[] header = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\n"
                        + "Content-Type: application/json\r\n"
                        + $"Content-Length: {payload.Length}\r\n"
                        + "Connection: close\r\n\r\n");
                    stream.Write(header);
                    stream.Write(payload);
                    stream.Flush();
                }
            }
        }
    }
}
