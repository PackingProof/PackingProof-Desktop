using System.Text.Json;
using ExpressPackingMonitoring.UpdateCore;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 按平台挑最新版本：版本号两个平台共用，只修 Windows 的版本不能被当成
/// Mac 的新版本推给用户，只发 macOS 的版本也不能推给 Windows，
/// 所以 macOS 只认带 DMG 的 release、Windows 只认带 update_v*.json 的 release。
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

    [Theory]
    [InlineData("update_v0.0.71.json", true)]
    [InlineData("UPDATE_V0.0.71.JSON", true)]
    [InlineData("update.json", false)]
    [InlineData("update_v0.0.71.zip", false)]
    [InlineData("PackingProof_Setup_v0.0.71.exe", false)]
    [InlineData("PackingProof-macOS-0.0.71.dmg", false)]
    [InlineData("", false)]
    public void UpdateManifestRecognition(string assetName, bool expected)
    {
        Assert.Equal(expected, UpdateReleaseSelection.IsUpdateManifest(assetName));
    }

    [Fact]
    public void SkipsMacOnlyNewestReleaseForWindows()
    {
        // 最新版只发了 macOS（只有 DMG），Windows 要挑到带更新清单的那一个
        const string releasesJson = """
        [
          {"tag_name":"v0.0.73","assets":[{"name":"PackingProof-macOS-0.0.73.dmg"}]},
          {"tag_name":"v0.0.72","assets":[{"name":"update_v0.0.72.json"}]}
        ]
        """;

        using var document = JsonDocument.Parse(releasesJson);
        int index = UpdateReleaseSelection.FindLatestWithAsset(
            document.RootElement,
            UpdateReleaseSelection.PlatformAssetPredicate(isMacOs: false));

        Assert.Equal(1, index);
        Assert.Equal(
            "v0.0.72",
            document.RootElement[index].GetProperty("tag_name").GetString());
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

    /// <summary>
    /// Gitee 的 releases 是**旧 → 新**返回（GitHub 是新 → 旧），所以不能按位置挑第一个匹配项：
    /// 那样在 Gitee 上会挑到列表里最旧的一版，正式发了新版也永远提示"已是最新"。
    /// 这里给一份旧→新的列表，必须挑出版本最高的那个。
    /// </summary>
    [Fact]
    public void PicksHighestVersionEvenWhenListIsOldestFirst()
    {
        const string releasesJson = """
        [
          {"tag_name":"v0.0.41","assets":[{"name":"update_v0.0.41.json"}]},
          {"tag_name":"v0.0.42","assets":[{"name":"update_v0.0.42.json"}]},
          {"tag_name":"v0.0.73","assets":[{"name":"update_v0.0.73.json"}]},
          {"tag_name":"v0.0.74","assets":[{"name":"update_v0.0.74.json"}]}
        ]
        """;

        using var document = JsonDocument.Parse(releasesJson);
        int index = UpdateReleaseSelection.FindLatestWithAsset(
            document.RootElement,
            UpdateReleaseSelection.IsUpdateManifest);

        Assert.Equal(3, index);
        Assert.Equal("v0.0.74", document.RootElement[index].GetProperty("tag_name").GetString());
    }

    /// <summary>版本号比大小要看数值，不能按字符串比（"v0.0.9" 比 "v0.0.10" 旧）。</summary>
    [Fact]
    public void ComparesVersionsNumerically()
    {
        Assert.True(UpdateReleaseSelection.CompareVersions("v0.0.10", "v0.0.9") > 0);
        Assert.True(UpdateReleaseSelection.CompareVersions("0.0.74", "v0.0.73") > 0);
        Assert.True(UpdateReleaseSelection.CompareVersions("v0.0.74", "0.0.74-91-g174bda9b") == 0);
        Assert.True(UpdateReleaseSelection.CompareVersions("v0.0.73", "v0.0.74") < 0);

        // 以后改到 0.1.X / 1.X.Y 也要按数值比较：跨次版本、跨主版本、两位数都不能按字符串比
        Assert.True(UpdateReleaseSelection.CompareVersions("v0.1.0", "v0.0.74") > 0);
        Assert.True(UpdateReleaseSelection.CompareVersions("v1.0.0", "v0.9.99") > 0);
        Assert.True(UpdateReleaseSelection.CompareVersions("v1.10.0", "v1.9.0") > 0);
        Assert.True(UpdateReleaseSelection.CompareVersions("v1.0.1", "v1.0.0") > 0);
        Assert.True(UpdateReleaseSelection.CompareVersions("v10.0.0", "v9.99.99") > 0);
        Assert.True(UpdateReleaseSelection.CompareVersions("v0.1.0", "v0.1.0-3-gabc1234") == 0);
    }

    /// <summary>
    /// 同一批 release，无论服务端按新→旧还是旧→新返回，挑中的必须是同一版（版本最高的那一版）。
    /// 这条盯的是"不能依赖数组顺序"——现场那个 bug 就是踩在 Gitee 的旧→新顺序上。
    /// </summary>
    [Fact]
    public void SelectionDoesNotDependOnServerOrder()
    {
        const string newestFirst = """
        [
          {"tag_name":"v0.0.74","assets":[{"name":"update_v0.0.74.json"}]},
          {"tag_name":"v0.0.73","assets":[{"name":"update_v0.0.73.json"}]},
          {"tag_name":"v0.0.42","assets":[{"name":"update_v0.0.42.json"}]}
        ]
        """;
        const string oldestFirst = """
        [
          {"tag_name":"v0.0.42","assets":[{"name":"update_v0.0.42.json"}]},
          {"tag_name":"v0.0.73","assets":[{"name":"update_v0.0.73.json"}]},
          {"tag_name":"v0.0.74","assets":[{"name":"update_v0.0.74.json"}]}
        ]
        """;

        using var newest = JsonDocument.Parse(newestFirst);
        using var oldest = JsonDocument.Parse(oldestFirst);

        int newestIndex = UpdateReleaseSelection.FindLatestWithAsset(
            newest.RootElement,
            UpdateReleaseSelection.IsUpdateManifest);
        int oldestIndex = UpdateReleaseSelection.FindLatestWithAsset(
            oldest.RootElement,
            UpdateReleaseSelection.IsUpdateManifest);

        Assert.Equal(
            "v0.0.74",
            newest.RootElement[newestIndex].GetProperty("tag_name").GetString());
        Assert.Equal(
            "v0.0.74",
            oldest.RootElement[oldestIndex].GetProperty("tag_name").GetString());
    }

    /// <summary>最新一版没带本平台资产时，仍然要在带资产的里面挑版本最高的，而不是挑第一个。</summary>
    [Fact]
    public void PicksHighestVersionThatHasTheAsset()
    {
        const string releasesJson = """
        [
          {"tag_name":"v0.0.74","assets":[{"name":"PackingProof-macOS-0.0.74.dmg"}]},
          {"tag_name":"v0.0.72","assets":[{"name":"update_v0.0.72.json"}]},
          {"tag_name":"v0.0.73","assets":[{"name":"update_v0.0.73.json"}]}
        ]
        """;

        using var document = JsonDocument.Parse(releasesJson);
        int index = UpdateReleaseSelection.FindLatestWithAsset(
            document.RootElement,
            UpdateReleaseSelection.IsUpdateManifest);

        Assert.Equal("v0.0.73", document.RootElement[index].GetProperty("tag_name").GetString());
    }

    /// <summary>
    /// 渠道规则：预览版（prerelease）默认不参与挑选，测试机显式放开时才收。
    /// 以前标了预览版也会被推给所有店铺，这条守的就是那个坑。
    /// </summary>
    [Fact]
    public void SkipsPrereleaseUnlessExplicitlyAllowed()
    {
        const string releasesJson = """
        [
          {"tag_name":"v0.0.76","prerelease":true,"assets":[{"name":"update_v0.0.76.json"}]},
          {"tag_name":"v0.0.75","prerelease":false,"assets":[{"name":"update_v0.0.75.json"}]}
        ]
        """;

        using var document = JsonDocument.Parse(releasesJson);
        int defaultIndex = UpdateReleaseSelection.FindLatestWithAsset(
            document.RootElement,
            UpdateReleaseSelection.IsUpdateManifest);
        int testMachineIndex = UpdateReleaseSelection.FindLatestWithAsset(
            document.RootElement,
            UpdateReleaseSelection.IsUpdateManifest,
            allowPrerelease: true);

        Assert.Equal("v0.0.75", document.RootElement[defaultIndex].GetProperty("tag_name").GetString());
        Assert.Equal("v0.0.76", document.RootElement[testMachineIndex].GetProperty("tag_name").GetString());
    }

    /// <summary>列表里只有预览版时，正式渠道必须认为"没有可更新版本"，不能退回随便挑一个。</summary>
    [Fact]
    public void ReturnsNothingWhenOnlyPrereleasesExist()
    {
        const string releasesJson = """
        [
          {"tag_name":"v0.0.76","prerelease":true,"assets":[{"name":"update_v0.0.76.json"}]}
        ]
        """;

        using var document = JsonDocument.Parse(releasesJson);

        Assert.Equal(
            -1,
            UpdateReleaseSelection.FindLatestWithAsset(
                document.RootElement,
                UpdateReleaseSelection.IsUpdateManifest));
    }

    /// <summary>草稿任何情况下都不推，即使测试机放开了预览版也一样。</summary>
    [Fact]
    public void NeverPicksDraftEvenWhenPrereleaseIsAllowed()
    {
        const string releasesJson = """
        [
          {"tag_name":"v0.0.77","draft":true,"assets":[{"name":"update_v0.0.77.json"}]},
          {"tag_name":"v0.0.75","assets":[{"name":"update_v0.0.75.json"}]}
        ]
        """;

        using var document = JsonDocument.Parse(releasesJson);
        int index = UpdateReleaseSelection.FindLatestWithAsset(
            document.RootElement,
            UpdateReleaseSelection.IsUpdateManifest,
            allowPrerelease: true);

        Assert.Equal("v0.0.75", document.RootElement[index].GetProperty("tag_name").GetString());
    }

    /// <summary>服务端不返回 prerelease 字段时按正式版处理，不能因为字段缺失把版本藏起来。</summary>
    [Fact]
    public void MissingPrereleaseFlagCountsAsStable()
    {
        const string releasesJson = """
        [
          {"tag_name":"v0.0.75","assets":[{"name":"update_v0.0.75.json"}]}
        ]
        """;

        using var document = JsonDocument.Parse(releasesJson);
        int index = UpdateReleaseSelection.FindLatestWithAsset(
            document.RootElement,
            UpdateReleaseSelection.IsUpdateManifest);

        Assert.Equal("v0.0.75", document.RootElement[index].GetProperty("tag_name").GetString());
    }
}
