using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Services.Extensions;
using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class UserscriptCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "epm-userscript-tests-" + Guid.NewGuid().ToString("N"));

    public UserscriptCatalogTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Import_RegistersScriptAndReportsMissingMaintenanceMarkers()
    {
        string source = Path.Combine(_directory, "scale.user.js");
        File.WriteAllText(source, "// ==UserScript==\n// @name Scale Demo\n// @namespace demo.scale\n// @version 1.4\n// @author Test\n// ==/UserScript==\nconsole.log('ok');\n", Encoding.UTF8);

        var catalog = new UserscriptCatalog(_directory);
        UserscriptDescriptor imported = catalog.Import(source);

        Assert.Equal("Scale Demo", imported.Name);
        Assert.Equal("1.4", imported.Version);
        Assert.Contains(imported.Warnings, warning => warning.Contains("设备", StringComparison.Ordinal));
        Assert.Contains(catalog.GetAll(), item => item.Id == imported.Id);
    }

    [Fact]
    public void GetCustomScripts_ListsImportedScriptsAndRemoveDeletesThem()
    {
        string source = Path.Combine(_directory, "scale.user.js");
        File.WriteAllText(source, "// ==UserScript==\n// @name Scale Demo\n// @namespace demo.scale\n// @version 1.4\n// ==/UserScript==\n", Encoding.UTF8);
        var catalog = new UserscriptCatalog(_directory);

        UserscriptDescriptor imported = catalog.Import(source);

        Assert.Contains(catalog.GetCustomScripts(), item => item.Id == imported.Id);
        Assert.True(catalog.Remove(imported.Id));
        Assert.DoesNotContain(catalog.GetCustomScripts(), item => item.Id == imported.Id);
        Assert.False(File.Exists(imported.SourcePath));
    }

    [Fact]
    public void BuildUserscriptChoice_UsesUniformCardAndHighlightsWarnings()
    {
        var normal = new UserscriptDescriptor { Id = "normal", Name = "Normal", Version = "1.0" };
        var warning = new UserscriptDescriptor
        {
            Id = "warning",
            Name = "Warning",
            Version = "1.0",
            Warnings = ["缺少设备占位符"]
        };

        string normalHtml = OfficialUserscriptMigrationService.BuildChoice(normal, "http", "127.0.0.1:5280");
        string warningHtml = OfficialUserscriptMigrationService.BuildChoice(warning, "http", "127.0.0.1:5280");

        Assert.Contains("class=\"script-choice is-maintainable\"", normalHtml);
        Assert.Contains("<span>版本</span> 1.0 · <span>可自动维护</span>", normalHtml);
        Assert.DoesNotContain("has-warning", normalHtml);
        Assert.Contains("class=\"script-choice has-warning\"", warningHtml);
        Assert.Contains("有提示：缺少设备占位符", warningHtml);
        Assert.Contains(">安装</a>", normalHtml);
        Assert.DoesNotContain("安装此脚本", normalHtml);
        Assert.Contains(
            "href=\"http://127.0.0.1:5280/api/userscripts/normal/download.user.js\"",
            normalHtml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUserscriptDownloadUrl_UsesGenericUserscriptEndpoint()
    {
        string url = OfficialUserscriptMigrationService.BuildDownloadUrl("http", "127.0.0.1:5280", "custom script");

        var uri = new Uri(url);
        Assert.Equal("/api/userscripts/custom script/download.user.js", Uri.UnescapeDataString(uri.AbsolutePath));
        Assert.EndsWith(".user.js", uri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", uri.Query);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
    }
}
