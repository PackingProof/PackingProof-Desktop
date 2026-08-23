using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.ViewModels;
using ZXing;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

[Collection("Web server tests")]
public sealed class MobileConnectionTests
{
    [Fact]
    public void RecordingHostMobileConnectionRepairsDisabledWebServer()
    {
        Assert.True(MainViewModel.ShouldEnableWebServerForMobileConnection(new AppConfig
        {
            DeploymentPreset = DeploymentPresets.RecordingHost,
            EnableWebServer = false
        }));
        Assert.False(MainViewModel.ShouldEnableWebServerForMobileConnection(new AppConfig
        {
            DeploymentPreset = DeploymentPresets.ViewerClient,
            EnableWebServer = false
        }));
    }

    [Theory]
    [InlineData(false, "", "http://192.168.1.20:5280")]
    [InlineData(false, "abc 123", "http://192.168.1.20:5280/?key=abc%20123")]
    [InlineData(true, "abc 123", "http://192.168.1.20:5280/?key=abc%20123")]
    public void AccessUrlMatchesProtectionSettings(bool requireAccessKey, string accessKey, string expected)
    {
        bool result = MobileConnectionService.TryBuildUsableAccessUrl(
            "192.168.1.20:5280",
            requireAccessKey,
            accessKey,
            out string url);

        Assert.True(result);
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData("http://192.168.1.20:5280/?key=abc", true)]
    [InlineData("http://192.168.1.20:5280/?KEY=abc%20123", true)]
    [InlineData("http://192.168.1.20:5280/?key=", false)]
    [InlineData("http://192.168.1.20:5280/", false)]
    public void AccessKeyWarningFollowsActualSharedUrl(string url, bool expected)
    {
        Assert.Equal(expected, MobileConnectionService.ContainsAccessKey(url));
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1:5280")]
    [InlineData("localhost:5280")]
    [InlineData("0.0.0.0:5280")]
    public void LoopbackOrMissingAddressCannotProduceQrUrl(string address)
    {
        Assert.False(MobileConnectionService.TryBuildUsableAccessUrl(address, false, "", out string url));
        Assert.Equal("", url);
    }

    [Fact]
    public void GeneratedQrDecodesToExactAccessUrl()
    {
        const string expected = "http://192.168.1.20:5280/?key=0123456789abcdef";
        var bitmap = MobileConnectionService.CreateQrBitmap(expected, 320);
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        var luminance = new RGBLuminanceSource(
            pixels,
            bitmap.PixelWidth,
            bitmap.PixelHeight,
            RGBLuminanceSource.BitmapFormat.BGRA32);
        var decoded = new BarcodeReaderGeneric().Decode(luminance);

        Assert.NotNull(decoded);
        Assert.Equal(expected, decoded.Text);
    }

    [Fact]
    public void ExistingUserIsPromptedOnceWithoutChangingOtherSettings()
    {
        var config = new AppConfig
        {
            FirstUseWizardCompleted = true,
            EnableWebServer = true,
            MobileConnectionSetupVersion = 0,
            EnableGlobalKeyboard = false,
            RequireWebAccessKey = true
        };

        Assert.True(AppConfig.ShouldPromptMobileConnection(config));

        AppConfig.MarkMobileConnectionSetupCompleted(config);

        Assert.False(AppConfig.ShouldPromptMobileConnection(config));
        Assert.Equal(AppConfig.CurrentMobileConnectionSetupVersion, config.MobileConnectionSetupVersion);
        Assert.False(config.EnableGlobalKeyboard);
        Assert.True(config.RequireWebAccessKey);
    }

    [Fact]
    public void FirstUseDefaultsLeaveMobilePromptPendingUntilQrWasShown()
    {
        var config = new AppConfig { EnableWebServer = true };

        AppConfig.ApplyFirstUseDefaults(config);

        Assert.True(config.FirstUseWizardCompleted);
        Assert.Equal(0, config.MobileConnectionSetupVersion);
        Assert.True(AppConfig.ShouldPromptMobileConnection(config));
    }

    [Fact]
    public void FailedSaveCanKeepCurrentSetupVersionPending()
    {
        var current = new AppConfig
        {
            FirstUseWizardCompleted = true,
            EnableWebServer = true,
            MobileConnectionSetupVersion = 0
        };
        var saveCandidate = new AppConfig
        {
            FirstUseWizardCompleted = current.FirstUseWizardCompleted,
            EnableWebServer = current.EnableWebServer,
            MobileConnectionSetupVersion = current.MobileConnectionSetupVersion
        };

        AppConfig.MarkMobileConnectionSetupCompleted(saveCandidate);

        Assert.Equal(0, current.MobileConnectionSetupVersion);
        Assert.True(AppConfig.ShouldPromptMobileConnection(current));
        Assert.Equal(AppConfig.CurrentMobileConnectionSetupVersion, saveCandidate.MobileConnectionSetupVersion);
    }

    [Theory]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 1)]
    public void PromptRequiresCompletedWizardEnabledServerAndOldVersion(
        bool firstUseCompleted,
        bool webServerEnabled,
        int setupVersion)
    {
        var config = new AppConfig
        {
            FirstUseWizardCompleted = firstUseCompleted,
            EnableWebServer = webServerEnabled,
            MobileConnectionSetupVersion = setupVersion
        };

        Assert.False(AppConfig.ShouldPromptMobileConnection(config));
    }

    [Fact]
    public async Task ProtectedEndpointRejectsUnauthorizedAndAcceptsQueryThenCookie()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"epm-mobile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        int port = GetFreeTcpPort();
        const string accessKey = "0123456789abcdef0123456789abcdef";
        string expectedUrl = $"http://192.168.1.20:{port}/?key={accessKey}";

        try
        {
            using var database = new VideoDatabase(Path.Combine(tempDirectory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                requireAccessKey: true,
                accessKey: accessKey,
                listenerHost: "127.0.0.1",
                mobileBackupStateDirectory: Path.Combine(tempDirectory, "state"),
                mobileConnectionUrlProvider: () => expectedUrl);
            server.Start();

            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = true,
                CookieContainer = new CookieContainer()
            };
            using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            using HttpResponseMessage unauthorized = await client.GetAsync("/api/mobile-connection", cancellationToken);
            string unauthorizedBody = await unauthorized.Content.ReadAsStringAsync(cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            Assert.DoesNotContain(accessKey, unauthorizedBody, StringComparison.Ordinal);
            Assert.DoesNotContain(expectedUrl, unauthorizedBody, StringComparison.Ordinal);

            using HttpResponseMessage queryAuthorized = await client.GetAsync($"/api/mobile-connection?key={accessKey}", cancellationToken);
            Assert.Equal(HttpStatusCode.OK, queryAuthorized.StatusCode);
            using JsonDocument payload = JsonDocument.Parse(await queryAuthorized.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal(expectedUrl, payload.RootElement.GetProperty("url").GetString());
            Assert.StartsWith("data:image/png;base64,", payload.RootElement.GetProperty("qrCode").GetString());
            Assert.True(payload.RootElement.GetProperty("accessProtected").GetBoolean());

            using HttpResponseMessage cookieAuthorized = await client.GetAsync("/api/mobile-connection", cancellationToken);
            Assert.Equal(HttpStatusCode.OK, cookieAuthorized.StatusCode);
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void MobileConnectionEndpointAlwaysRequiresAccessKeyWhenProtectionIsEnabled()
    {
        Assert.True(WebServer.RequiresAccessKey("/api/mobile-connection"));
        Assert.True(WebServer.RequiresAccessKey("/API/MOBILE-CONNECTION"));
    }

    [Fact]
    public void StorageOverviewRequiresAccessKeyWhenProtectionIsEnabled()
    {
        Assert.True(WebServer.RequiresAccessKey("/api/storage"));
        Assert.True(WebServer.RequiresAccessKey("/API/STORAGE/OVERVIEW"));
    }

    [Fact]
    public void ExtensionApiRequiresAccessKeyWhenProtectionIsEnabled()
    {
        Assert.True(WebServer.RequiresAccessKey("/api/extensions/v1/capabilities"));
        Assert.True(WebServer.RequiresAccessKey("/API/EXTENSIONS/V1/ORDERS"));
    }

    [Fact]
    public async Task ExtensionOrdersEndpointAcceptsCountsAndPersistsOrderSnapshot()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"epm-extension-orders-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        int port = GetFreeTcpPort();
        const string accessKey = "0123456789abcdef0123456789abcdef";

        try
        {
            using var database = new VideoDatabase(Path.Combine(tempDirectory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                requireAccessKey: true,
                accessKey: accessKey,
                listenerHost: "127.0.0.1",
                mobileBackupStateDirectory: Path.Combine(tempDirectory, "state"));
            server.Start();

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;

            using HttpResponseMessage unauthorized = await client.GetAsync(
                "/api/extensions/v1/capabilities", cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/extensions/v1/orders");
            request.Headers.Add("X-EPM-Access-Key", accessKey);
            request.Content = new StringContent(
                "{\"apiVersion\":\"v1\",\"providerId\":\"test.provider\",\"orders\":[{\"trackingNumber\":\"EXT-TEST-001\",\"orderId\":\"ORDER-001\",\"totalItemCount\":7,\"mergedOrderCount\":2}]}",
                System.Text.Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using JsonDocument payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal(0, payload.RootElement.GetProperty("testCount").GetInt32());

            using HttpResponseMessage query = await client.GetAsync(
                "/api/orderinfo?trackingNo=EXT-TEST-001", cancellationToken);
            Assert.Equal(HttpStatusCode.OK, query.StatusCode);
            string queryBody = await query.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument queryPayload = JsonDocument.Parse(queryBody);
            Assert.True(queryPayload.RootElement.GetProperty("found").GetBoolean(), queryBody);
            Assert.Equal(7, queryPayload.RootElement.GetProperty("totalItemCount").GetInt32());
            Assert.Equal(2, queryPayload.RootElement.GetProperty("mergedOrderCount").GetInt32());
            Assert.Equal("test.provider", queryPayload.RootElement.GetProperty("providerId").GetString());
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task StorageOverviewRejectsMissingAndWrongKeyAndAcceptsValidKey()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"epm-storage-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        int port = GetFreeTcpPort();
        const string accessKey = "0123456789abcdef0123456789abcdef";

        try
        {
            using var database = new VideoDatabase(Path.Combine(tempDirectory, "videos.db"));
            using var server = new WebServer(
                database,
                port,
                requireAccessKey: true,
                accessKey: accessKey,
                listenerHost: "127.0.0.1",
                mobileBackupStateDirectory: Path.Combine(tempDirectory, "state"),
                mobileConnectionUrlProvider: () => $"http://192.168.1.20:{port}/?key={accessKey}");
            server.Start();

            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = true,
                CookieContainer = new CookieContainer()
            };
            using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;

            using HttpResponseMessage missing = await client.GetAsync("/api/storage", cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

            using (var wrongHeaderRequest = new HttpRequestMessage(HttpMethod.Get, "/api/storage"))
            {
                wrongHeaderRequest.Headers.Add("X-EPM-Access-Key", "wrong-key");
                using HttpResponseMessage wrongHeader = await client.SendAsync(wrongHeaderRequest, cancellationToken);
                Assert.Equal(HttpStatusCode.Unauthorized, wrongHeader.StatusCode);
            }

            using HttpResponseMessage wrongQuery = await client.GetAsync(
                "/api/storage?key=wrong-key",
                cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, wrongQuery.StatusCode);

            // 有效密钥只验证鉴权边界：存储概览可能受本机真实配置影响返回 500，
            // 只要不是 401 就证明请求已通过访问密钥保护。
            using (var validRequest = new HttpRequestMessage(HttpMethod.Get, "/api/storage"))
            {
                validRequest.Headers.Add("X-EPM-Access-Key", accessKey);
                using HttpResponseMessage validHeader = await client.SendAsync(validRequest, cancellationToken);
                Assert.NotEqual(HttpStatusCode.Unauthorized, validHeader.StatusCode);
            }

            using HttpResponseMessage queryAuthorized = await client.GetAsync(
                $"/api/storage?key={accessKey}",
                cancellationToken);
            Assert.NotEqual(HttpStatusCode.Unauthorized, queryAuthorized.StatusCode);

            using HttpResponseMessage cookieAuthorized = await client.GetAsync("/api/storage", cancellationToken);
            Assert.NotEqual(HttpStatusCode.Unauthorized, cookieAuthorized.StatusCode);
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); } catch { }
        }
    }

    private static int GetFreeTcpPort() =>
        TestPortAllocator.GetFreeTcpPort();
}
