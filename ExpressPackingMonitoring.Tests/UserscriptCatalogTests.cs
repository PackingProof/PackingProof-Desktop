using ExpressPackingMonitoring.Services;
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
        Assert.Contains(catalog.GetAll(source), item => item.Id == imported.Id);
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

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
    }
}
