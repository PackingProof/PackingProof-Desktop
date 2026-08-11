using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class UserscriptStatusCardModelTests
{
    [Theory]
    [InlineData("订单联动已就绪", "已就绪")]
    [InlineData("需要更新订单联动", "需更新")]
    [InlineData("暂无订单接收设备", "暂无设备")]
    [InlineData("未配置订单联动", "未配置")]
    public void GetCardTexts_MapsShortStatusAndKeepsDetail(
        string statusText,
        string expectedShort)
    {
        (string shortStatus, string detailText) = UserscriptStatusCardModel.GetCardTexts(
            new UserscriptTargetStatus(statusText, "安装订单联动", "sig"));

        Assert.Equal(expectedShort, shortStatus);
        Assert.Equal(statusText, detailText);
    }
}
