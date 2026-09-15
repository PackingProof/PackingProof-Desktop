using ExpressPackingMonitoring.Data;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 导出单号必须带上界面上已经生效的设备筛选，
/// 否则筛了设备导出来的表里还会混进别的设备的单号。
/// </summary>
public sealed class OrderNumberExportFilterTests
{
    [Fact]
    public void BuildWhere_DeviceId_FiltersExternalDevice()
    {
        (string sql, IReadOnlyList<(string Name, string Value)> parameters) =
            new OrderNumberExportFilter(null, null, "", "DEV-1").BuildWhere("v");

        Assert.Contains("v.SourceDeviceId = @deviceId", sql);
        Assert.Contains("v.SourceType = 'external'", sql);
        Assert.Contains(("deviceId", "DEV-1"), parameters);
    }

    [Fact]
    public void BuildWhere_SourceNameOnly_FiltersByDeviceName()
    {
        (string sql, IReadOnlyList<(string Name, string Value)> parameters) =
            new OrderNumberExportFilter(null, null, "", "", "手机1").BuildWhere("v");

        Assert.Contains("v.SourceDeviceName = @sourceName", sql);
        Assert.Contains(("sourceName", "手机1"), parameters);
    }

    /// <summary>
    /// 多台同名设备在下拉里合并成一项后，导出必须按设备号集合命中，
    /// 否则改名那台设备改名前的录像会漏在导出结果之外。
    /// </summary>
    [Fact]
    public void BuildWhere_DeviceIdSet_UsesInClauseForEveryMember()
    {
        (string sql, IReadOnlyList<(string Name, string Value)> parameters) =
            new OrderNumberExportFilter(
                null,
                null,
                "",
                "",
                "安卓1",
                "external",
                new[] { "dev-1", "dev-2", "dev-1" }).BuildWhere("v");

        Assert.Contains("v.SourceDeviceId IN (@exportDeviceId0, @exportDeviceId1)", sql);
        Assert.Contains("v.SourceType = 'external'", sql);
        Assert.Contains(("exportDeviceId0", "dev-1"), parameters);
        Assert.Contains(("exportDeviceId1", "dev-2"), parameters);
        // 重复设备号只保留一次，避免参数名撞车。
        Assert.DoesNotContain(parameters, item => item.Name == "exportDeviceId2");
        Assert.DoesNotContain("@sourceName", sql);
    }

    [Fact]
    public void BuildWhere_LocalSource_ExcludesExternalRecords()
    {
        (string sql, _) = new OrderNumberExportFilter(null, null, "", "", "本机", "pc").BuildWhere("v");

        Assert.Contains("v.SourceType <> 'external'", sql);
        Assert.DoesNotContain("@deviceId", sql);
    }

    [Fact]
    public void BuildWhere_NoSourceFilter_DoesNotTouchDeviceColumns()
    {
        (string sql, IReadOnlyList<(string Name, string Value)> parameters) =
            new OrderNumberExportFilter(null, null, "").BuildWhere("v");

        Assert.Equal("", sql);
        Assert.Empty(parameters);
    }

    [Fact]
    public void BuildWhere_Mode_MatchesListSemantics()
    {
        (string returnSql, _) = new OrderNumberExportFilter(null, null, "退货").BuildWhere("v");
        (string shippingSql, _) = new OrderNumberExportFilter(null, null, "发货").BuildWhere("v");

        Assert.Contains("v.Mode IN ('return', '退货')", returnSql);
        // 早期录像没写 Mode，按发货算，否则老数据会在发货筛选里整批消失。
        Assert.Contains("v.Mode IS NULL", shippingSql);
    }

    [Fact]
    public void BuildWhere_DateRange_IsHalfOpen()
    {
        (string sql, IReadOnlyList<(string Name, string Value)> parameters) =
            new OrderNumberExportFilter(new DateTime(2026, 9, 1), new DateTime(2026, 9, 13)).BuildWhere("v");

        Assert.Contains("v.StartTime >= @startDate", sql);
        Assert.Contains("v.StartTime < @endDate", sql);
        Assert.Contains(("endDate", "2026-09-14 00:00:00"), parameters);
    }
}
