using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 架构守卫：NAS 只上传、永不删除；PendingDeleteAt 完全弃用。
/// 按接口定义与调用点扫描，不绑定具体实现文件名。
/// </summary>
public sealed class ArchiveArchitectureGuardTests
{
    private static string FindRepositoryRoot()
    {
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(startPath));
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        throw new DirectoryNotFoundException("ExpressPackingMonitoring repository root was not found.");
    }

    private static string[] ProjectSourceFiles()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "ExpressPackingMonitoring");
        return Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    [Fact]
    public void ArchiveProviderInterface_DoesNotExposeDeleteCapability()
    {
        string interfaceFile = Path.Combine(
            FindRepositoryRoot(),
            "ExpressPackingMonitoring",
            "Services",
            "IArchiveProvider.cs");
        string source = File.ReadAllText(interfaceFile);

        Assert.DoesNotContain("DeleteAsync", source, StringComparison.Ordinal);
        Assert.Contains("PublishFileAsync", source, StringComparison.Ordinal);
        Assert.Contains("ProbeAsync", source, StringComparison.Ordinal);
        Assert.Contains("ComputeSha256Async", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ArchivePipeline_HasNoRemoteDeleteCalls()
    {
        string[] offenders = ProjectSourceFiles()
            .Where(path => File.ReadAllText(path).Contains("DeleteAsync", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "归档链路不应存在远端删除调用: " + string.Join(" | ", offenders));
    }

    [Fact]
    public void PendingDeleteAt_IsFullyDeprecated()
    {
        string[] offenders = ProjectSourceFiles()
            .Where(path => File.ReadAllText(path).Contains("PendingDeleteAt", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "PendingDeleteAt 已弃用，新代码不得引用: " + string.Join(" | ", offenders));
    }
}
