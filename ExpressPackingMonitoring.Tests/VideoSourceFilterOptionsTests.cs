using ExpressPackingMonitoring.Data;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 数据库按设备号分组，同一台手机换过设备号就会出现多条，
/// 下拉必须按显示名合并，否则会看到好几个"手机1"。
/// </summary>
public sealed class VideoSourceFilterOptionsTests
{
    private static IReadOnlyList<VideoSourceFilterOption> Build(params VideoSourceInfo[] sources) =>
        VideoSourceFilterOptions.Build(
            sources,
            source => string.Equals(source.SourceType, "external", StringComparison.OrdinalIgnoreCase)
                ? (string.IsNullOrWhiteSpace(source.DeviceName) ? "手机设备" : source.DeviceName)
                : "本机");

    [Fact]
    public void Build_SameNameDifferentDeviceId_MergesIntoOneOption()
    {
        IReadOnlyList<VideoSourceFilterOption> options = Build(
            new VideoSourceInfo("external", "dev-1", "手机1", 3),
            new VideoSourceInfo("external", "dev-2", "手机1", 4));

        VideoSourceFilterOption single = Assert.Single(options);
        Assert.Equal("手机1", single.Name);
        Assert.Equal(7, single.VideoCount);
        // 同名多设备不能再钉某一个设备号，否则筛出来少一半录像。
        Assert.Equal("", single.DeviceId);
    }

    [Fact]
    public void Build_SingleDevice_KeepsDeviceId()
    {
        VideoSourceFilterOption single = Assert.Single(Build(
            new VideoSourceInfo("external", "dev-1", "手机1", 2)));

        Assert.Equal("dev-1", single.DeviceId);
        Assert.Equal("external", single.SourceType);
    }

    [Fact]
    public void Build_LocalSources_CollapseToSingleLocalOption()
    {
        IReadOnlyList<VideoSourceFilterOption> options = Build(
            new VideoSourceInfo("pc", "", "", 5),
            new VideoSourceInfo("", "", "", 1));

        VideoSourceFilterOption single = Assert.Single(options);
        Assert.Equal("本机", single.Name);
        Assert.Equal("pc", single.SourceType);
        Assert.Equal(6, single.VideoCount);
    }

    [Fact]
    public void Build_ExternalWithoutDeviceId_IsSkipped()
    {
        Assert.Empty(Build(new VideoSourceInfo("external", "", "手机1", 2)));
    }

    [Fact]
    public void Build_DifferentNames_StaySeparateAndKeepOrder()
    {
        IReadOnlyList<VideoSourceFilterOption> options = Build(
            new VideoSourceInfo("external", "dev-1", "手机1", 1),
            new VideoSourceInfo("external", "dev-2", "冲击1", 1));

        Assert.Equal(new[] { "手机1", "冲击1" }, options.Select(option => option.Name));
    }
}
