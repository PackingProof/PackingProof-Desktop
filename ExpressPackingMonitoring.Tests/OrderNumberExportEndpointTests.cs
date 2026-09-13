using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// Web 端导出单号的参数解析与响应头规则。
/// 文件名是中文，Content-Disposition 处理不对浏览器就会存成乱码。
/// </summary>
public sealed class OrderNumberExportEndpointTests
{
    [Fact]
    public void ParseRequest_ReadsDatesAndMode()
    {
        OrderNumberExportEndpoint.Request request =
            OrderNumberExportEndpoint.ParseRequest("2026-09-01", "2026-09-13", "退货");

        Assert.Equal(new DateTime(2026, 9, 1), request.StartDate);
        Assert.Equal(new DateTime(2026, 9, 13), request.EndDate);
        Assert.Equal("return", request.Mode);
    }

    /// <summary>日期填反时交换，而不是返回空表让人以为没有数据。</summary>
    [Fact]
    public void ParseRequest_SwapsInvertedRange()
    {
        OrderNumberExportEndpoint.Request request =
            OrderNumberExportEndpoint.ParseRequest("2026-09-13", "2026-09-01", "");

        Assert.Equal(new DateTime(2026, 9, 1), request.StartDate);
        Assert.Equal(new DateTime(2026, 9, 13), request.EndDate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("不是日期")]
    public void ParseRequest_IgnoresUnparsableDates(string? value)
    {
        OrderNumberExportEndpoint.Request request =
            OrderNumberExportEndpoint.ParseRequest(value, value, "");

        Assert.Null(request.StartDate);
        Assert.Null(request.EndDate);
    }

    /// <summary>无法识别的类型按不筛选处理，不能误判成某一种类型。</summary>
    [Theory]
    [InlineData("unknown")]
    [InlineData("all")]
    [InlineData("")]
    public void ParseRequest_UnknownMode_IsNoFilter(string mode)
    {
        Assert.Equal("", OrderNumberExportEndpoint.ParseRequest("", "", mode).Mode);
    }

    /// <summary>文件名带日期与类型，店员连续导出多份时不会互相覆盖。</summary>
    [Fact]
    public void BuildFileName_IncludesRangeAndMode()
    {
        var request = new OrderNumberExportEndpoint.Request(
            new DateTime(2026, 9, 1), new DateTime(2026, 9, 13), "return");

        string name = OrderNumberExportEndpoint.BuildFileName(
            request, new DateTime(2026, 9, 13, 10, 30, 0));

        Assert.Equal("单号_20260901-20260913_退货_20260913_103000.xlsx", name);
    }

    [Fact]
    public void BuildFileName_NoFilters_SaysAll()
    {
        var request = new OrderNumberExportEndpoint.Request(null, null, "");

        string name = OrderNumberExportEndpoint.BuildFileName(
            request, new DateTime(2026, 9, 13, 10, 30, 0));

        Assert.StartsWith("单号_全部_", name);
        Assert.EndsWith(".xlsx", name);
    }

    [Fact]
    public void BuildFileName_OpenEndedRange_IsDescribed()
    {
        var now = new DateTime(2026, 9, 13);

        Assert.Contains("20260901起", OrderNumberExportEndpoint.BuildFileName(
            new OrderNumberExportEndpoint.Request(new DateTime(2026, 9, 1), null, ""), now));
        Assert.Contains("截至20260913", OrderNumberExportEndpoint.BuildFileName(
            new OrderNumberExportEndpoint.Request(null, new DateTime(2026, 9, 13), ""), now));
    }

    /// <summary>
    /// 中文文件名必须走 RFC 5987 的 filename*，否则浏览器存下来是乱码；
    /// 同时保留 ASCII 回退名给不支持的老浏览器。
    /// </summary>
    [Fact]
    public void ContentDisposition_EncodesChineseFileName()
    {
        string header = OrderNumberExportEndpoint.BuildContentDisposition("单号_全部.xlsx");

        Assert.StartsWith("attachment;", header);
        Assert.Contains("filename=\"order-numbers.xlsx\"", header);
        Assert.Contains("filename*=UTF-8''", header);
        // 中文必须是百分号编码，不能原样出现在头里。
        Assert.DoesNotContain("单号", header);
        Assert.Contains(Uri.EscapeDataString("单号_全部.xlsx"), header);
    }
}
