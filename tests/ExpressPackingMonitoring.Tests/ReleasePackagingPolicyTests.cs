using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ReleasePackagingPolicyTests
{
    [Fact]
    public void StoreSymbolCollection_HasUpdateCoreReleaseSymbols()
    {
        string repositoryRoot = FindRepositoryRoot();
        string project = File.ReadAllText(
            Path.Combine(
                repositoryRoot,
                "src",
                "ExpressPackingMonitoring.UpdateCore",
                "ExpressPackingMonitoring.UpdateCore.csproj"),
            Encoding.UTF8);

        Assert.Contains(
            "<PropertyGroup Condition=\"'$(Configuration)' == 'Release'\">",
            project,
            StringComparison.Ordinal);
        Assert.Contains("<DebugType>portable</DebugType>", project, StringComparison.Ordinal);
        Assert.Contains("<DebugSymbols>true</DebugSymbols>", project, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreTestCertificate_IsTrustedAsRootForLocalInstall()
    {
        string script = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "tools", "Publish-StorePackage.ps1"),
            Encoding.UTF8);

        Assert.Contains("ca=1&pathlength=1", script, StringComparison.Ordinal);
        Assert.Contains("ca=0", script, StringComparison.Ordinal);
        Assert.Contains("-Signer $rootCertificate", script, StringComparison.Ordinal);
        Assert.Contains("-KeyUsage CertSign, CRLSign", script, StringComparison.Ordinal);
        Assert.Contains("-KeyUsageProperty Sign", script, StringComparison.Ordinal);
        Assert.Contains("sign /fd SHA256 /a /f", script, StringComparison.Ordinal);
        Assert.Contains("WindowsPrincipal", script, StringComparison.Ordinal);
        Assert.Contains("WindowsBuiltInRole]::Administrator", script, StringComparison.Ordinal);
        Assert.Contains("Cert:\\LocalMachine\\Root", script, StringComparison.Ordinal);
        Assert.Contains("Cert:\\LocalMachine\\TrustedPeople", script, StringComparison.Ordinal);
    }

    [Fact]
    public void StorePackage_RejectsMixedAssemblyVersions()
    {
        string repositoryRoot = FindRepositoryRoot();
        string directoryProps = File.ReadAllText(
            Path.Combine(repositoryRoot, "Directory.Build.props"),
            Encoding.UTF8);
        string script = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-StorePackage.ps1"),
            Encoding.UTF8);

        Assert.Contains(
            "<Version Condition=\"'$(Version)' == ''\">",
            directoryProps,
            StringComparison.Ordinal);
        Assert.Contains("$env:Version = $Version", script, StringComparison.Ordinal);
        Assert.Contains("Store payload 程序集版本不一致", script, StringComparison.Ordinal);
        Assert.Contains(
            "Get-ChildItem -LiteralPath $payloadDir -File -Filter \"ExpressPackingMonitoring*.dll\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Copy-Item -LiteralPath $brand.FeaturesPath -Destination $capabilityManifestPath",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalResourcesFollowBrandCapabilities()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string prepareScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Prepare-Release.ps1"),
            Encoding.UTF8);
        string policyScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "BrandResourcePolicy.Common.ps1"),
            Encoding.UTF8);

        Assert.Contains("[string]$VisionObb = \"auto\"", publishScript, StringComparison.Ordinal);
        Assert.Contains("[string]$VisionObb = \"auto\"", prepareScript, StringComparison.Ordinal);
        Assert.Contains("Get-BrandResourcePolicy", publishScript, StringComparison.Ordinal);
        Assert.Contains("Resolve-BrandOptionalResource", policyScript, StringComparison.Ordinal);
        Assert.Contains("软件配置未启用 $ResourceName", policyScript, StringComparison.Ordinal);
        Assert.Contains("if ($includeTtsCache)", publishScript, StringComparison.Ordinal);
        Assert.Contains("if ($resourcePolicy.DefaultScripts)", publishScript, StringComparison.Ordinal);
        Assert.Contains("ProductCapabilities.json", publishScript, StringComparison.Ordinal);
        Assert.Contains(
            "TTS capability is disabled but tts_cache is present",
            publishScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OfficialLinks_UseCurrentGithubRepository()
    {
        string repositoryRoot = FindRepositoryRoot();
        string brandProfile = File.ReadAllText(
            Path.Combine(repositoryRoot, "branding", "profiles", "packingproof", "Brand.props"),
            Encoding.UTF8);
        string installer = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Installer", "ExpressPackingMonitoring.iss"),
            Encoding.UTF8);
        string backupPolicy = File.ReadAllText(
            Path.Combine(repositoryRoot, "src", "ExpressPackingMonitoring", "Services", "BackupCompatibilityPolicy.cs"),
            Encoding.UTF8);
        string settings = File.ReadAllText(
            Path.Combine(repositoryRoot, "src", "ExpressPackingMonitoring", "UI", "SettingsWindow.xaml.cs"),
            Encoding.UTF8);

        Assert.Contains("https://github.com/PackingProof/PackingProof-Desktop", brandProfile, StringComparison.Ordinal);
        Assert.Contains("{#MyBrandWebsiteUrl}", installer, StringComparison.Ordinal);
        Assert.Contains("BrandMetadata.UpdatesUrl", backupPolicy, StringComparison.Ordinal);
        Assert.Contains("BrandMetadata.WebsiteUrl", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("m-RNA/ExpressPackingMonitoring", brandProfile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Packaging_WarnsButDoesNotBlockWhenManualChecksAreUnconfirmed()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string incrementalScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "打包脚本-增量.bat"),
            Encoding.UTF8);
        string baselineScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "打包脚本-基线.bat"),
            Encoding.UTF8);

        Assert.Contains("Packaging will continue", publishScript);
        Assert.DoesNotContain("throw \"Manual core business", publishScript);
        Assert.DoesNotContain("choice /C YN", incrementalScript);
        Assert.DoesNotContain("-ConfirmManualCoreChecks", incrementalScript);
        Assert.DoesNotContain("choice /C YN", baselineScript);
        Assert.DoesNotContain("-ConfirmManualCoreChecks", baselineScript);
    }

    [Fact]
    public void Packaging_DoubleClickSkipsFullTestsAndVersionedRunsKeepThem()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string incrementalScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "打包脚本-增量.bat"),
            Encoding.UTF8);
        string baselineScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "打包脚本-基线.bat"),
            Encoding.UTF8);

        Assert.Contains("[switch]$SkipFullTest", publishScript, StringComparison.Ordinal);
        Assert.Contains("if ($SkipFullTest)", publishScript, StringComparison.Ordinal);
        Assert.Contains("skipping full regression tests", publishScript, StringComparison.Ordinal);
        Assert.Contains("if \"%~1\"==\"\"", incrementalScript, StringComparison.Ordinal);
        Assert.Contains("set \"TEST_ARGS=-SkipFullTest\"", incrementalScript, StringComparison.Ordinal);
        Assert.Contains("%TEST_ARGS%", incrementalScript, StringComparison.Ordinal);
        Assert.Contains("if \"%~1\"==\"\"", baselineScript, StringComparison.Ordinal);
        Assert.Contains("set \"TEST_ARGS=-SkipFullTest\"", baselineScript, StringComparison.Ordinal);
        Assert.Contains("%TEST_ARGS%", baselineScript, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleasePreparation_RunsValidationOnceAfterTagAndPackagesWithoutRepeat()
    {
        string repositoryRoot = FindRepositoryRoot();
        string prepareScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Prepare-Release.ps1"),
            Encoding.UTF8);
        string workflow = File.ReadAllText(
            Path.Combine(repositoryRoot, ".github", "workflows", "release-package.yml"),
            Encoding.UTF8);

        int tagCheckIndex = prepareScript.IndexOf(
            "当前 HEAD 不是 $releaseTag 指向的提交",
            StringComparison.Ordinal);
        int validationIndex = prepareScript.IndexOf(
            "Test-Release-Automated.ps1",
            StringComparison.Ordinal);
        int packageIndex = prepareScript.IndexOf(
            "Publish-CleanPackage.ps1",
            StringComparison.Ordinal);
        int skipTestIndex = prepareScript.IndexOf(
            "\"-SkipFullTest\"",
            packageIndex,
            StringComparison.Ordinal);

        Assert.True(tagCheckIndex >= 0, "发布准备必须先校验版本标签");
        Assert.True(validationIndex > tagCheckIndex, "标签校验必须早于发布自动验收");
        Assert.True(packageIndex > validationIndex, "发布自动验收必须早于出包");
        Assert.True(skipTestIndex > packageIndex, "出包必须显式跳过重复全量测试");
        Assert.Contains("tools\\Prepare-Release.ps1", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("tools\\Test-Release-Automated.ps1", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Packaging_BundlesVcRuntimeAndDropsDebugOnlyRuntimeFiles()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);

        Assert.Contains("$vcRuntimeFileNames = @(", publishScript, StringComparison.Ordinal);
        Assert.Contains("\"msvcp140.dll\"", publishScript, StringComparison.Ordinal);
        Assert.Contains("\"msvcp140_1.dll\"", publishScript, StringComparison.Ordinal);
        Assert.Contains("\"vcruntime140.dll\"", publishScript, StringComparison.Ordinal);
        Assert.Contains("\"vcruntime140_1.dll\"", publishScript, StringComparison.Ordinal);
        Assert.Contains("Copy-VcRuntimeFiles -AppDir $appPublishDir", publishScript, StringComparison.Ordinal);
        Assert.Contains("Remove-DebugOnlyRuntimeFiles -AppDir $appPublishDir", publishScript, StringComparison.Ordinal);
        Assert.Contains("\"mscordaccore*.dll\"", publishScript, StringComparison.Ordinal);
        Assert.Contains("\"mscordbi.dll\"", publishScript, StringComparison.Ordinal);
        Assert.Contains("\"Microsoft.DiaSymReader.Native.*.dll\"", publishScript, StringComparison.Ordinal);
        Assert.Contains("\"msquic.dll\"", publishScript, StringComparison.Ordinal);
        Assert.Contains("VC++ 运行库没有打进包", publishScript, StringComparison.Ordinal);
    }

    [Fact]
    public void Packaging_RequiresDestructiveOutputsToBeStrictRepositoryDescendants()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);

        Assert.Contains("function Test-IsStrictDescendantPath", publishScript, StringComparison.Ordinal);
        Assert.Contains(
            "[string]::Equals($fullPath, $fullRoot",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "$fullRoot + [System.IO.Path]::DirectorySeparatorChar",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Test-IsStrictDescendantPath -Path $outputFullPath -Root $repoFullPath",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Test-IsStrictDescendantPath -Path $zipFullPath -Root $repoFullPath",
            publishScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$outputFullPath.StartsWith($repoFullPath",
            publishScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$zipFullPath.StartsWith($repoFullPath",
            publishScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Packaging_EmbedsSafeManualInstallersInPatchPackages()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string appInstallerScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Apply-AppPatch.ps1"),
            Encoding.UTF8);
        string installerCmd = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Install-AppPatch.cmd"),
            Encoding.UTF8);
        string launcherBaselineScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-LauncherBaseline.ps1"),
            Encoding.UTF8);

        Assert.DoesNotContain("New-ManualUpdatePackage", publishScript);
        Assert.Contains("双击更新主程序.cmd", publishScript);
        Assert.Contains("apply_app_patch.ps1", publishScript);
        Assert.Contains("主程序更新说明.txt", publishScript);
        Assert.Contains("双击更新启动器.cmd", launcherBaselineScript);
        Assert.Contains("apply_launcher_patch.ps1", launcherBaselineScript);
        Assert.Contains("launcher_patch_manifest.json", launcherBaselineScript);
        Assert.Contains("启动器更新说明.txt", launcherBaselineScript);
        Assert.Contains("Get-FileSha256", appInstallerScript);
        Assert.Contains("System.Security.Cryptography.SHA256", appInstallerScript);
        Assert.Contains("AppRootDirectory", appInstallerScript);
        Assert.Contains("正在恢复原文件", appInstallerScript);
        Assert.Contains("apply_app_patch.ps1", installerCmd);
        Assert.Contains("powershell.exe", installerCmd);
        Assert.Contains("Copy-NormalizedCommandFile", publishScript);
        Assert.Contains("Copy-NormalizedCommandFile", launcherBaselineScript);
        Assert.DoesNotContain("taskkill", installerCmd, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Packaging_ReusesLockedLauncherBaselineAndKeepsBridgeSafetyGate()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string baselineScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-LauncherBaseline.ps1"),
            Encoding.UTF8);
        string commonScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "LauncherBaseline.Common.ps1"),
            Encoding.UTF8);

        Assert.Contains("Read-LauncherBaselineManifest", publishScript);
        Assert.Contains("Resolve-LauncherBaselineExecutable", publishScript);
        Assert.Contains("Launcher logical inputs changed", publishScript);
        Assert.Contains("Test-LauncherBaselineSourceUnchanged", publishScript);
        Assert.DoesNotContain("$launcherProject", publishScript);
        Assert.Contains("\"--artifacts-path\", $appBuildArtifacts", publishScript);
        Assert.Contains("\"-o\", $appPublishDir", publishScript);
        Assert.DoesNotContain("BaseIntermediateOutputPath=$appBaseIntermediate", publishScript);
        Assert.Contains("dotnet publish $launcherProject", baselineScript);
        Assert.Contains("Get-LauncherBaselineTag", baselineScript);
        Assert.Contains("tag = $baselineTag", baselineScript);
        Assert.Contains("src\\ExpressPackingMonitoring\\", commonScript);
        Assert.Contains("update_check_url=", commonScript);
        Assert.Contains("Replace(\"`r`n\", \"`n\")", commonScript);
        Assert.Contains("Assert-LauncherPackage", commonScript);
        Assert.Contains("$updateManifest[\"launcher_package\"]", publishScript);
        Assert.Contains("$launcherPackageInfo[\"github_url\"]", publishScript);
        Assert.Contains("$launcherPackageInfo[\"gitee_url\"]", publishScript);
        Assert.Contains("LAUNCHER_PACKAGE_GITHUB_URL_TEMPLATE", publishScript);
        Assert.Contains("LAUNCHER_PACKAGE_GITEE_URL_TEMPLATE", publishScript);
        Assert.Contains("$launcherPackageHash", publishScript);
        Assert.Contains("$launcherExecutableHash", publishScript);
        Assert.Contains("protocol_version", publishScript);
        Assert.Contains(
            "AppPatch bridge validation failed: launcher changed but updated app assembly is missing",
            publishScript);
        Assert.Contains("A new launcher baseline requires a compatible AppPatch bridge", publishScript);
        Assert.Contains(
            "if ($launcherPublishedWithRelease -and -not $patchSupported -and -not $DisablePatch)",
            publishScript);
        Assert.Contains("[switch]$ReuseExistingLauncherBaseline", publishScript);
        Assert.Contains("ReuseExistingLauncherBaseline requires an existing app release tag", publishScript);
        Assert.Contains("本版本不要重复上传 LauncherPatch", publishScript);
        Assert.DoesNotContain("Compress-PackageWithRetry -SourceDir $launcherPackageWorkDir", publishScript);
    }

    [Fact]
    public void Packaging_UsesPinnedVerifiedFfmpegDependency()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string commonScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "FFmpegBaseline.Common.ps1"),
            Encoding.UTF8);
        string manifest = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "ffmpeg-baseline.json"),
            Encoding.UTF8);
        using JsonDocument manifestDocument = JsonDocument.Parse(manifest);
        string packageEntry = manifestDocument.RootElement
            .GetProperty("package")
            .GetProperty("entry")
            .GetString() ?? "";
        string buildScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "third_party", "ffmpeg", "build.sh"),
            Encoding.UTF8);
        string capabilityScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "ffmpeg-capability-check.ps1"),
            Encoding.UTF8);
        string releaseWorkflow = File.ReadAllText(
            Path.Combine(repositoryRoot, ".github", "workflows", "release-package.yml"),
            Encoding.UTF8);
        string ciWorkflow = File.ReadAllText(
            Path.Combine(repositoryRoot, ".github", "workflows", "ci.yml"),
            Encoding.UTF8);
        string prepareScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Prepare-PinnedFFmpeg.ps1"),
            Encoding.UTF8);
        string directoryBuildProps = File.ReadAllText(
            Path.Combine(repositoryRoot, "Directory.Build.props"),
            Encoding.UTF8);
        string testsProject = File.ReadAllText(
            Path.Combine(repositoryRoot, "tests", "ExpressPackingMonitoring.Tests", "ExpressPackingMonitoring.Tests.csproj"),
            Encoding.UTF8);
        XDocument project = XDocument.Load(
            Path.Combine(repositoryRoot, "src", "ExpressPackingMonitoring", "ExpressPackingMonitoring.csproj"));
        XElement[] ffmpegContent = project.Descendants("Content")
            .Where(item => string.Equals(
                item.Element("TargetPath")?.Value,
                @"tools\ffmpeg.exe",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Contains("Read-FFmpegBaselineManifest", publishScript);
        Assert.Contains("Resolve-FFmpegBaselineExecutable", publishScript);
        Assert.Contains("tools\\ffmpeg.exe", publishScript);
        Assert.Contains("-RepositoryRoot $repoRoot", publishScript);
        // 精简构建只用于减小体积；第三方开发者没有本地产物时应允许哈希校验后的上游包回退。
        Assert.DoesNotContain("-RequireLocalBuild", publishScript);
        Assert.Contains("Assert-FFmpegPackage", commonScript);
        Assert.Contains("Assert-FFmpegExecutable", commonScript);
        Assert.Contains("unsafe path", commonScript);
        Assert.Contains("trying next source", commonScript);
        // 基线已切到自建精简构建（third_party/ffmpeg），产物由本仓库构建脚本产出后钉入缓存
        Assert.Contains("\"local_build\"", manifest);
        Assert.Contains("third_party/ffmpeg/build.sh", manifest);
        Assert.Contains("package/ffmpeg-build/ffmpeg.exe", manifest);
        Assert.Contains("\"4.4.1-minimal\"", manifest);
        // MSYS2 x265.pc 会重新带入 -lgcc_s；必须覆盖该 pkg-config，且验收要在干净 PATH 下启动
        Assert.Contains("LOCAL_PKGCONFIG", buildScript);
        Assert.Contains("Libs.private: -lstdc++", buildScript);
        Assert.Contains("Invoke-FFmpegWithCleanPath", capabilityScript);
        Assert.Contains("$startInfo.Environment[\"PATH\"]", capabilityScript);
        Assert.Contains("[switch]$RequireLocalBuild", prepareScript);
        Assert.DoesNotContain("FFMPEG_MINIMAL_URL", releaseWorkflow);
        Assert.Contains("Prepare-PinnedFFmpeg.ps1", releaseWorkflow);
        Assert.Equal(2, Regex.Matches(ciWorkflow, "Prepare-PinnedFFmpeg\\.ps1").Count);
        Assert.Contains("<FFmpegBaselineVersion>4.4.1-minimal</FFmpegBaselineVersion>", directoryBuildProps);
        Assert.Contains("<FFmpegBaselineExecutable>$(MSBuildThisFileDirectory)package\\dependency-cache\\ffmpeg\\$(FFmpegBaselineVersion)\\ffmpeg.exe</FFmpegBaselineExecutable>", directoryBuildProps);
        Assert.Contains("<Content Include=\"$(FFmpegBaselineExecutable)\"", testsProject);
        Assert.Contains("<TargetPath>tools\\ffmpeg.exe</TargetPath>", testsProject);
        Assert.Contains("<CopyToOutputDirectory>Always</CopyToOutputDirectory>", testsProject);
        Assert.Contains("<Content Include=\"..\\..\\ffmpeg.exe\"", testsProject);
        Assert.Contains("GyanD/codexffmpeg/releases/download/4.4.1", manifest);
        Assert.Equal(@"ffmpeg-4.4.1-essentials_build\bin\ffmpeg.exe", packageEntry);
        Assert.DoesNotContain(packageEntry, char.IsControl);
        // 老用户机器上长期保留的历史随包版本（AppPatch 不携带 FFmpeg）必须继续被登记为兼容
        Assert.Contains("8436760af8f81c95eff92d854a7684e6d3cedb872888420359fc45c8eb2664ac", manifest);
        Assert.Equal(2, ffmpegContent.Length);
        Assert.All(ffmpegContent, item =>
        {
            Assert.Equal("Always", item.Element("CopyToOutputDirectory")?.Value);
            Assert.Equal("Always", item.Element("CopyToPublishDirectory")?.Value);
        });
    }

    [Fact]
    public void Packaging_UsesPinnedMinimalOpenCvForFreshInstallsAndStore()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string commonScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "OpenCvBaseline.Common.ps1"),
            Encoding.UTF8);
        string builderScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Build-MinimalOpenCv.ps1"),
            Encoding.UTF8);
        string manifest = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "opencv-baseline.json"),
            Encoding.UTF8);
        using JsonDocument manifestDocument = JsonDocument.Parse(manifest);

        Assert.Contains("Read-OpenCvBaselineManifest", publishScript, StringComparison.Ordinal);
        Assert.Contains("Resolve-OpenCvBaselineNativeDll", publishScript, StringComparison.Ordinal);
        Assert.Contains(
            "-ExcludeRelativePaths ($openCvAppPatchExcludeRelativePaths + $visionAppPatchExcludeRelativePaths)",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains("OpenCvSharp.dll changed while OpenCvSharpExtern.dll is excluded", publishScript, StringComparison.Ordinal);
        Assert.Contains("AppPatch delete list must not contain OpenCV native DLL", publishScript, StringComparison.Ordinal);
        Assert.Contains("OpenCV native DLL must not be included", publishScript, StringComparison.Ordinal);
        Assert.Contains("[switch]$AllowExperimentalOpenCvStore", publishScript, StringComparison.Ordinal);
        Assert.Contains("当前 OpenCV 精简库尚未完成现场验证", publishScript, StringComparison.Ordinal);
        Assert.Contains("Assert-OpenCvNativeDll", commonScript, StringComparison.Ordinal);
        Assert.Contains("opencv_win_x64-$($baseline.opencvsharp.files_tag)", builderScript, StringComparison.Ordinal);
        Assert.Contains("/p:VCToolsVersion=", builderScript, StringComparison.Ordinal);
        Assert.Contains("/p:WindowsTargetPlatformVersion=", builderScript, StringComparison.Ordinal);
        Assert.Contains("NO_VIDEOIO", builderScript, StringComparison.Ordinal);
        Assert.Contains("HAVE_OPENCV_CORE", builderScript, StringComparison.Ordinal);
        Assert.Contains("HAVE_OPENCV_IMGPROC", builderScript, StringComparison.Ordinal);
        Assert.DoesNotContain("HAVE_OPENCV_IMGCODECS", builderScript, StringComparison.Ordinal);
        Assert.Equal(
            "core",
            manifestDocument.RootElement.GetProperty("modules")[0].GetString());
        Assert.Equal(
            "imgproc",
            manifestDocument.RootElement.GetProperty("modules")[1].GetString());
        Assert.True(manifestDocument.RootElement.GetProperty("app_patch_excluded").GetBoolean());
        Assert.True(manifestDocument.RootElement.GetProperty("store_channel_allowed").GetBoolean());
        Assert.Equal(
            "62e1cfc1c1fa043d65f203be28aeeda40b61e001589c418752d68fbc34c23223",
            manifestDocument.RootElement.GetProperty("native").GetProperty("sha256").GetString());
    }

    [Fact]
    public void ReleaseValidation_RequiresHardwareAwareEncoderRoundTrips()
    {
        string repositoryRoot = FindRepositoryRoot();
        string releaseScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Test-Release.ps1"),
            Encoding.UTF8);
        string encoderScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Test-EncodingCodecs.ps1"),
            Encoding.UTF8);
        string solution = File.ReadAllText(
            Path.Combine(repositoryRoot, "ExpressPackingMonitoring.sln"),
            Encoding.UTF8);

        Assert.Contains("Test-EncodingCodecs.ps1", releaseScript, StringComparison.Ordinal);
        Assert.Contains("Get-CimInstance Win32_VideoController", encoderScript, StringComparison.Ordinal);
        Assert.Contains("libx264", encoderScript, StringComparison.Ordinal);
        Assert.Contains("libx265", encoderScript, StringComparison.Ordinal);
        Assert.Contains("h264_nvenc", encoderScript, StringComparison.Ordinal);
        Assert.Contains("hevc_nvenc", encoderScript, StringComparison.Ordinal);
        Assert.Contains("h264_amf", encoderScript, StringComparison.Ordinal);
        Assert.Contains("hevc_amf", encoderScript, StringComparison.Ordinal);
        Assert.Contains("h264_qsv", encoderScript, StringComparison.Ordinal);
        Assert.Contains("hevc_qsv", encoderScript, StringComparison.Ordinal);
        Assert.Contains("EPM_REQUIRED_ENCODERS", encoderScript, StringComparison.Ordinal);
        Assert.Contains("ExpressPackingMonitoring.EncodingIntegrationTests", solution, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseValidation_HasPerTestHangGuard()
    {
        string repositoryRoot = FindRepositoryRoot();
        string releaseScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Test-Release.ps1"),
            Encoding.UTF8);

        Assert.Contains("\"--blame-hang-timeout\"", releaseScript, StringComparison.Ordinal);
        Assert.Contains("\"5m\"", releaseScript, StringComparison.Ordinal);
        Assert.Contains("\"--blame-hang-dump-type\"", releaseScript, StringComparison.Ordinal);
        Assert.Contains("\"none\"", releaseScript, StringComparison.Ordinal);
    }

    [Fact]
    public void Packaging_FollowsVisionCapabilityAndCanExcludeIt()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string appProject = File.ReadAllText(
            Path.Combine(repositoryRoot, "src", "ExpressPackingMonitoring", "ExpressPackingMonitoring.csproj"),
            Encoding.UTF8);
        string incrementalScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "打包脚本-增量.bat"),
            Encoding.UTF8);
        string baselineScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "打包脚本-基线.bat"),
            Encoding.UTF8);

        Assert.Contains(
            "[ValidateSet(\"auto\", \"include\", \"exclude\")]",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "[string]$VisionObb = \"auto\"",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains("-p:IncludeVisionObb=false", publishScript, StringComparison.Ordinal);
        Assert.Contains(
            "Resolve-BrandOptionalResource",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            ") + $visionPublishArguments + @(",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains("$visionAppPatchExcludeRelativePaths", publishScript, StringComparison.Ordinal);
        Assert.Contains(
            "Condition=\"'$(IncludeVisionObb)' == 'true'\"",
            appProject,
            StringComparison.Ordinal);
        Assert.Contains(
            "<IncludeVisionObb Condition=\"'$(IncludeVisionObb)' == ''\">true</IncludeVisionObb>",
            appProject,
            StringComparison.Ordinal);
        Assert.Contains("VISION_OBB", appProject, StringComparison.Ordinal);
        Assert.Contains("Vision.Obb.csproj", appProject, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PackageReference Include=\"Microsoft.ML.OnnxRuntime.DirectML\"",
            appProject,
            StringComparison.Ordinal);
        Assert.Contains("novision", incrementalScript, StringComparison.Ordinal);
        Assert.Contains("novision", baselineScript, StringComparison.Ordinal);
    }

    [Fact]
    public void Packaging_DisablesAppHostCetCompatibilityBeforeCreatingAllDistributionPayloads()
    {
        string publishScript = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);

        int policyIndex = publishScript.IndexOf(
            "PortableExecutableCetPolicy]::TryDisableCetCompatibility",
            StringComparison.Ordinal);
        int runtimeSeedIndex = publishScript.IndexOf(
            "生成共享运行时布局",
            StringComparison.Ordinal);
        Assert.True(policyIndex >= 0);
        Assert.True(runtimeSeedIndex > policyIndex);
        Assert.Contains("Setup, AppPatch and Store MSIX", publishScript, StringComparison.Ordinal);
        Assert.Contains("$publishedAppExe", publishScript, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyMigrationPackaging_UsesTheV100BridgeAndFollowsManagedRuntimePolicy()
    {
        string repositoryRoot = FindRepositoryRoot();
        string migrationScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "New-LegacyMigrationPackage.ps1"),
            Encoding.UTF8);
        string reachabilityScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Test-UpdateReachability.ps1"),
            Encoding.UTF8);
        string runtimeDocument = File.ReadAllText(
            Path.Combine(repositoryRoot, "docs", "development", "RELEASE_AND_RUNTIME.md"),
            Encoding.UTF8);

        Assert.Contains("PortableExecutableCetPolicy", migrationScript, StringComparison.Ordinal);
        Assert.Contains("-ExcludeCompatibleRuntimes", migrationScript, StringComparison.Ordinal);
        Assert.Contains("$update.download_source = \"gitee\"", migrationScript, StringComparison.Ordinal);
        Assert.Contains("$update.full_download_page = $FullDownloadPage.Trim()", migrationScript, StringComparison.Ordinal);
        Assert.Contains("vision-model-baseline.json", migrationScript, StringComparison.Ordinal);
        Assert.Contains("$baselineModelMatchesTarget", migrationScript, StringComparison.Ordinal);
        Assert.Contains("Migration package repeats an unchanged visual model", migrationScript, StringComparison.Ordinal);
        Assert.Contains("$DeclaredPatchUrl", reachabilityScript, StringComparison.Ordinal);
        Assert.Contains("0.0.81 历史迁移桥（0.0.100）", runtimeDocument, StringComparison.Ordinal);
        Assert.Contains("waybill-yolo11n-obb-v11", runtimeDocument, StringComparison.Ordinal);
        Assert.Contains("### 多段升级与迁移桥接", runtimeDocument, StringComparison.Ordinal);
        Assert.Contains("空包、无 `files` 的补丁", runtimeDocument, StringComparison.Ordinal);
        Assert.Contains("patch_variants", runtimeDocument, StringComparison.Ordinal);
    }

    [Fact]
    public void Tests_IncludeVisionObbByDefaultAndCanExcludeIt()
    {
        string repositoryRoot = FindRepositoryRoot();
        string testProject = File.ReadAllText(
            Path.Combine(
                repositoryRoot,
                "tests",
                "ExpressPackingMonitoring.Tests",
                "ExpressPackingMonitoring.Tests.csproj"),
            Encoding.UTF8);
        string testCi = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Test-CI.ps1"),
            Encoding.UTF8);
        string releaseTest = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Test-Release.ps1"),
            Encoding.UTF8);
        string ciWorkflow = File.ReadAllText(
            Path.Combine(repositoryRoot, ".github", "workflows", "ci.yml"),
            Encoding.UTF8);

        Assert.Contains(
            "<IncludeVisionObb Condition=\"'$(IncludeVisionObb)' == ''\">true</IncludeVisionObb>",
            testProject,
            StringComparison.Ordinal);
        Assert.Contains(
            "Condition=\"'$(IncludeVisionObb)' == 'true'\"",
            testProject,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Compile Remove=\"CameraVisionObbTests.cs\" />",
            testProject,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Compile Remove=\"VisionObbDetectorContractTests.cs\" />",
            testProject,
            StringComparison.Ordinal);
        Assert.Contains(
            "[ValidateSet(\"auto\", \"include\", \"exclude\")]",
            testCi,
            StringComparison.Ordinal);
        Assert.Contains(
            "[string]$VisionObb = \"auto\"",
            testCi,
            StringComparison.Ordinal);
        Assert.Contains(
            "[ValidateSet(\"auto\", \"include\", \"exclude\")]",
            releaseTest,
            StringComparison.Ordinal);
        Assert.Contains(
            "[string]$VisionObb = \"auto\"",
            releaseTest,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"-p:IncludeVisionObb=$($includeVisionObb.ToString().ToLowerInvariant())\"",
            testCi,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"-p:IncludeVisionObb=$($includeVisionObb.ToString().ToLowerInvariant())\"",
            releaseTest,
            StringComparison.Ordinal);
        Assert.Contains(
            "build-test-vision:",
            ciWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "-p:IncludeVisionObb=true",
            ciWorkflow,
            StringComparison.Ordinal);
        string releaseWorkflow = File.ReadAllText(
            Path.Combine(repositoryRoot, ".github", "workflows", "release-package.yml"),
            Encoding.UTF8);
        Assert.Contains("vision_obb:", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains("default: true", releaseWorkflow, StringComparison.Ordinal);
        Assert.Contains(
            "if: env.VISION_OBB != 'exclude'",
            releaseWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"-VisionObb\", \"$env:VISION_OBB\"",
            releaseWorkflow,
            StringComparison.Ordinal);
        Assert.Contains("dotnet-version: 10.0.404", ciWorkflow, StringComparison.Ordinal);
        Assert.Contains("dotnet-version: 10.0.404", releaseWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    public void NoRuntimePackage_InheritsVisionObbFeatureState()
    {
        string repositoryRoot = FindRepositoryRoot();
        string script = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-NoRuntimePackage.ps1"),
            Encoding.UTF8);

        Assert.Contains(
            "\"-p:IncludeVisionObb=$($includeVisionObb.ToString().ToLowerInvariant())\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains("-p:CameraVisionModelPath=$visionModel", script, StringComparison.Ordinal);
        Assert.Contains("no-runtime 包未继承视觉文件", script, StringComparison.Ordinal);
        Assert.Contains(
            "Copy-Item -LiteralPath $sourceFile -Destination $publishedFile -Force",
            script,
            StringComparison.Ordinal);
        Assert.Contains("no-runtime 视觉文件与标准包不一致", script, StringComparison.Ordinal);
        Assert.Contains(
            "Assert-VisionModelRuntimeFiles `",
            script,
            StringComparison.Ordinal);
        Assert.Contains("no-runtime 包出现", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseValidation_RequiredCoreTestsExist()
    {
        string repositoryRoot = FindRepositoryRoot();
        string releaseScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Test-Release.ps1"),
            Encoding.UTF8);
        Match requiredTestsBlock = Regex.Match(
            releaseScript,
            @"\$requiredCoreTests\s*=\s*@\((?<body>.*?)\r?\n\)",
            RegexOptions.Singleline);

        Assert.True(requiredTestsBlock.Success, "Could not find the required release test list.");

        string[] requiredTests = Regex.Matches(requiredTestsBlock.Groups["body"].Value, "\"(?<name>[^\"]+)\"")
            .Select(match => match.Groups["name"].Value)
            .ToArray();
        HashSet<string> availableTests = typeof(ReleasePackagingPolicyTests).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods()
                .Select(method => $"{type.Name}.{method.Name}"))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(requiredTests);
        Assert.All(requiredTests, requiredTest => Assert.Contains(requiredTest, availableTests));
    }

    [Fact]
    public void Packaging_OnlyBuildsSlimPatchForCompatibleRuntimeBaseline()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string compatibilityScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "AppPatchRuntimeCompatibility.Common.ps1"),
            Encoding.UTF8);
        string manifest = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "ffmpeg-baseline.json"),
            Encoding.UTF8);

        Assert.Contains("Test-AppPatchRuntimeCompatibility", publishScript);
        Assert.Contains("-ExcludeCompatibleRuntimes", publishScript);
        Assert.Contains("Test-IsAppPatchManagedRuntimePath", publishScript);
        Assert.Contains("Test-ZipContainsEntryPrefix", publishScript);
        Assert.Contains("patch_supported", publishScript);
        Assert.Contains("APP_PATCH_GITHUB_URL_TEMPLATE", publishScript);
        Assert.Contains("APP_PATCH_GITEE_URL_TEMPLATE", publishScript);
        Assert.Contains("$patchPackageInfo[\"github_url\"]", publishScript);
        Assert.Contains("$patchPackageInfo[\"gitee_url\"]", publishScript);
        Assert.Contains("full_download_fallback_page", publishScript);
        Assert.Contains("FULL_DOWNLOAD_PRIMARY_PAGE_URL_TEMPLATE", publishScript);
        Assert.Contains("FULL_DOWNLOAD_FALLBACK_PAGE_URL_TEMPLATE", publishScript);
        Assert.Contains("-Key \"FULL_DOWNLOAD_PAGE\" -DefaultValue \"\"", publishScript);
        Assert.Contains("Full download fallback page:", publishScript);
        Assert.Contains("$updateManifest[\"patch_package\"] = $null", publishScript);
        Assert.Contains("Remove-Item -LiteralPath $appPatchZipPath -Force", publishScript);
        Assert.Contains("$patchReason", publishScript);
        Assert.Contains("app_patch_compatible_executables", manifest);
        Assert.Contains("b1383f5d07470d503edecdaee4bddc5891e986e916a698299b357f79cfe445fd", manifest);
        Assert.Contains("AppPatch 基线中的 FFmpeg 版本或哈希不在兼容白名单中", compatibilityScript);
        Assert.Contains("AppPatch 基线缺少 LibVLC 必需文件", compatibilityScript);
    }

    [Fact]
    public void Packaging_IgnoresLauncherComponentTagsWhenResolvingAppVersion()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);

        Assert.Contains("^v\\d+\\.\\d+\\.\\d+", publishScript);
        Assert.Contains("git -C $repoRoot describe --tags --match \"v[0-9]*\"", publishScript);
        Assert.DoesNotContain("git -C $repoRoot describe --tags --always", publishScript);
    }

    [Fact]
    public void WindowsInstaller_UsesFixedPerUserIdentityAndSafeReleaseInputs()
    {
        string repositoryRoot = FindRepositoryRoot();
        string innoScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Installer", "ExpressPackingMonitoring.iss"),
            Encoding.UTF8);
        string chineseMessages = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Installer", "Languages", "ChineseSimplified.isl"),
            Encoding.UTF8);
        string buildScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Build-Installer.ps1"),
            Encoding.UTF8);
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string brandProfile = File.ReadAllText(
            Path.Combine(repositoryRoot, "branding", "profiles", "packingproof", "Brand.props"),
            Encoding.UTF8);

        Assert.Contains("99E9FCE3-C8FE-4D7A-9FA4-BC9CB9186B05", brandProfile);
        Assert.Contains(@"DefaultDirName={localappdata}\Programs\{#MyBrandInstallDirectoryName}", innoScript);
        Assert.Contains("<BrandInstallDirectoryName>ExpressPackingMonitoring</BrandInstallDirectoryName>", brandProfile);
        Assert.Contains("DisableDirPage=no", innoScript);
        Assert.Contains("IsProtectedInstallRoot", innoScript);
        Assert.Contains("IsDirectoryWritable", innoScript);
        Assert.Contains("(Length(Dir) = 3) and (Dir[2] = ':') and (Dir[3] = '\\')", innoScript);
        Assert.Contains("ForceDirectories(Dir)", innoScript);
        Assert.Contains("DirRequiresAdmin", innoScript);
        Assert.DoesNotContain("需要管理员权限", innoScript);
        Assert.DoesNotContain("administrator rights", innoScript);
        Assert.Contains("Program Files、Windows、ProgramData 或磁盘根目录", innoScript);
        Assert.Contains("PrivilegesRequired=lowest", innoScript);
        Assert.Contains("ArchitecturesAllowed=x64compatible", innoScript);
        Assert.Contains("CloseApplications=no", innoScript);
        Assert.Contains("AppMutex=Local\\{#MyBrandInstanceNamePrefix}.Portable.Mutex", innoScript);
        Assert.DoesNotContain("CloseApplicationsFilter=", innoScript);
        Assert.Contains(@"MessagesFile: ""Languages\ChineseSimplified.isl""", innoScript);
        Assert.Contains("LanguageName=简体中文", chineseMessages);
        Assert.Contains("ButtonNext=下一步", chineseMessages);
        Assert.Contains(@"Filename: ""{app}\{#MyAppExeName}""; WorkingDir: ""{app}""", innoScript);
        Assert.Equal(2, innoScript.Split(
            "AppUserModelID: \"{#MyAppUserModelId}\"",
            StringSplitOptions.None).Length - 1);
        Assert.Contains("<BrandAppUserModelId>PackingProof.ExpressPackingMonitoring</BrandAppUserModelId>", brandProfile);
        Assert.Contains("--uninstall-plan-recordings", innoScript);
        Assert.Contains("--uninstall-delete-recordings", innoScript);
        Assert.Contains("--uninstall-delete-local-data", innoScript);
        Assert.Contains("删除设置和临时文件", innoScript);
        Assert.Contains("不会删除录像、录像记录和恢复备份", innoScript);
        Assert.Contains("删除录像和录像记录", innoScript);
        Assert.Contains("SettingsCheckBox.Checked := False", innoScript);
        Assert.Contains("RecordingsCheckBox.Checked := False", innoScript);
        Assert.Contains("HeadingLabel.Height := ScaleY(26)", innoScript);
        Assert.Contains("/SILENT /EPMUNINSTALLOPTIONS", innoScript);
        Assert.DoesNotContain("MB_DEFBUTTON2", innoScript);
        Assert.DoesNotContain("是否删除本机应用数据", innoScript);
        Assert.DoesNotContain("是否同时删除数据库登记的录像原文件", innoScript);
        Assert.DoesNotContain("DelTree(UserDataPath", innoScript);
        Assert.DoesNotContain("if WizardSilent", innoScript);
        Assert.Contains("if (not WizardSilent) then", innoScript);
        Assert.Contains("if not UpgradeDirPromptShown then", innoScript);
        Assert.Contains("(PreviousDir <> '') or DirHasInstalledApp(SelectedDir)", innoScript);
        Assert.DoesNotContain("你选择了新的安装文件夹", innoScript);
        Assert.Contains("function IsBrandAppRunning: Boolean", innoScript);
        Assert.Contains("Local\\{#MyBrandInstanceNamePrefix}.Portable.Mutex", innoScript);
        Assert.Contains("Local\\ExpressPackingMonitoring.Mutex", innoScript);
        Assert.Equal(4, innoScript.Split("if IsBrandAppRunning then", StringSplitOptions.None).Length - 1);
        int prepareToInstall = innoScript.IndexOf(
            "function PrepareToInstall(var NeedsRestart: Boolean): String",
            StringComparison.Ordinal);
        int unconditionalRunningCheck = innoScript.IndexOf(
            "if IsBrandAppRunning then",
            prepareToInstall,
            StringComparison.Ordinal);
        int removeOldVersionExit = innoScript.IndexOf(
            "if not RemoveOldVersion then",
            prepareToInstall,
            StringComparison.Ordinal);
        Assert.True(
            prepareToInstall >= 0
            && unconditionalRunningCheck > prepareToInstall
            && unconditionalRunningCheck < removeOldVersionExit,
            "PrepareToInstall 必须在直接覆盖分支前检查运行中的程序");
        Assert.Contains("InstallDir[Length(InstallDir)] = '\\'", innoScript);
        Assert.Contains("function PrepareToInstall(var NeedsRestart: Boolean): String", innoScript);
        Assert.Contains("function RemoveOldInstall", innoScript);
        Assert.Contains("DirHasInstalledApp", innoScript);
        Assert.Contains("RemoveRuntimeLeftovers", innoScript);
        Assert.Contains("app\\winrt-disabled", innoScript);
        Assert.Contains("CreatedDir", innoScript);
        Assert.Contains("RemoveDir(Dir)", innoScript);
        Assert.Contains("RemoveRuntimeLeftovers(ExpandConstant('{app}'))", innoScript);
        Assert.Contains("RemoveDir(ExpandConstant('{app}'))", innoScript);
        Assert.Contains("OldUninstaller := AddBackslash(Dir) + 'unins000.exe'", innoScript);
        Assert.Contains("'/SILENT'", innoScript);
        Assert.Contains("SW_SHOWNORMAL", innoScript);
        Assert.Contains("是否先删除旧版本的程序文件和启动器", innoScript);
        Assert.Contains("你的设置、数据库和录像都会保留", innoScript);
        Assert.Contains("旧版本位置", innoScript);
        Assert.Contains("AppRunningBeforeRemove", innoScript);

        Assert.Contains("INNO_SETUP_ISCC", buildScript);
        Assert.Contains("InstallerCompression = \"lzma2/ultra64\"", buildScript);
        Assert.Contains("InstallerCompression = \"lzma2/ultra64\"", publishScript);
        Assert.Contains("winget install --id JRSoftware.InnoSetup", buildScript);
        Assert.Contains("WINDOWS_SIGN_CERT_THUMBPRINT", buildScript);
        Assert.Contains("Get-AuthenticodeSignature", buildScript);
        Assert.Contains("\"$($brand.ArtifactPrefix)_Setup_v$normalizedVersion.exe\"", buildScript);
        Assert.Contains("[string]$OutputFileName", buildScript);
        Assert.Contains("Installer output file name must be a leaf .exe name", buildScript);
        Assert.Contains("config.json", buildScript);
        Assert.Contains("videos.db", buildScript);

        Assert.Contains("OutputBaseFilename={#MyBrandArtifactPrefix}_Setup_v{#MyAppVersion}", innoScript);
        Assert.Contains("\"$($brand.ArtifactPrefix)_Setup_v$artifactVersion.exe\"", publishScript);
        Assert.Contains("\"$($brand.ArtifactPrefix)_AppPatch_$artifactTag.zip\"", publishScript);
        Assert.Contains("update_$artifactTag.json", publishScript);
        Assert.Contains("launcher_manifest_$artifactTag.json", publishScript);
        Assert.Contains("package_integrity_$artifactTag.json", publishScript);
        Assert.Contains("release_info_$artifactTag.txt", publishScript);
        Assert.Contains("-OutputFileName $setupFileName", publishScript);
        Assert.Contains("\"$($brand.ArtifactPrefix)+v$artifactVersion\"", publishScript);
        Assert.Contains("git -C $repoRoot describe --tags --match \"v[0-9]*\" --long --abbrev=8 HEAD", publishScript);
        Assert.Contains("return \"-$($Matches['count'])-g$($Matches['commit'].ToLowerInvariant())$dirtySuffix\"", publishScript);
        Assert.Contains("$isReleaseTagAtHead", publishScript);
        Assert.Contains("if ($isReleaseTagAtHead)", publishScript);
        Assert.DoesNotContain("commit.$CommitCount.$shortCommitId", publishScript);
        Assert.Contains("-p:InformationalVersion=$informationalVersion", publishScript);
        Assert.Contains("-p:IncludeSourceRevisionInInformationalVersion=false", publishScript);
        Assert.Contains("-p:GitCommitId=$gitCommitId", publishScript);
        Assert.Contains("Build-Installer.ps1", publishScript);
        Assert.Contains("SmartScreen", publishScript);
        Assert.Contains("GitHub 默认上传", publishScript);
        Assert.Contains("Gitee 命令行上传", publishScript);
        Assert.Contains("$giteeAssetIndex", publishScript);
        Assert.Contains("完整 Setup 严格小于 100MB，已直接上传 Gitee", publishScript);
        Assert.Contains(
            "完整 Setup 达到 100MB，Gitee 使用商店桥接安装器或 Full download page",
            publishScript);
        Assert.Contains("[switch]$IncludeFullZip", publishScript);
        Assert.Contains("if ($IncludeFullZip)", publishScript);
        // 完整 7z 与完整 ZIP 都不再上传 Release，默认也不生成，只能显式开启。
        Assert.Contains("[switch]$IncludeSevenZip", publishScript);
        Assert.Contains("if ($IncludeSevenZip)", publishScript);
        Assert.Contains("完整 7z 与完整 ZIP 默认不生成、不上传", publishScript);
        Assert.Contains("SEVEN_ZIP_EXE", publishScript);
        Assert.Contains("winget install --id 7zip.7zip", publishScript);
        Assert.Contains("-t7z", publishScript);
        Assert.Contains("SevenZipCompressionLevel = 5", publishScript);
        Assert.Contains("ZipCompressionLevel = \"Optimal\"", publishScript);
        Assert.Contains("if ($CompressionLevel -ge 9)", publishScript);
        Assert.Contains("\"-mx=$CompressionLevel\"", publishScript);
        Assert.Contains("\"-md=128m\"", publishScript);
        Assert.Contains("\"-mfb=273\"", publishScript);
        Assert.Contains("-CompressionLevel $ZipCompressionLevel", publishScript);
        Assert.Contains("-InstallerCompression $InstallerCompression", publishScript);
        Assert.Contains("-m0=lzma2", publishScript);
        Assert.Contains("-ms=on", publishScript);
    }

    /// <summary>
    /// 发布脚本必须用与打包脚本同一套归一化推断产物名。
    /// 之前它直接拿 tag 拼名字：tag 少写 v（0.0.67）或带后缀（v0.0.67-rc1）时，
    /// 实际产物名仍然是 v0.0.67，脚本会在"找不到产物目录"或"缺少必须上传的产物"处失败。
    /// </summary>
    [Fact]
    public void ReleasePublisher_DerivesArtifactNamesFromNormalizedVersion()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publisher = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-Releases.ps1"),
            Encoding.UTF8);
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string versionCommon = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "ReleaseVersion.Common.ps1"),
            Encoding.UTF8);

        // 命名规则集中在 ReleaseVersion.Common.ps1，两边都必须复用它。
        Assert.Contains("ReleaseVersion.Common.ps1", publisher, StringComparison.Ordinal);
        Assert.Contains("ReleaseVersion.Common.ps1", publishScript, StringComparison.Ordinal);
        Assert.Contains("function Get-NormalizedReleaseVersion", versionCommon, StringComparison.Ordinal);
        Assert.Contains("function Get-ReleaseArtifactNames", versionCommon, StringComparison.Ordinal);
        Assert.Contains("$brand.ArtifactPrefix + \"+$releaseTag\"", versionCommon, StringComparison.Ordinal);
        Assert.Contains("\"$($brand.ArtifactPrefix)_Setup_v$normalizedVersion.exe\"", versionCommon, StringComparison.Ordinal);
        Assert.Contains("\"update_v$normalizedVersion.json\"", versionCommon, StringComparison.Ordinal);
        Assert.Contains("\"$($brand.ArtifactPrefix)_AppPatch_$releaseTag.zip\"", versionCommon, StringComparison.Ordinal);
        Assert.Contains("\"$($brand.ArtifactPrefix)_LauncherPatch_$releaseTag.zip\"", versionCommon, StringComparison.Ordinal);
        Assert.Contains("\"store_package_$releaseTag.json\"", versionCommon, StringComparison.Ordinal);
        Assert.Contains("\"$($brand.ArtifactPrefix)_Store_v$normalizedVersion.msix\"", versionCommon, StringComparison.Ordinal);
        Assert.Contains("\"$($brand.ArtifactPrefix)_Store_v$normalizedVersion.appxsym\"", versionCommon, StringComparison.Ordinal);
        Assert.Contains("\"$($brand.ArtifactPrefix)_Store_v$normalizedVersion.msixupload\"", versionCommon, StringComparison.Ordinal);
        Assert.Contains("$brand.ArtifactPrefix + \"_Store_v$normalizedVersion\"", versionCommon, StringComparison.Ordinal);

        // 发布脚本按归一化结果挑文件，不再自己用 tag 或 TrimStart 拼名字。
        Assert.Contains(
            "Get-ReleaseArtifactNames -Tag $tag -RepoRoot $repoRoot",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains("$packageRoot = $artifactNames.PackageRoot", publisher, StringComparison.Ordinal);
        Assert.Contains(
            "$setupPath = Join-Path $packageRoot $artifactNames.SetupFileName",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains(
            "$updateJsonPath = Join-Path $packageRoot $artifactNames.UpdateJsonFileName",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains(
            "$appPatchPath = Join-Path $packageRoot $artifactNames.AppPatchFileName",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains(
            "$launcherPatchPath = Join-Path $packageRoot $artifactNames.LauncherPatchFileName",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains(
            "$storePackagePath = Join-Path $artifactNames.StorePackageRoot $artifactNames.StorePackageFileName",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains(
            "$storeSymbolPath = Join-Path $artifactNames.StorePackageRoot $artifactNames.StoreSymbolFileName",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains(
            "$storeUploadPath = Join-Path $artifactNames.StorePackageRoot $artifactNames.StoreUploadFileName",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains("store_package_$artifactTag.json", publishScript, StringComparison.Ordinal);
        Assert.DoesNotContain("$tag.TrimStart('v')", publisher, StringComparison.Ordinal);
        Assert.DoesNotContain("\"package\\PackingProof+$tag\"", publisher, StringComparison.Ordinal);

        // 标题用归一化版本号，tag 只用于创建 Release 本体；没写标题就拒绝发布，
        // 避免发出去的版本名只有版本号、看不出本版改了什么。
        Assert.Contains(
            "$releaseTitle = \"$releaseTag $Title\"",
            publisher,
            StringComparison.Ordinal);
        Assert.Contains("必须用 -Title", publisher, StringComparison.Ordinal);
        Assert.Contains("& gh release view $tag --repo $repoSlug", publisher, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseTooling_UsesCurrentRepositoryLayout()
    {
        string repositoryRoot = FindRepositoryRoot();
        string testCi = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Test-CI.ps1"),
            Encoding.UTF8);
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string launcherCommon = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "LauncherBaseline.Common.ps1"),
            Encoding.UTF8);

        Assert.Contains("tests\\ExpressPackingMonitoring.Tests\\ExpressPackingMonitoring.Tests.csproj", testCi, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet test ExpressPackingMonitoring.Tests\\ExpressPackingMonitoring.Tests.csproj", testCi, StringComparison.Ordinal);
        Assert.Contains(@"$appSourceRoot = Join-Path $repoRoot ""src\ExpressPackingMonitoring""", publishScript, StringComparison.Ordinal);
        Assert.Contains("foreach ($candidateTagPath in @($tagPath, $headPath)", launcherCommon, StringComparison.Ordinal);
    }

    [Fact]
    public void Packaging_PinsAppRuntimeToKeepAppPatchSmall()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string runtimeCommon = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "SharedRuntimeLayout.Common.ps1"),
            Encoding.UTF8);

        Assert.Contains(
            "[string]$AppRuntimeFrameworkVersion = \"10.0.13\"",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"-p:PackingProofAppRuntimeFrameworkVersion=$AppRuntimeFrameworkVersion\"",
            publishScript,
            StringComparison.Ordinal);
        string appProject = File.ReadAllText(
            Path.Combine(repositoryRoot, "src", "ExpressPackingMonitoring", "ExpressPackingMonitoring.csproj"),
            Encoding.UTF8);
        Assert.Contains(
            "<RuntimeFrameworkVersion Condition=\"'$(PackingProofAppRuntimeFrameworkVersion)' != ''\">$(PackingProofAppRuntimeFrameworkVersion)</RuntimeFrameworkVersion>",
            appProject,
            StringComparison.Ordinal);
        Assert.Contains(
            "优先使用应用实际声明的精确补丁版本",
            runtimeCommon,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Packaging_EnforcesModernPatchBaselineAndDocsAgree()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string runtimeDocument = File.ReadAllText(
            Path.Combine(repositoryRoot, "docs", "development", "RELEASE_AND_RUNTIME.md"),
            Encoding.UTF8);
        string checklist = File.ReadAllText(
            Path.Combine(repositoryRoot, "docs", "development", "RELEASE_CHECKLIST.md"),
            Encoding.UTF8);
        string incrementalPackageScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "打包脚本-增量.bat"),
            Encoding.UTF8);
        string prepareReleaseScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Prepare-Release.ps1"),
            Encoding.UTF8);
        string agentRules = File.ReadAllText(
            Path.Combine(repositoryRoot, "AGENTS.md"),
            Encoding.UTF8);

        Assert.Contains("0.0.101 及以后正式版的补丁基线不得低于 0.0.100", publishScript, StringComparison.Ordinal);
        Assert.Contains("[string]$PatchBaselineVersion = \"0.0.100\"", publishScript, StringComparison.Ordinal);
        Assert.Contains("[string]$PatchBaselineVersion = \"0.0.100\"", prepareReleaseScript, StringComparison.Ordinal);
        Assert.Contains("set \"BASELINE_VERSION=0.0.100\"", incrementalPackageScript, StringComparison.Ordinal);
        Assert.Contains("从 0.0.101 起固定为 `0.0.100`", runtimeDocument, StringComparison.Ordinal);
        Assert.Contains("0.0.101 及以后统一使用 `-PatchBaselineVersion 0.0.100`", checklist, StringComparison.Ordinal);
        Assert.Contains("默认补丁基线必须同步为 `0.0.100`", runtimeDocument, StringComparison.Ordinal);
        Assert.Contains("默认补丁基线必须同步为 `0.0.100`", checklist, StringComparison.Ordinal);
        Assert.Contains("补丁基线版本是发布链路的一项同步配置", agentRules, StringComparison.Ordinal);
        Assert.Contains("0.0.85 从未正式发布", runtimeDocument, StringComparison.Ordinal);
        Assert.Contains("0.0.84、0.0.100 的 Release", runtimeDocument, StringComparison.Ordinal);
        Assert.Contains("0.0.100 的 Release", runtimeDocument, StringComparison.Ordinal);
        Assert.Contains("`patch_supported: false`", runtimeDocument, StringComparison.Ordinal);
        Assert.Contains("`patch_supported: false`", checklist, StringComparison.Ordinal);
        Assert.DoesNotContain("从下一版（0.0.84）起改成", checklist, StringComparison.Ordinal);
        Assert.DoesNotContain("-PatchBaselineVersion 0.0.18 -BaselineAppDir", runtimeDocument, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseBotPusher_DefaultsToBothPlatformsAndRequiresConfirmation()
    {
        string repositoryRoot = FindRepositoryRoot();
        string pusher = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Push-ReleaseBot.ps1"),
            Encoding.UTF8);
        string auth = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "ReleaseAuth.Common.ps1"),
            Encoding.UTF8);
        string releaseDocument = File.ReadAllText(
            Path.Combine(repositoryRoot, "docs", "development", "RELEASE_AND_RUNTIME.md"),
            Encoding.UTF8);
        string agentGuide = File.ReadAllText(
            Path.Combine(repositoryRoot, "AGENTS.md"),
            Encoding.UTF8);
        string releaseTests = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Test-Release.ps1"),
            Encoding.UTF8);

        Assert.Contains("$value = \"both\"", pusher, StringComparison.Ordinal);
        Assert.Contains("-Confirmed", pusher, StringComparison.Ordinal);
        Assert.Contains("远端直推属于发布写操作", pusher, StringComparison.Ordinal);
        Assert.Contains("merge-base --is-ancestor", pusher, StringComparison.Ordinal);
        Assert.Contains("RELEASE_PUSH_TARGET", pusher, StringComparison.Ordinal);
        Assert.Contains("Enable-ReleaseBotGitTransport", pusher, StringComparison.Ordinal);
        Assert.Contains("(?:v|launcher-v)\\d+\\.\\d+\\.\\d+", pusher, StringComparison.Ordinal);
        Assert.Contains("[string]$GitHubRemote = \"Github\"", pusher, StringComparison.Ordinal);
        Assert.Contains("[string]$GiteeRemote = \"Gitee\"", pusher, StringComparison.Ordinal);
        Assert.DoesNotContain("return if", pusher, StringComparison.Ordinal);
        Assert.Contains("Import-GitHubReleaseAppInstallationToken", auth, StringComparison.Ordinal);
        Assert.Contains("Import-GiteeReleaseBotToken", auth, StringComparison.Ordinal);
        Assert.Contains("url.https://x-access-token:", auth, StringComparison.Ordinal);
        Assert.Contains("url.https://$giteeUser", auth, StringComparison.Ordinal);
        Assert.Contains(
            "$_ -notin @(\"github.com\", \"api.github.com\")",
            auth,
            StringComparison.Ordinal);
        Assert.Contains("PACKINGPROOF_RELEASE_BOT = \"1\"", auth, StringComparison.Ordinal);
        Assert.DoesNotContain("--force", pusher, StringComparison.Ordinal);
        Assert.Contains("Push-ReleaseBot.ps1", releaseDocument, StringComparison.Ordinal);
        Assert.Contains("默认发布不再创建 PR", releaseDocument, StringComparison.Ordinal);
        Assert.Contains("强制创建 PR", agentGuide, StringComparison.Ordinal);
        Assert.Contains("远端直推统一使用 `tools\\Push-ReleaseBot.ps1`", agentGuide, StringComparison.Ordinal);
        Assert.Contains("禁止直接运行 `git push`", agentGuide, StringComparison.Ordinal);
        Assert.Contains("发布标签必须打在", agentGuide, StringComparison.Ordinal);
        Assert.Contains("版本标签必须先在本地指向该提交", releaseDocument, StringComparison.Ordinal);
        Assert.Contains("全部通过后才用 `Push-ReleaseBot.ps1` 推送", releaseDocument, StringComparison.Ordinal);
        Assert.Contains("发布标签 $Name 必须指向本地 main 的当前提交", pusher, StringComparison.Ordinal);
        Assert.Contains("[switch]$ReplaceTag", pusher, StringComparison.Ordinal);
        Assert.Contains("git push --porcelain $remote --delete", pusher, StringComparison.Ordinal);
        Assert.Contains("-ReplaceTag -Confirmed", releaseDocument, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "用 `Push-ReleaseBot.ps1` 机器人直推 GitHub/Gitee 的 `main` → 按 `docs/development/RELEASE_CHECKLIST.md` 做变更审计",
            releaseDocument,
            StringComparison.Ordinal);
        Assert.Contains(
            "$env:PACKINGPROOF_SKIP_CAMERA_TESTS = \"1\"",
            releaseTests,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UiChanges_RequireRenderedScreenshotReviewBeforeRelease()
    {
        string repositoryRoot = FindRepositoryRoot();
        string agentGuide = File.ReadAllText(
            Path.Combine(repositoryRoot, "AGENTS.md"),
            Encoding.UTF8);
        string checklist = File.ReadAllText(
            Path.Combine(repositoryRoot, "docs", "development", "RELEASE_CHECKLIST.md"),
            Encoding.UTF8);

        Assert.Contains("任何用户可见 UI 修改都必须生成真实渲染截图", agentGuide, StringComparison.Ordinal);
        Assert.Contains("C# 使用 WPF UI 测试或截图工具", agentGuide, StringComparison.Ordinal);
        Assert.Contains("HTML 使用 `Test-WebUi.ps1` 或 Playwright", agentGuide, StringComparison.Ordinal);
        Assert.Contains("构建、单元测试和 DOM 几何断言不能替代人工视觉确认", agentGuide, StringComparison.Ordinal);
        Assert.Contains("没有截图证据和用户视觉确认不得正式发布", checklist, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateManifestNotes_KeepSingleLineShort()
    {
        string repositoryRoot = FindRepositoryRoot();
        string notesCommon = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "ReleaseNotes.Common.ps1"),
            Encoding.UTF8);
        string template = File.ReadAllText(
            Path.Combine(repositoryRoot, "docs", "development", "RELEASE_NOTES_TEMPLATE.md"),
            Encoding.UTF8);

        // 启动器会折行，条目太长整页就乱，所以摘要必须卡住单条长度。
        Assert.Contains("$maxNoteLength = 40", notesCommon, StringComparison.Ordinal);
        Assert.Contains("单条最多 $maxNoteLength 个字", notesCommon, StringComparison.Ordinal);
        Assert.Contains("硬上限 40 字", template, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateManifest_DefaultsDownloadSourceToGithub()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string runtimeDocument = File.ReadAllText(
            Path.Combine(repositoryRoot, "docs", "development", "RELEASE_AND_RUNTIME.md"),
            Encoding.UTF8);

        Assert.Contains(
            "[string]$DownloadSource = \"github\"",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "$updateManifest[\"download_source\"] = $DownloadSource",
            publishScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "`download_source` 控制增量包和启动器包默认从哪个平台下载：`github`（出包缺省",
            runtimeDocument,
            StringComparison.Ordinal);
        Assert.Contains(
            "-DownloadSource github|gitee|auto",
            runtimeDocument,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseAuthentication_UsesBrandScopedDedicatedTokens()
    {
        string repositoryRoot = FindRepositoryRoot();
        string releaseAuth = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "ReleaseAuth.Common.ps1"),
            Encoding.UTF8);
        string giteeAuth = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "GiteeAuth.Common.ps1"),
            Encoding.UTF8);
        string prereqs = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Check-ReleasePrereqs.ps1"),
            Encoding.UTF8);
        string publish = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-Releases.ps1"),
            Encoding.UTF8);
        string releaseDocument = File.ReadAllText(
            Path.Combine(repositoryRoot, "docs", "development", "RELEASE_AND_RUNTIME.md"),
            Encoding.UTF8);
        string agentGuide = File.ReadAllText(
            Path.Combine(repositoryRoot, "AGENTS.md"),
            Encoding.UTF8);
        string noRuntime = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-NoRuntimePackage.ps1"),
            Encoding.UTF8);
        string prePushHook = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "git-hooks", "pre-push"),
            Encoding.UTF8);

        Assert.Contains("\"GITHUB_RELEASE_TOKEN\"", releaseAuth, StringComparison.Ordinal);
        Assert.Contains("GitHubReleaseTokenFile", releaseAuth, StringComparison.Ordinal);
        Assert.Contains("packingproof-release-bot", releaseAuth, StringComparison.Ordinal);
        Assert.Contains("private_key_file", releaseAuth, StringComparison.Ordinal);
        Assert.Contains("access_tokens", releaseAuth, StringComparison.Ordinal);
        Assert.Contains("Test-GitHubReleaseAuthentication", releaseAuth, StringComparison.Ordinal);
        Assert.Contains("gitee-release-bot", giteeAuth, StringComparison.Ordinal);
        Assert.Contains("GiteeReleaseTokenFile", giteeAuth, StringComparison.Ordinal);
        Assert.Contains("GiteeReleaseUsername", releaseAuth, StringComparison.Ordinal);
        Assert.Contains("function Import-GiteeReleaseBotToken", giteeAuth, StringComparison.Ordinal);
        Assert.Contains("\"GITEE_RELEASE_TOKEN\"", giteeAuth, StringComparison.Ordinal);
        Assert.Contains("\"GITEE_TOKEN\"", giteeAuth, StringComparison.Ordinal);
        Assert.Contains("Import-GitHubReleaseAppInstallationToken -Brand $brand", prereqs, StringComparison.Ordinal);
        Assert.DoesNotContain("Import-GitHubReleaseTokenFromEnvFile", prereqs, StringComparison.Ordinal);
        Assert.Contains("Import-GiteeReleaseBotToken -Brand $brand", prereqs, StringComparison.Ordinal);
        Assert.DoesNotContain("gh auth status", prereqs, StringComparison.Ordinal);
        Assert.Contains("Import-GitHubReleaseAppInstallationToken -Brand $brand", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("Import-GitHubReleaseTokenFromEnvFile", publish, StringComparison.Ordinal);
        Assert.Contains("Import-GiteeReleaseBotToken -Brand $brand", publish, StringComparison.Ordinal);
        Assert.Contains("Test-GitHubReleaseAuthentication", publish, StringComparison.Ordinal);
        Assert.Contains("Import-GiteeReleaseBotToken -Brand $brand", noRuntime, StringComparison.Ordinal);
        Assert.Contains("PACKINGPROOF_RELEASE_BOT", prePushHook, StringComparison.Ordinal);
        Assert.Contains("Direct git push is disabled", prePushHook, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users\\", releaseDocument, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users\\", agentGuide, StringComparison.Ordinal);
        Assert.Contains("允许匿名读取", releaseDocument, StringComparison.Ordinal);
        Assert.Contains("BrandMobileAppEnabled=false", releaseDocument, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
