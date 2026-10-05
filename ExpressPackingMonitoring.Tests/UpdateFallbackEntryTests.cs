using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// "关于 → 检查更新"失败时的人工出口：现场出过"检查得到新版本、下载一路失败"，
/// 用户只能反复点重试。这里守住按钮该用哪个地址、什么时候不该给。
/// </summary>
public sealed class UpdateFallbackEntryTests
{
    private static AppPatchPreparationResult Failed(
        string fullDownloadUrl = "",
        string fullDownloadFallbackUrl = "") =>
        new(AppPatchPreparationStatus.Failed, "补丁下载或校验失败", fullDownloadUrl, fullDownloadFallbackUrl);

    [Fact]
    public void CheckResultDownloadUrl_Wins()
    {
        // 发布说明里带百度网盘时，检查结果给的就是网盘链接，优先用它
        string url = UpdateAvailableDialog.ResolveFallbackDownloadUrl(
            new UpdateCheckResult { DownloadUrl = "https://pan.baidu.com/s/xxx" },
            Failed("https://github.com/PackingProof/PackingProof-Desktop/releases/tag/v0.0.76"));

        Assert.Equal("https://pan.baidu.com/s/xxx", url);
    }

    [Fact]
    public void ManifestFullDownloadPage_IsUsedWhenCheckResultHasNoUrl()
    {
        string url = UpdateAvailableDialog.ResolveFallbackDownloadUrl(
            new UpdateCheckResult(),
            Failed("https://gitee.com/PackingProof/PackingProof-Desktop/releases/tag/v0.0.76"));

        Assert.Equal("https://gitee.com/PackingProof/PackingProof-Desktop/releases/tag/v0.0.76", url);
    }

    [Fact]
    public void ManifestFallbackPage_IsUsedAsLastResort()
    {
        string url = UpdateAvailableDialog.ResolveFallbackDownloadUrl(
            new UpdateCheckResult(),
            Failed(fullDownloadFallbackUrl: "https://pan.baidu.com/s/yyy"));

        Assert.Equal("https://pan.baidu.com/s/yyy", url);
    }

    [Fact]
    public void NoUrlAnywhere_MeansNoFallbackButton()
    {
        string url = UpdateAvailableDialog.ResolveFallbackDownloadUrl(
            new UpdateCheckResult(),
            Failed());

        Assert.Equal("", url);
    }
}
