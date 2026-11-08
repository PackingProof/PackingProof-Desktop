using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// Setup 版与 Microsoft Store 版可以并存，但更新入口必须完全分开：
/// 商店包不能夹带 Setup 的手工更新脚本，手动补丁也不能写进 WindowsApps。
/// 这里守住打包与安装两侧的边界，避免只修了应用内更新而漏掉双击脚本。
/// </summary>
public sealed class StoreEditionPackagingGuardTests
{
    [Fact]
    public void StorePackage_ContainsEditionMarkerAndRejectsSetupUpdateScripts()
    {
        string script = ReadRepositoryFile("tools", "Publish-StorePackage.ps1");
        string cleanPackageScript = ReadRepositoryFile("tools", "Publish-CleanPackage.ps1");
        string appProject = ReadRepositoryFile("src", "ExpressPackingMonitoring", "ExpressPackingMonitoring.csproj");
        string updateCoreProject = ReadRepositoryFile("src", "ExpressPackingMonitoring.UpdateCore", "ExpressPackingMonitoring.UpdateCore.csproj");

        Assert.Contains("PackingProof.PackageEdition.json", script, StringComparison.Ordinal);
        Assert.Contains("edition = \"store\"", script, StringComparison.Ordinal);
        Assert.Contains("商店包不能包含 Setup 版更新脚本", script, StringComparison.Ordinal);
        Assert.Contains("\"apply_app_patch.ps1\"", script, StringComparison.Ordinal);
        Assert.Contains("\"双击更新主程序.cmd\"", script, StringComparison.Ordinal);
        Assert.Contains("[string]$AppPayloadDir", script, StringComparison.Ordinal);
        Assert.Contains("Get-ChildItem -LiteralPath $resolvedAppPayloadDir -Force", script, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -Destination $payloadDir -Recurse -Force", script, StringComparison.Ordinal);
        Assert.Contains("runtime-seed 只服务 Setup 版", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $runtimeSeedDir -Recurse -Force", script, StringComparison.Ordinal);
        Assert.Contains("[string]$SymbolsDir", script, StringComparison.Ordinal);
        Assert.Contains(".appxsym", script, StringComparison.Ordinal);
        Assert.Contains("PackingProofCollectStoreSymbols=true", script, StringComparison.Ordinal);
        Assert.Contains("-p:DebugType=portable", script, StringComparison.Ordinal);
        Assert.Contains("-p:DebugSymbols=true", script, StringComparison.Ordinal);
        Assert.Contains("-p:DebugType=portable", cleanPackageScript, StringComparison.Ordinal);
        Assert.Contains("-p:DebugSymbols=true", cleanPackageScript, StringComparison.Ordinal);
        Assert.Contains("Store 符号包缺少程序集符号", script, StringComparison.Ordinal);
        Assert.Contains("PackingProofCollectStoreSymbols", appProject, StringComparison.Ordinal);
        Assert.Contains("<DebugType>none</DebugType>", updateCoreProject, StringComparison.Ordinal);
        Assert.DoesNotContain("<DebugType>portable</DebugType>", updateCoreProject, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupInstaller_WarnsAboutStoreCoexistenceAndBlocksWindowsApps()
    {
        string installer = ReadRepositoryFile("tools", "Installer", "ExpressPackingMonitoring.iss");

        Assert.Contains("function IsStorePackageInstalled", installer, StringComparison.Ordinal);
        Assert.Contains("StoreEditionNoticeShown", installer, StringComparison.Ordinal);
        Assert.Contains("StoreEditionDetected", installer, StringComparison.Ordinal);
        Assert.Contains("WindowsAppsInstallBlocked", installer, StringComparison.Ordinal);
        Assert.Contains("chinesesimplified.StoreEditionDetected", installer, StringComparison.Ordinal);
        Assert.Contains("english.StoreEditionDetected", installer, StringComparison.Ordinal);
        Assert.Contains("japanese.StoreEditionDetected", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualPatchScripts_RejectPackagedInstallAndDataPaths()
    {
        string appPatch = ReadRepositoryFile("tools", "Apply-AppPatch.ps1");
        string launcherPatch = ReadRepositoryFile("tools", "Apply-LauncherPatch.ps1");
        string appNotice = ReadRepositoryFile("tools", "Publish-CleanPackage.ps1");
        string launcherNotice = ReadRepositoryFile("tools", "Publish-LauncherBaseline.ps1");

        foreach (string script in new[] { appPatch, launcherPatch })
        {
            Assert.Contains("Test-IsPackagedInstallRoot", script, StringComparison.Ordinal);
            Assert.Contains("Test-IsPackagedUserDataPath", script, StringComparison.Ordinal);
            Assert.Contains("WindowsApps", script, StringComparison.Ordinal);
            Assert.Contains("AppxManifest.xml", script, StringComparison.Ordinal);
            Assert.Contains("PackingProof.PackageEdition.json", script, StringComparison.Ordinal);
        }

        Assert.Contains("本补丁只适用于 Setup 版", appNotice, StringComparison.Ordinal);
        Assert.Contains("Microsoft Store 版", appNotice, StringComparison.Ordinal);
        Assert.Contains("本补丁只适用于 Setup 版", launcherNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreDocumentation_ExplainsTheTwoEditionsStaySeparate()
    {
        string document = ReadRepositoryFile("docs", "store", "README.md");

        Assert.Contains("不会互相覆盖", document, StringComparison.Ordinal);
        Assert.Contains("独立数据目录", document, StringComparison.Ordinal);
        Assert.Contains("AppPatch", document, StringComparison.Ordinal);
        Assert.Contains("WindowsApps", document, StringComparison.Ordinal);
        Assert.Contains("正式发布不再单独", document, StringComparison.Ordinal);
        Assert.Contains("submission API", document, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleasePipeline_BuildsAndValidatesStoreFromSetupPayload()
    {
        string publishScript = ReadRepositoryFile("tools", "Publish-CleanPackage.ps1");
        string releaseScript = ReadRepositoryFile("tools", "Publish-Releases.ps1");
        string versionCommon = ReadRepositoryFile("tools", "ReleaseVersion.Common.ps1");
        string prereqs = ReadRepositoryFile("tools", "Check-ReleasePrereqs.ps1");
        string releaseWorkflow = ReadRepositoryFile(".github", "workflows", "release-package.yml");

        Assert.Contains("-AppPayloadDir", publishScript, StringComparison.Ordinal);
        Assert.Contains("-RequireStoreIdentity", publishScript, StringComparison.Ordinal);
        Assert.Contains("Publish-StorePackage.ps1", publishScript, StringComparison.Ordinal);
        Assert.Contains("SkipStorePackage", publishScript, StringComparison.Ordinal);
        Assert.Contains("store_package_$releaseTag.json", publishScript, StringComparison.Ordinal);
        Assert.Contains("app_payload_sha256", publishScript, StringComparison.Ordinal);
        Assert.Contains("app_payload_file_count", publishScript, StringComparison.Ordinal);
        Assert.Contains("symbol_sha256", publishScript, StringComparison.Ordinal);
        Assert.Contains("symbol_size", publishScript, StringComparison.Ordinal);
        Assert.Contains("PDB files must be submitted through the appxsym package", publishScript, StringComparison.Ordinal);
        Assert.Contains("Get-AppPayloadFingerprint", versionCommon, StringComparison.Ordinal);
        Assert.Contains("StoreSymbolFileName", versionCommon, StringComparison.Ordinal);
        Assert.Contains("Assert-AppPayloadMatchesArchive", releaseScript, StringComparison.Ordinal);
        Assert.Contains("-ExcludeRelativePrefixes @(\"runtime-seed/\")", releaseScript, StringComparison.Ordinal);
        Assert.Contains("app_payload_sha256", releaseScript, StringComparison.Ordinal);
        Assert.Contains("Resolve-ReleasePayloadAppDir", releaseScript, StringComparison.Ordinal);
        Assert.Contains("package_integrity_$releaseTag.json", publishScript, StringComparison.Ordinal);
        Assert.Contains("Assert-SetupPayloadIntegrity", releaseScript, StringComparison.Ordinal);
        Assert.Contains("package_integrity_", releaseScript, StringComparison.Ordinal);
        int staleManifestRemoval = publishScript.IndexOf(
            "Remove-Item -LiteralPath $storeManifestPath -Force",
            StringComparison.Ordinal);
        int appPublishDir = publishScript.IndexOf(
            "$appPublishDir = Join-Path $outputFullPath \"app\"",
            StringComparison.Ordinal);
        int firstAppPublish = publishScript.IndexOf(
            "Invoke-DotNetPublish -Arguments @(",
            staleManifestRemoval,
            StringComparison.Ordinal);
        int skipStoreBranch = publishScript.IndexOf(
            "if ($SkipStorePackage)",
            StringComparison.Ordinal);
        Assert.True(
            staleManifestRemoval >= 0
            && appPublishDir > staleManifestRemoval
            && firstAppPublish > staleManifestRemoval
            && skipStoreBranch > staleManifestRemoval,
            "构建 Setup 前必须先删除同版本旧 Store 清单");
        Assert.Contains("正式发布必须包含 Microsoft Store 包", releaseScript, StringComparison.Ordinal);
        Assert.Contains("Microsoft Store MSIX (submit via Partner Center", publishScript, StringComparison.Ordinal);
        Assert.Contains("Store 符号包中没有 PDB 文件，拒绝发布", releaseScript, StringComparison.Ordinal);
        Assert.Contains("*.appxsym", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("store-package.local.json", prereqs, StringComparison.Ordinal);
        Assert.Contains("makeappx.exe", prereqs, StringComparison.Ordinal);
        Assert.Contains("makepri.exe", prereqs, StringComparison.Ordinal);
        Assert.Contains("signtool.exe", prereqs, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] relativeParts)
    {
        string path = Path.Combine(new[] { FindRepositoryRoot() }.Concat(relativeParts).ToArray());
        return File.ReadAllText(path, Encoding.UTF8);
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

        throw new DirectoryNotFoundException("ExpressPackingMonitoring repository root was not found.");
    }
}
