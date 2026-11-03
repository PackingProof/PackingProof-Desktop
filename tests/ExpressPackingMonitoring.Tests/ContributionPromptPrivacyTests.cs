using System.Text;
using System.Xml.Linq;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ContributionPromptPrivacyTests
{
    [Fact]
    public void ContributionPromptDisclosesIdentifiersInAllLanguages()
    {
        string repositoryRoot = FindRepositoryRoot();
        string[] resourceFiles =
        [
            "Strings.resx",
            "Strings.zh-Hans.resx",
            "Strings.ja-JP.resx"
        ];

        foreach (string resourceFile in resourceFiles)
        {
            XDocument document = XDocument.Load(Path.Combine(
                repositoryRoot,
                "src",
                "ExpressPackingMonitoring",
                "Resources",
                resourceFile));
            string[] promptPrefixes =
            [
                "你可以贡献一些本机识别样本",
                "如果当前识别不够可靠"
            ];
            foreach (string prefix in promptPrefixes)
            {
                Assert.Single(
                    document.Root!.Elements("data"),
                    element =>
                        element.Attribute("name")?.Value.StartsWith(
                            prefix,
                            StringComparison.Ordinal) == true);
            }

            string noAutoUpload = document.Root!
                .Elements("data")
                .Single(element =>
                    element.Attribute("name")?.Value.StartsWith(
                        "我们不会主动上传",
                        StringComparison.Ordinal) == true)
                .Element("value")!
                .Value;
            Assert.True(
                noAutoUpload.Contains("upload", StringComparison.OrdinalIgnoreCase)
                || noAutoUpload.Contains("アップロード", StringComparison.OrdinalIgnoreCase)
                || noAutoUpload.Contains("上传", StringComparison.OrdinalIgnoreCase),
                $"{resourceFile} 未说明不会主动上传：{noAutoUpload}");

            string privacy = document.Root!
                .Elements("data")
                .Single(element =>
                    element.Attribute("name")?.Value.StartsWith(
                        "包内不含录像",
                        StringComparison.Ordinal) == true)
                .Element("value")!
                .Value;
            Assert.True(
                privacy.Contains("device ID", StringComparison.OrdinalIgnoreCase)
                || privacy.Contains("端末 ID", StringComparison.OrdinalIgnoreCase)
                || privacy.Contains("设备标识", StringComparison.OrdinalIgnoreCase),
                $"{resourceFile} 未披露匿名设备标识：{privacy}");
            Assert.True(
                privacy.Contains("fingerprint", StringComparison.OrdinalIgnoreCase)
                || privacy.Contains("指紋", StringComparison.OrdinalIgnoreCase)
                || privacy.Contains("指纹", StringComparison.OrdinalIgnoreCase),
                $"{resourceFile} 未披露来源指纹：{privacy}");
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
