using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 摄像头设备下拉的共用规则：主摄/副摄互相排除对方占用的设备、
/// 自己当前那台保留在列表里、被占用或消失时要退回"无"。
/// 主摄、副摄（以及以后的第三第四路）都调这一份，这里把规则钉死。
/// </summary>
public sealed class CameraDeviceSelectionPolicyTests
{
    private static readonly CameraDeviceChoice None = new("无", "none", "", -1);
    private static readonly CameraDeviceChoice CamA = new("A", "usb", "moniker-a", 0);
    private static readonly CameraDeviceChoice CamB = new("B", "usb", "moniker-b", 1);
    private static readonly CameraDeviceChoice Network = new("网络摄像头", "network", "", -1);
    private static readonly CameraDeviceChoice[] All = [None, CamA, CamB, Network];

    [Fact]
    public void Project_ExcludesDevicesTakenByOtherChannels()
    {
        IReadOnlyList<CameraDeviceChoice> forSecondary =
            CameraDeviceSelectionPolicy.Project(All, selfMoniker: "", otherMonikers: ["moniker-a"]);

        Assert.DoesNotContain(forSecondary, c => c.Moniker == "moniker-a");
        Assert.Contains(forSecondary, c => c.Moniker == "moniker-b");
        Assert.Contains(forSecondary, c => c.Kind == "network");
        Assert.Contains(forSecondary, c => c.Kind == "none");
    }

    /// <summary>
    /// 一台 USB 摄像头都没有时，下拉里会同时出现"未检测到摄像头"和"网络摄像头"，两者都不占用本机设备。
    /// 选中判定必须按身份区分，否则重算下拉时会把用户选好的网络摄像头顶回"未检测到摄像头"，
    /// 网络摄像头地址框跟着被收起来。
    /// </summary>
    [Fact]
    public void SelectIdentityIndex_KeepsNetworkCameraWhenNoUsbDeviceIsPresent()
    {
        var choices = new List<CameraDeviceChoice>
        {
            new("未检测到摄像头", "usb", "", 0),
            new("网络摄像头（手动地址）", "network", CameraDeviceSelectionPolicy.NetworkIdentity, -1)
        };

        Assert.Equal(
            1,
            CameraDeviceSelectionPolicy.SelectIdentityIndex(choices, CameraDeviceSelectionPolicy.NetworkIdentity));
        Assert.Equal(0, CameraDeviceSelectionPolicy.SelectIdentityIndex(choices, ""));
        Assert.Equal(-1, CameraDeviceSelectionPolicy.SelectIdentityIndex([], "network:"));
    }

    /// <summary>网络摄像头的身份按"类型"给：副摄那一路的下拉项标识是空串，但也不能和"无"混起来。</summary>
    [Fact]
    public void IdentityOf_DistinguishesNetworkChoiceFromNoDevicePlaceholder()
    {
        Assert.Equal(
            "network:",
            CameraDeviceSelectionPolicy.IdentityOf(new CameraDeviceChoice("网络摄像头", "network", "", -1)));
        Assert.Equal(
            "",
            CameraDeviceSelectionPolicy.IdentityOf(new CameraDeviceChoice("未检测到摄像头", "usb", "", 0)));
        Assert.Equal("moniker-a", CameraDeviceSelectionPolicy.IdentityOf(CamA));
    }

    [Fact]
    public void Project_KeepsOwnCurrentDeviceEvenIfListedAsTaken()
    {
        IReadOnlyList<CameraDeviceChoice> forSecondary =
            CameraDeviceSelectionPolicy.Project(All, selfMoniker: "moniker-a", otherMonikers: ["moniker-a"]);

        Assert.Contains(forSecondary, c => c.Moniker == "moniker-a");
    }

    [Theory]
    [InlineData("", "moniker-a", true)]          // 没选设备：无需回退
    [InlineData("moniker-a", "moniker-a", false)] // 被另一路占用：必须回退
    [InlineData("moniker-c", "moniker-a", false)] // 设备已不存在：必须回退
    [InlineData("moniker-b", "moniker-a", true)]  // 仍然可用：保留
    public void CanKeepSelection_DecidesWhetherToFallBackToNone(
        string selfMoniker,
        string otherMoniker,
        bool expected)
    {
        Assert.Equal(
            expected,
            CameraDeviceSelectionPolicy.CanKeepSelection(selfMoniker, [otherMoniker], All));
    }

    /// <summary>
    /// 两路撞车（历史配置里主副摄存了同一台）时只能有一个结果：
    /// 优先级高的主摄保留，副摄退回"无"。两边各自投影会互相挤，下拉会来回跳。
    /// </summary>
    [Fact]
    public void ResolveOwnership_LetsTheHigherPriorityChannelKeepTheDevice()
    {
        IReadOnlyList<string> resolved = CameraDeviceSelectionPolicy.ResolveOwnership(
            ["moniker-b", "moniker-b"],
            All);

        Assert.Equal("moniker-b", resolved[0]);
        Assert.Equal("", resolved[1]);
    }

    [Fact]
    public void ResolveOwnership_ClearsDevicesThatAreNoLongerPresent()
    {
        IReadOnlyList<string> resolved = CameraDeviceSelectionPolicy.ResolveOwnership(
            ["moniker-gone", "moniker-b"],
            All);

        Assert.Equal("", resolved[0]);
        Assert.Equal("moniker-b", resolved[1]);
    }

    [Fact]
    public void ResolveOwnership_KeepsDistinctDevicesAndIgnoresEmptySelections()
    {
        IReadOnlyList<string> resolved = CameraDeviceSelectionPolicy.ResolveOwnership(
            [null, "moniker-b"],
            All);

        Assert.Equal("", resolved[0]);
        Assert.Equal("moniker-b", resolved[1]);
    }
}
