using System.Net;
using System.Security.Cryptography;
using System.Text;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 更新链路的整体守卫：从"应用内检查更新"一路走到"增量包准备完成"，并且跨两个发布平台。
///
/// 现场事故（0.0.74 的店里机器）：Gitee 检查源挑不到新版本 → 落到 GitHub → 更新清单地址在
/// github.com、附件域名在店里打不开 → 旧实现只给增量包做了回退，读清单这一步挂了就整条中断，
/// 用户看到"没有连接、没有回应"。这条守卫把整条路一次跑通：Gitee 检查源不可用、GitHub 的
/// 清单与增量包附件域名也不可用，仍然必须挑到最新版并把增量包准备好。
/// </summary>
public sealed class UpdatePipelineGuardTests
{
    [Fact]
    public async Task CheckAndPrepare_CompleteWhenOnePlatformCheckAndAssetsAreUnreachable()
    {
        using var handler = new TwoPlatformHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        string root = Path.Combine(
            Path.GetTempPath(),
            "packingproof-update-guard",
            Guid.NewGuid().ToString("N"));
        string appDirectory = Path.Combine(root, "install", "app");
        string updatesDirectory = Path.Combine(root, "updates");
        Directory.CreateDirectory(appDirectory);
        Directory.CreateDirectory(updatesDirectory);
        File.WriteAllBytes(
            Path.Combine(root, "install", "ExpressPackingMonitoring.exe"),
            "launcher"u8.ToArray());
        try
        {
            var checkService = new UpdateCheckService(client);
            UpdateCheckResult check = await checkService.FetchLatestReleaseAsync(
                TestContext.Current.CancellationToken);

            Assert.True(check.HasUpdate);
            Assert.Equal(TwoPlatformHandler.Tag, check.LatestVersion);
            // Gitee 检查源不可用时会落到 GitHub，所以清单地址在 github.com 这条打不开的链路上
            Assert.StartsWith(
                "https://github.com/",
                check.UpdateManifestUrl,
                StringComparison.Ordinal);

            var download = new AppPatchDownloadService(client, updatesDirectory, appDirectory);
            AppPatchPreparationResult prepared = await download.PrepareAsync(
                check,
                progress: null,
                TestContext.Current.CancellationToken);

            Assert.True(prepared.Status == AppPatchPreparationStatus.Ready, prepared.Message);

            string pendingPackage = Path.Combine(
                updatesDirectory,
                "pending",
                $"PackingProof_AppPatch_v{TwoPlatformHandler.Version}.zip");
            Assert.True(File.Exists(pendingPackage));
            Assert.Equal(TwoPlatformHandler.PackageBytes, File.ReadAllBytes(pendingPackage));

            // 清单与增量包都必须真的走了镜像：github.com 那两次是失败的尝试，gitee.com 才是成功的那次
            Assert.Contains(TwoPlatformHandler.GithubManifestUrl, handler.Requests);
            Assert.Contains(TwoPlatformHandler.GiteeManifestUrl, handler.Requests);
            Assert.Contains(TwoPlatformHandler.GithubPatchUrl, handler.Requests);
            Assert.Contains(TwoPlatformHandler.GiteePatchUrl, handler.Requests);
            Assert.True(
                handler.Requests.IndexOf(TwoPlatformHandler.GiteeManifestUrl)
                < handler.Requests.IndexOf(TwoPlatformHandler.GiteePatchUrl));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// 两套发布平台：Gitee 检查源返回 429（挑不到版本），GitHub 检查源可用；
    /// GitHub 的清单与增量包附件域名全部 502，只有 Gitee 的镜像附件可下载。
    /// </summary>
    private sealed class TwoPlatformHandler : HttpMessageHandler
    {
        internal const string Version = "999.0.75";
        internal const string Tag = "v999.0.75";
        internal const string AssetName = "update_v999.0.75.json";
        internal static readonly byte[] PackageBytes = "fleet-scenario-patch"u8.ToArray();

        internal const string GithubManifestUrl =
            "https://github.com/PackingProof/PackingProof-Desktop/releases/download/v999.0.75/update_v999.0.75.json";
        internal const string GiteeManifestUrl =
            "https://gitee.com/PackingProof/PackingProof-Desktop/releases/download/v999.0.75/update_v999.0.75.json";
        internal const string GithubPatchUrl =
            "https://github.com/PackingProof/PackingProof-Desktop/releases/download/v999.0.75/PackingProof_AppPatch_v999.0.75.zip";
        internal const string GiteePatchUrl =
            "https://gitee.com/PackingProof/PackingProof-Desktop/releases/download/v999.0.75/PackingProof_AppPatch_v999.0.75.zip";

        internal List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            Requests.Add(url);

            // Gitee 检查源限流：现场就是从这一步掉到 GitHub 的
            if (url.StartsWith(
                    "https://gitee.com/api/v5/repos/PackingProof/PackingProof-Desktop/releases",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Status(request, HttpStatusCode.TooManyRequests);
            }

            if (url.StartsWith(
                    "https://api.github.com/repos/PackingProof/PackingProof-Desktop/releases",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Json(request,
                    $$"""
                    [
                      {
                        "tag_name": "{{Tag}}",
                        "name": "windows release",
                        "assets": [
                          {
                            "name": "{{AssetName}}",
                            "browser_download_url": "{{GithubManifestUrl}}"
                          }
                        ]
                      }
                    ]
                    """);
            }

            // github.com 的附件域名在店里打不开：清单和增量包都命中这里
            if (url is GithubManifestUrl or GithubPatchUrl)
                return Status(request, HttpStatusCode.BadGateway);

            if (url == GiteeManifestUrl)
                return Json(request, BuildManifest());

            if (url == GiteePatchUrl)
                return Content(request, PackageBytes, "application/zip");

            return Status(request, HttpStatusCode.NotFound);
        }

        private static string BuildManifest()
        {
            string hash = Convert.ToHexString(SHA256.HashData(PackageBytes)).ToLowerInvariant();
            return
                $$"""
                {
                  "latest_version": "{{Version}}",
                  "patch_baseline_version": "0.0.0",
                  "patch_supported": true,
                  "full_download_page": "https://example.com/releases",
                  "patch_package": {
                    "type": "baseline_patch",
                    "url": "{{GiteePatchUrl}}",
                    "github_url": "{{GithubPatchUrl}}",
                    "gitee_url": "{{GiteePatchUrl}}",
                    "sha256": "{{hash}}",
                    "size": {{PackageBytes.Length}}
                  }
                }
                """;
        }

        private static Task<HttpResponseMessage> Status(
            HttpRequestMessage request,
            HttpStatusCode status) =>
            Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });

        private static Task<HttpResponseMessage> Json(HttpRequestMessage request, string json) =>
            Content(request, Encoding.UTF8.GetBytes(json), "application/json");

        private static Task<HttpResponseMessage> Content(
            HttpRequestMessage request,
            byte[] payload,
            string contentType)
        {
            var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = content
            });
        }
    }
}
