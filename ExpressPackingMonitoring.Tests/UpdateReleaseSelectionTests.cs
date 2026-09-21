using System.Text.Json;
using ExpressPackingMonitoring.UpdateCore;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 按平台挑最新版本：版本号两个平台共用，只修 Windows 的版本不能被当成
/// Mac 的新版本推给用户，所以 macOS 只认带 macOS 安装包（DMG）的 release。
/// 这里的命名规则必须与打包脚本产出的文件名一致，否则更新提示会永远不出现。
/// </summary>
public class UpdateReleaseSelectionTests
{
    [Theory]
    [InlineData("PackingProof-macOS-0.0.71.dmg", true)]
    [InlineData("PackingProof-macOS-0.0.71.zip", false)]
    [InlineData("PackingProof_Setup_v0.0.71.exe", false)]
    [InlineData("PackingProof_AppPatch_v0.0.71.zip", false)]
    [InlineData("", false)]
    public void MacOsPackageRecognition(string assetName, bool expected)
    {
        Assert.Equal(expected, UpdateReleaseSelection.IsMacOsPackage(assetName));
    }

    [Fact]
    public void SkipsReleasesWithoutMacPackage()
    {
        // 最新版只修了 Windows，Mac 应挑到带 DMG 的那一个
        const string releasesJson = """
        [
          {"tag_name":"v0.0.73","assets":[{"name":"PackingProof_Setup_v0.0.73.exe"}]},
          {"tag_name":"v0.0.72","assets":[{"name":"PackingProof_AppPatch_v0.0.72.zip"}]},
          {"tag_name":"v0.0.71","assets":[{"name":"PackingProof-macOS-0.0.71.dmg"}]}
        ]
        """;

        using var document = JsonDocument.Parse(releasesJson);
        int index = UpdateReleaseSelection.FindLatestWithAsset(
            document.RootElement,
            UpdateReleaseSelection.IsMacOsPackage);

        Assert.Equal(2, index);
        Assert.Equal(
            "v0.0.71",
            document.RootElement[index].GetProperty("tag_name").GetString());
    }

    [Fact]
    public void ReturnsMinusOneWhenNoPlatformPackageExists()
    {
        const string releasesJson = """
        [{"tag_name":"v0.0.73","assets":[{"name":"PackingProof_Setup_v0.0.73.exe"}]}]
        """;

        using var document = JsonDocument.Parse(releasesJson);
        Assert.Equal(
            -1,
            UpdateReleaseSelection.FindLatestWithAsset(
                document.RootElement,
                UpdateReleaseSelection.IsMacOsPackage));
    }
}
