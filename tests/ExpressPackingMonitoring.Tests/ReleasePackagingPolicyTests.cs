using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ReleasePackagingPolicyTests
{
    [Fact]
    public void OfficialLinks_UseCurrentGithubRepository()
    {
        string repositoryRoot = FindRepositoryRoot();
        string[] relativePaths =
        [
            @"tools\Installer\ExpressPackingMonitoring.iss",
            @"src\ExpressPackingMonitoring\Services\BackupCompatibilityPolicy.cs",
            @"src\ExpressPackingMonitoring\UI\SettingsWindow.xaml.cs"
        ];

        foreach (string relativePath in relativePaths)
        {
            string content = File.ReadAllText(
                Path.Combine(repositoryRoot, relativePath),
                Encoding.UTF8);
            Assert.Contains("PackingProof/PackingProof-Desktop", content, StringComparison.Ordinal);
            Assert.DoesNotContain("m-RNA/ExpressPackingMonitoring", content, StringComparison.OrdinalIgnoreCase);
        }
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
        Assert.Contains("launcher-v$normalizedVersion", baselineScript);
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
        string prepareScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Prepare-PinnedFFmpeg.ps1"),
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
    public void Packaging_UsesPinnedMinimalOpenCvOnlyForFreshInstalls()
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
        Assert.Contains("-ExcludeRelativePaths $openCvAppPatchExcludeRelativePaths", publishScript, StringComparison.Ordinal);
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
        Assert.False(manifestDocument.RootElement.GetProperty("store_channel_allowed").GetBoolean());
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

        Assert.Contains("99E9FCE3-C8FE-4D7A-9FA4-BC9CB9186B05", innoScript);
        Assert.Contains(@"DefaultDirName={localappdata}\Programs\ExpressPackingMonitoring", innoScript);
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
        Assert.Contains("CloseApplications=yes", innoScript);
        Assert.DoesNotContain("CloseApplications=force", innoScript);
        Assert.Contains(@"MessagesFile: ""Languages\ChineseSimplified.isl""", innoScript);
        Assert.Contains("LanguageName=简体中文", chineseMessages);
        Assert.Contains("ButtonNext=下一步", chineseMessages);
        Assert.Contains(@"Filename: ""{app}\{#MyAppExeName}""; WorkingDir: ""{app}""", innoScript);
        Assert.Equal(2, innoScript.Split(
            "AppUserModelID: \"{#MyAppUserModelId}\"",
            StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "#define MyAppUserModelId \"PackingProof.ExpressPackingMonitoring\"",
            innoScript);
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
        Assert.Contains("function IsPackingProofRunning: Boolean", innoScript);
        Assert.Contains("Local\\ExpressPackingMonitoring.Portable.Mutex", innoScript);
        Assert.Contains("Local\\ExpressPackingMonitoring.Mutex", innoScript);
        Assert.Equal(4, innoScript.Split("if IsPackingProofRunning then", StringSplitOptions.None).Length - 1);
        int prepareToInstall = innoScript.IndexOf(
            "function PrepareToInstall(var NeedsRestart: Boolean): String",
            StringComparison.Ordinal);
        int unconditionalRunningCheck = innoScript.IndexOf(
            "if IsPackingProofRunning then",
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
        Assert.Contains("PackingProof_Setup_v$normalizedVersion.exe", buildScript);
        Assert.Contains("[string]$OutputFileName", buildScript);
        Assert.Contains("Installer output file name must be a leaf .exe name", buildScript);
        Assert.Contains("config.json", buildScript);
        Assert.Contains("videos.db", buildScript);

        Assert.Contains("OutputBaseFilename=PackingProof_Setup_v{#MyAppVersion}", innoScript);
        Assert.Contains("PackingProof_Setup_v$artifactVersion.exe", publishScript);
        Assert.Contains("PackingProof_AppPatch_$releaseTag.zip", publishScript);
        Assert.Contains("-OutputFileName $setupFileName", publishScript);
        Assert.Contains("\"PackingProof+v$artifactVersion\"", publishScript);
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
            "完整 Setup 达到 100MB，Gitee 使用 no-runtime 安装向导或 Full download page",
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
        Assert.Contains("\"package\\PackingProof+$releaseTag\"", versionCommon, StringComparison.Ordinal);
        Assert.Contains("\"PackingProof_Setup_v$normalizedVersion.exe\"", versionCommon, StringComparison.Ordinal);
        Assert.Contains("\"update_v$normalizedVersion.json\"", versionCommon, StringComparison.Ordinal);
        Assert.Contains("\"PackingProof_AppPatch_$releaseTag.zip\"", versionCommon, StringComparison.Ordinal);
        Assert.Contains("\"PackingProof_LauncherPatch_$releaseTag.zip\"", versionCommon, StringComparison.Ordinal);
        Assert.Contains("\"store_package_$releaseTag.json\"", versionCommon, StringComparison.Ordinal);
        Assert.Contains("\"PackingProof_Store_v$normalizedVersion.msix\"", versionCommon, StringComparison.Ordinal);
        Assert.Contains("StorePackageRoot = Join-Path $RepoRoot \"package\\store\\PackingProof_Store_v$normalizedVersion\"", versionCommon, StringComparison.Ordinal);

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
        Assert.Contains("store_package_$releaseTag.json", publishScript, StringComparison.Ordinal);
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

        Assert.Contains("0.0.86 及以后正式版的补丁基线不得低于 0.0.84", publishScript, StringComparison.Ordinal);
        Assert.Contains("[string]$PatchBaselineVersion = \"0.0.84\"", publishScript, StringComparison.Ordinal);
        Assert.Contains("set \"BASELINE_VERSION=0.0.84\"", incrementalPackageScript, StringComparison.Ordinal);
        Assert.Contains("0.0.84 之后的下一个正式版（本次 0.0.86）起统一使用 `-PatchBaselineVersion 0.0.84`", runtimeDocument, StringComparison.Ordinal);
        Assert.Contains("0.0.84 之后的下一个正式版（0.0.86）起统一使用 `-PatchBaselineVersion 0.0.84`", checklist, StringComparison.Ordinal);
        Assert.Contains("默认补丁基线都必须是 `0.0.84`", runtimeDocument, StringComparison.Ordinal);
        Assert.Contains("默认补丁基线都必须是 `0.0.84`", checklist, StringComparison.Ordinal);
        Assert.Contains("0.0.85 从未正式发布", runtimeDocument, StringComparison.Ordinal);
        Assert.Contains("会沿 baseline 链查找 0.0.84", runtimeDocument, StringComparison.Ordinal);
        Assert.Contains("0.0.84 的 Release", runtimeDocument, StringComparison.Ordinal);
        Assert.Contains("会先通过 0.0.84 的 `update_v0.0.84.json`", checklist, StringComparison.Ordinal);
        Assert.Contains("`patch_supported: false`", runtimeDocument, StringComparison.Ordinal);
        Assert.Contains("`patch_supported: false`", checklist, StringComparison.Ordinal);
        Assert.DoesNotContain("从下一版（0.0.84）起改成", checklist, StringComparison.Ordinal);
        Assert.DoesNotContain("-PatchBaselineVersion 0.0.18 -BaselineAppDir", runtimeDocument, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangePrSubmitter_DefaultsToBothPlatformsAndStaysConfigurable()
    {
        string repositoryRoot = FindRepositoryRoot();
        string submitter = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Submit-ChangePr.ps1"),
            Encoding.UTF8);
        string releaseDocument = File.ReadAllText(
            Path.Combine(repositoryRoot, "docs", "development", "RELEASE_AND_RUNTIME.md"),
            Encoding.UTF8);
        string agentGuide = File.ReadAllText(
            Path.Combine(repositoryRoot, "AGENTS.md"),
            Encoding.UTF8);

        // 改动默认走 PR；只有发布准备阶段仅更新版本号的 chore 可以直接提交 main。
        // 我们自己发现的 bug 先在 GitHub 开 issue，再提 PR 并在说明里关联；
        // 性能、功能、工具、文档这类不是 bug 的改动直接提 PR，不必为了留痕再补 issue。
        // PR 默认 Gitee 和 GitHub 两边都提，合并用快进（提交原样保留）；提交前必须人工确认。
        // 脚本默认值和规范必须同步，避免流程说一套、脚本做另一套。
        Assert.Contains("$defaultTarget = \"both\"", submitter, StringComparison.Ordinal);
        Assert.Contains("-Confirmed", submitter, StringComparison.Ordinal);
        Assert.Contains("提交 PR 前需要人工确认", submitter, StringComparison.Ordinal);
        Assert.Contains("快进", submitter, StringComparison.Ordinal);
        Assert.Contains("PR_TARGET_HOST", submitter, StringComparison.Ordinal);
        Assert.Contains("Submit-ChangePr.ps1", submitter, StringComparison.Ordinal);
        Assert.Contains("PR_TARGET_HOST", releaseDocument, StringComparison.Ordinal);
        Assert.Contains("Submit-ChangePr.ps1", releaseDocument, StringComparison.Ordinal);
        Assert.Contains("不加 `-Confirmed` 只打印计划并拒绝执行", releaseDocument, StringComparison.Ordinal);
        Assert.Contains("提交 PR 必须人工确认", agentGuide, StringComparison.Ordinal);
        Assert.Contains("PR 默认 Gitee 和 GitHub 两边都提", agentGuide, StringComparison.Ordinal);
        Assert.Contains("PR 合并使用快进", agentGuide, StringComparison.Ordinal);
        Assert.Contains("发布准备阶段仅更新 `Directory.Build.props` 版本号", releaseDocument, StringComparison.Ordinal);
        Assert.Contains("不得夹带代码、配置、测试或文档修改", releaseDocument, StringComparison.Ordinal);
        Assert.Contains("发布准备阶段可以单独提交", agentGuide, StringComparison.Ordinal);
        Assert.Contains("先在 GitHub 开 issue", releaseDocument, StringComparison.Ordinal);
        Assert.Contains("先在 GitHub 开 issue", agentGuide, StringComparison.Ordinal);
        Assert.Contains("别人在某平台提的 issue", agentGuide, StringComparison.Ordinal);
        Assert.Contains("不是 bug", agentGuide, StringComparison.Ordinal);
        Assert.Contains("不是 bug", releaseDocument, StringComparison.Ordinal);
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
            "`download_source` 控制增量包默认从哪个平台下载：`github`（出包缺省",
            runtimeDocument,
            StringComparison.Ordinal);
        Assert.Contains(
            "-DownloadSource github|gitee|auto",
            runtimeDocument,
            StringComparison.Ordinal);
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
