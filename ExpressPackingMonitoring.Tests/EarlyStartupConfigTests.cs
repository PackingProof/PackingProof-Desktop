using System.Text;
using ExpressPackingMonitoring.Config;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 渲染模式在 WPF 静态初始化阶段就要定下来，此时没有日志也没有配置服务；
/// 这里锁定"读不到就回退硬件渲染"的兜底行为，避免配置异常把程序挡在启动之外。
/// </summary>
public sealed class EarlyStartupConfigTests
{
    [Fact]
    public void EnabledFlag_ForcesSoftwareRendering()
    {
        using var config = new TempConfig("""{"ForceSoftwareRendering": true}""");

        Assert.True(EarlyStartupConfig.ReadForceSoftwareRenderingFromConfig(config.FilePath));
    }

    [Theory]
    [InlineData("""{"ForceSoftwareRendering": false}""")]
    [InlineData("""{"ShowAdvancedSettings": true}""")]
    [InlineData("{}")]
    public void AbsentOrDisabledFlag_KeepsHardwareRendering(string json)
    {
        using var config = new TempConfig(json);

        Assert.False(EarlyStartupConfig.ReadForceSoftwareRenderingFromConfig(config.FilePath));
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    public void BrokenConfig_KeepsHardwareRenderingInsteadOfThrowing(string json)
    {
        using var config = new TempConfig(json);

        Assert.False(EarlyStartupConfig.ReadForceSoftwareRenderingFromConfig(config.FilePath));
    }

    [Fact]
    public void MissingConfigFile_KeepsHardwareRendering()
    {
        string missing = Path.Combine(
            Path.GetTempPath(),
            $"packingproof-early-startup-{Guid.NewGuid():N}",
            "config.json");

        Assert.False(EarlyStartupConfig.ReadForceSoftwareRenderingFromConfig(missing));
    }

    /// <summary>历史配置被手工编辑成字符串布尔值时也要照常识别。</summary>
    [Theory]
    [InlineData("\"true\"", true)]
    [InlineData("\"1\"", true)]
    [InlineData("\"false\"", false)]
    [InlineData("\"\"", false)]
    public void StringBooleanFlag_IsUnderstood(string rawValue, bool expected)
    {
        using var config = new TempConfig($$"""{"ForceSoftwareRendering": {{rawValue}}}""");

        Assert.Equal(expected, EarlyStartupConfig.ReadForceSoftwareRenderingFromConfig(config.FilePath));
    }

    private sealed class TempConfig : IDisposable
    {
        private readonly string _root;

        public TempConfig(string json)
        {
            _root = Path.Combine(Path.GetTempPath(), $"packingproof-early-startup-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            FilePath = Path.Combine(_root, "config.json");
            File.WriteAllText(FilePath, json, Encoding.UTF8);
        }

        public string FilePath { get; }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}

