using System.Xml.Linq;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 主项目关闭了 EnableDefaultCompileItems，但 UseWPF 仍会自动收录 *.xaml。
/// 漏登记 *.xaml.cs 时 XAML 照样编译通过，只是 code-behind 整体缺失：
/// 构造函数不再调用 InitializeComponent，控件在界面上直接空白消失。
/// 这种失效不会报错，只能靠守卫拦住。
/// </summary>
public sealed class CodeBehindCompileItemTests
{
    [Fact]
    public void EveryCodeBehindFile_IsRegisteredAsCompileItem()
    {
        string root = FindRepositoryRoot();
        string project = Path.Combine(root, "ExpressPackingMonitoring");
        string projectFile = Path.Combine(project, "ExpressPackingMonitoring.csproj");

        XDocument document = XDocument.Load(projectFile);
        Assert.Equal(
            "false",
            document.Descendants("EnableDefaultCompileItems").Single().Value.Trim());

        HashSet<string> registered = document.Descendants("Compile")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => include!.Replace('/', Path.DirectorySeparatorChar))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string[] missing = Directory
            .GetFiles(project, "*.xaml.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(project, path))
            .Where(relative => !registered.Contains(relative))
            .OrderBy(relative => relative, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "csproj 缺少 code-behind 的 Compile 登记: " + string.Join(" | ", missing));
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
