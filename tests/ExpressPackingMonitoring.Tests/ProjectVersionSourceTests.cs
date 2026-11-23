using System.Text.RegularExpressions;
using System.Xml.Linq;
using ExpressPackingMonitoring.Config;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 软件版本号只允许有一个来源：仓根 Directory.Build.props。
/// 以前主程序与 Core 各写一份 &lt;Version&gt;，结果 Core 停在 0.0.78 而主程序升到 0.0.81；
/// 应用显示版本读的是 Core 程序集，于是开发构建里界面、更新判断和 Mac 打包都拿到旧号。
/// 这类漂移不会报错，只能靠守卫拦住。
/// </summary>
public sealed class ProjectVersionSourceTests
{
    [Fact]
    public void DirectoryBuildProps_DeclaresPlainSemanticVersion()
    {
        string propsPath = Path.Combine(FindRepositoryRoot(), "Directory.Build.props");
        Assert.True(File.Exists(propsPath), $"缺少版本号唯一来源：{propsPath}");

        string version = ReadVersion(XDocument.Load(propsPath), propsPath);
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+$"), version);
    }

    [Fact]
    public void NoProject_DeclaresItsOwnVersion()
    {
        string root = FindRepositoryRoot();
        string[] offenders = EnumerateProjectFiles(root)
            .Where(path => XDocument.Load(path).Descendants("Version").Any())
            .Select(path => Path.GetRelativePath(root, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "工程文件不得自己写 <Version>，请改 Directory.Build.props：" + string.Join(" | ", offenders));
    }

    [Fact]
    public void AppVersion_ReportsTheSharedVersion()
    {
        // AppVersion 读的是 Core 程序集的信息版本；Core 与主程序版本漂移时这里必挂。
        string propsPath = Path.Combine(FindRepositoryRoot(), "Directory.Build.props");
        string expected = ReadVersion(XDocument.Load(propsPath), propsPath);

        Assert.StartsWith(expected, AppVersion.Current, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionReaders_PointAtDirectoryBuildProps()
    {
        string root = FindRepositoryRoot();
        foreach (string relativePath in new[]
                 {
                     @"tools\Check-ReleasePrereqs.ps1",
                     @"tools\Republish-SameVersion.ps1",
                     @"tools\Publish-MacHost.sh",
                     @"tools\Publish-MacRelease.sh"
                 })
        {
            string content = File.ReadAllText(Path.Combine(root, relativePath), System.Text.Encoding.UTF8);
            Assert.Contains("Directory.Build.props", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void VersionReaders_AcceptConditionalVersionElement()
    {
        string root = FindRepositoryRoot();
        foreach (string relativePath in new[]
                 {
                     @"tools\Prepare-Release.ps1",
                     @"tools\Check-ReleasePrereqs.ps1",
                     @"tools\Republish-SameVersion.ps1",
                     @"tools\Publish-StorePackage.ps1"
                 })
        {
            string content = File.ReadAllText(
                Path.Combine(root, relativePath),
                System.Text.Encoding.UTF8);
            Assert.Contains(
                "<Version(?:\\s+[^>]*)?>",
                content,
                StringComparison.Ordinal);
        }
    }

    private static string ReadVersion(XDocument document, string path)
    {
        string? version = document.Descendants("Version").FirstOrDefault()?.Value.Trim();
        Assert.False(string.IsNullOrWhiteSpace(version), $"读不到 <Version>：{path}");
        return version!;
    }

    private static IEnumerable<string> EnumerateProjectFiles(string root)
    {
        return Directory
            .EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(IsRepositorySourcePath);
    }

    private static bool IsRepositorySourcePath(string fullPath)
    {
        foreach (string excluded in new[] { "bin", "obj", "package", ".tmp", "node_modules" })
        {
            string marker = Path.DirectorySeparatorChar + excluded + Path.DirectorySeparatorChar;
            if (fullPath.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static string FindRepositoryRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("找不到解决方案根目录");
    }
}
