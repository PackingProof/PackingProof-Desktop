using System.Text.Json;
using ExpressPackingMonitoring;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 保存配置时的按字段三方合并。
/// 背景：长时间持有 AppConfig 的进程整份回写时，会把别人期间的改动一起抹掉
/// （实测出现过保存容量被写回旧值、已记住的主机被清空），所以保存前要和磁盘上的
/// 最新配置合并：调用方改过的字段用调用方的，没改过的用磁盘上的。
/// </summary>
public class ConfigMergeTests
{
    [Fact]
    public void KeepsDiskValuesForFieldsCallerDidNotTouch()
    {
        const string baseJson =
            """{"DeploymentPreset":"MobileBackupHost","StorageLocations":[{"Path":"/x","ReserveGB":128}]}""";
        const string latestJson =
            """{"DeploymentPreset":"ViewerClient","StorageLocations":[{"Path":"/x","ReserveGB":10}]}""";

        // 调用方这份配置什么都没改，保存时必须跟着磁盘走
        string merged = WorkstationConfigStore.MergeWithLatestJson(baseJson, latestJson, baseJson);

        using var document = JsonDocument.Parse(merged);
        Assert.Equal(
            "ViewerClient",
            document.RootElement.GetProperty("DeploymentPreset").GetString());
        Assert.Equal(
            10,
            document.RootElement.GetProperty("StorageLocations")[0].GetProperty("ReserveGB").GetInt32());
    }

    [Fact]
    public void KeepsCallerValuesForChangedFields()
    {
        const string baseJson = """{"Fps":15,"DeploymentPreset":"MobileBackupHost"}""";
        const string latestJson = """{"Fps":15,"DeploymentPreset":"MobileBackupHost"}""";
        const string oursJson = """{"Fps":30,"DeploymentPreset":"MobileBackupHost"}""";

        string merged = WorkstationConfigStore.MergeWithLatestJson(baseJson, latestJson, oursJson);

        using var document = JsonDocument.Parse(merged);
        Assert.Equal(30, document.RootElement.GetProperty("Fps").GetInt32());
    }

    [Fact]
    public void KeepsFieldsThatOnlyExistOnDisk()
    {
        const string baseJson = """{"Fps":15}""";
        const string latestJson = """{"Fps":15,"NewOption":true}""";
        const string oursJson = """{"Fps":30}""";

        string merged = WorkstationConfigStore.MergeWithLatestJson(baseJson, latestJson, oursJson);

        using var document = JsonDocument.Parse(merged);
        Assert.True(document.RootElement.GetProperty("NewOption").GetBoolean());
        Assert.Equal(30, document.RootElement.GetProperty("Fps").GetInt32());
    }

    [Fact]
    public void WithoutBaseline_WritesCallerCopyUnchanged()
    {
        const string oursJson = """{"Fps":30}""";

        Assert.Equal(
            oursJson,
            WorkstationConfigStore.MergeWithLatestJson("", """{"Fps":15}""", oursJson));
    }
}
