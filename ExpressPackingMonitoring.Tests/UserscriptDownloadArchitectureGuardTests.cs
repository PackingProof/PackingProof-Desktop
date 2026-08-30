using System.Text;
using System.Text.RegularExpressions;
using ExpressPackingMonitoring.Services.Extensions;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 架构守卫：新生成的油猴安装与更新地址必须以 .user.js 结尾。
/// 无后缀 /download 只允许作为旧版本兼容路由被解析，不能重新进入生成逻辑。
/// </summary>
public sealed class UserscriptDownloadArchitectureGuardTests
{
    private static readonly Regex SuffixlessManagedUserscriptUrl = new(
        "api/userscripts/[^\"\\r\\n]*/download(?!\\.user\\.js)",
        RegexOptions.CultureInvariant);

    [Fact]
    public void ManagedUserscriptUrlBuilder_AlwaysUsesRecognizableUserscriptSuffix()
    {
        string url = OfficialUserscriptMigrationService.BuildDownloadUrl(
            "http",
            "127.0.0.1:5280",
            "contract-check");

        Assert.EndsWith(".user.js", new Uri(url).AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionCode_DoesNotGenerateSuffixlessManagedUserscriptUrls()
    {
        string projectRoot = Path.Combine(FindRepositoryRoot(), "ExpressPackingMonitoring");
        string[] violations = Directory
            .GetFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(path))
            .SelectMany(path => FindViolations(projectRoot, path))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "无后缀 userscript 地址只允许用于旧路由解析，禁止生成到安装或更新链接："
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    private static IEnumerable<string> FindViolations(string projectRoot, string path)
    {
        string source = File.ReadAllText(path, Encoding.UTF8);
        foreach (Match match in SuffixlessManagedUserscriptUrl.Matches(source))
        {
            int lineNumber = source[..match.Index].Count(character => character == '\n') + 1;
            yield return $"{Path.GetRelativePath(projectRoot, path)}:{lineNumber}: {match.Value}";
        }
    }

    private static bool IsGeneratedPath(string path) =>
        path.Contains(
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
        || path.Contains(
            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);

    private static string FindRepositoryRoot()
    {
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(Path.GetFullPath(startPath));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("ExpressPackingMonitoring repository root was not found.");
    }
}
