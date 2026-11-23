using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExpressPackingMonitoring.UpdateCore;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ManualPatchInstallerTests
{
    [Fact]
    public async Task Installer_UsesConfiguredAppRootAndAppliesVerifiedFiles()
    {
        using var fixture = new ManualPatchFixture();
        fixture.CreatePatch("new-content", useValidHash: true);

        ProcessResult result = await fixture.RunInstallerAsync();

        Assert.True(
            result.ExitCode == 0,
            $"exit={result.ExitCode}{Environment.NewLine}stdout={result.StandardOutput}{Environment.NewLine}stderr={result.StandardError}");
        Assert.Equal("new-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
        Assert.Contains("增量更新完成", result.StandardOutput);
    }

    [Theory]
    [InlineData("AppxManifest.xml")]
    [InlineData("PackingProof.PackageEdition.json")]
    public async Task Installer_RejectsStoreEditionTargetBeforeChangingFiles(string markerFileName)
    {
        using var fixture = new ManualPatchFixture();
        fixture.CreatePatch("new-content", useValidHash: true);
        fixture.MarkAsStoreEdition(markerFileName);

        ProcessResult result = await fixture.RunInstallerAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Microsoft Store", result.StandardOutput + result.StandardError);
        Assert.Equal("old-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
        Assert.DoesNotContain("正在恢复原文件", result.StandardOutput);
    }

    [Fact]
    public async Task Installer_RejectsStoreEditionConfigPath()
    {
        using var fixture = new ManualPatchFixture();
        fixture.CreatePatch("new-content", useValidHash: true);
        string storePackageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            "PackingProof.ManualPatchGuard_" + Guid.NewGuid().ToString("N"));
        string storeConfigPath = Path.Combine(
            storePackageRoot,
            "LocalState",
            "ExpressPackingMonitoring",
            "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(storeConfigPath)!);
        try
        {
            File.WriteAllText(
                storeConfigPath,
                JsonSerializer.Serialize(new { AppRootDirectory = fixture.AppRootPath }),
                Encoding.UTF8);

            ProcessResult result = await fixture.RunInstallerAsync(configPath: storeConfigPath);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("数据目录", result.StandardOutput + result.StandardError);
            Assert.Equal("old-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
        }
        finally
        {
            try { Directory.Delete(storePackageRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Installer_ReplacesReadOnlyInstalledFile()
    {
        using var fixture = new ManualPatchFixture();
        fixture.CreatePatch("new-content", useValidHash: true);
        File.SetAttributes(fixture.TargetFilePath, FileAttributes.ReadOnly);

        ProcessResult result = await fixture.RunInstallerAsync();

        Assert.True(
            result.ExitCode == 0,
            $"exit={result.ExitCode}{Environment.NewLine}stdout={result.StandardOutput}{Environment.NewLine}stderr={result.StandardError}");
        Assert.Equal("new-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
        Assert.False((File.GetAttributes(fixture.TargetFilePath) & FileAttributes.ReadOnly) != 0);
    }

    [Fact]
    public async Task Installer_RejectsHashMismatchWithoutChangingInstalledFile()
    {
        using var fixture = new ManualPatchFixture();
        fixture.CreatePatch("tampered-content", useValidHash: false);

        ProcessResult result = await fixture.RunInstallerAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("old-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
        Assert.Contains("SHA256 校验失败", result.StandardOutput + result.StandardError);
    }

    [Fact]
    public async Task Installer_RejectsPatchForAnotherBrandBeforeChangingFiles()
    {
        using var fixture = new ManualPatchFixture();
        fixture.InstallRealAppDll();
        fixture.CreatePatch(
            "new-content",
            useValidHash: true,
            productName: "Other Brand");

        ProcessResult result = await fixture.RunInstallerAsync(verifyVersion: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("软件配置不匹配", result.StandardOutput + result.StandardError);
        Assert.Equal("old-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
    }

    [Fact]
    public async Task LegacyBrandlessPatchOnlyAppliesToPackingProofTarget()
    {
        using var fixture = new ManualPatchFixture();
        string version = fixture.InstallRealAppDll();
        fixture.CreatePatch(
            "new-content",
            useValidHash: true,
            latestVersion: version,
            baselineVersion: version,
            includeBrand: false);

        ProcessResult result = await fixture.RunInstallerAsync(verifyVersion: true);

        if (BrandMetadata.ProfileId == "packingproof")
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("new-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
        }
        else
        {
            Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("软件配置不匹配", result.StandardOutput + result.StandardError);
            Assert.Equal("old-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
        }
    }

    [Fact]
    public async Task Installer_RollsBackFilesWhenReplacementFailsMidway()
    {
        using var fixture = new ManualPatchFixture();
        fixture.CreatePartiallyFailingPatch();

        ProcessResult result = await fixture.RunInstallerAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("old-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
        Assert.Contains("正在恢复原文件", result.StandardOutput);
    }

    /// <summary>
    /// 同版本增量包必须能装：现场会把「同一版本 + 修复」的增量包直接发给用户双击更新，
    /// 以前用 -ge 判断，安装版本等于补丁版本时会被当成「无需重复更新」直接退出。
    /// </summary>
    [Fact]
    public async Task Installer_AppliesPatchWithSameVersionAsInstalled()
    {
        using var fixture = new ManualPatchFixture();
        string version = fixture.InstallRealAppDll();
        fixture.CreatePatch("new-content", useValidHash: true, latestVersion: version, baselineVersion: version);

        ProcessResult result = await fixture.RunInstallerAsync(verifyVersion: true);

        Assert.True(
            result.ExitCode == 0,
            $"exit={result.ExitCode}{Environment.NewLine}stdout={result.StandardOutput}{Environment.NewLine}stderr={result.StandardError}");
        Assert.Equal("new-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
        Assert.Contains("增量更新完成", result.StandardOutput);
    }

    /// <summary>
    /// 清理列表（deletes）：老版本留下、新版本已不再发布的文件必须在安装时删掉。升级一贯只新增
    /// 或覆盖，没有这一步这些文件（旧版本号写进文件名的原生库、已下线的旧程序集）会一直留在 app\ 里。
    /// </summary>
    [Fact]
    public async Task Installer_RemovesObsoleteFilesListedInDeletes()
    {
        using var fixture = new ManualPatchFixture();
        string obsoletePath = fixture.CreateObsoleteFile(Path.Combine("old", "retired.dll"));
        fixture.CreatePatch(
            "new-content",
            useValidHash: true,
            deletes: [new { path = "old/retired.dll" }]);

        ProcessResult result = await fixture.RunInstallerAsync();

        Assert.True(
            result.ExitCode == 0,
            $"exit={result.ExitCode}{Environment.NewLine}stdout={result.StandardOutput}{Environment.NewLine}stderr={result.StandardError}");
        Assert.Equal("new-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
        Assert.False(File.Exists(obsoletePath), "清理列表里的旧文件必须被删掉");
    }

    /// <summary>
    /// 清理列表不能碰用户可替换的运行时文件：清单来自网络，放行一条就可能删掉用户自己换过的 ffmpeg。
    /// 打包侧已经拒绝生成这种清单，安装侧必须再拦一次。
    /// </summary>
    [Fact]
    public async Task Installer_RejectsDeleteListThatTouchesUserManagedRuntimeFiles()
    {
        using var fixture = new ManualPatchFixture();
        string ffmpegPath = fixture.CreateObsoleteFile(Path.Combine("tools", "ffmpeg.exe"));
        fixture.CreatePatch(
            "new-content",
            useValidHash: true,
            deletes: [new { path = "tools/ffmpeg.exe" }]);

        ProcessResult result = await fixture.RunInstallerAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Patch 载荷不合规", result.StandardOutput + result.StandardError);
        Assert.True(File.Exists(ffmpegPath), "用户自己换过的 ffmpeg 不能被补丁删掉");
        Assert.Equal("old-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
    }

    /// <summary>同一个相对路径不能既在写入列表又在清理列表里：清单自相矛盾时当场拒绝，别装出一半。</summary>
    [Fact]
    public async Task Installer_RejectsDeleteListThatConflictsWithWrittenFiles()
    {
        using var fixture = new ManualPatchFixture();
        fixture.CreatePatch(
            "new-content",
            useValidHash: true,
            deletes: [new { path = "Web/index.html" }]);

        ProcessResult result = await fixture.RunInstallerAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("补丁清理列表与写入列表冲突", result.StandardOutput + result.StandardError);
        Assert.Equal("old-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
    }

    /// <summary>
    /// 清理列表的条目也要能回滚：删到一半失败时，已经被删掉的文件必须从备份里放回来，
    /// 不能留下"包没装成、旧文件也没了"的安装目录。
    /// </summary>
    [Fact]
    public async Task Installer_RollBackRestoresObsoleteFileDeletedBeforeAFailure()
    {
        using var fixture = new ManualPatchFixture();
        string deletedPath = fixture.CreateObsoleteFile(Path.Combine("old", "gone.dll"));
        string lockedPath = fixture.CreateObsoleteFile(Path.Combine("old", "locked.dll"));
        fixture.CreatePatch(
            "new-content",
            useValidHash: true,
            deletes:
            [
                new { path = "old/gone.dll" },
                new { path = "old/locked.dll" },
            ]);

        // 占住第二个待删文件的句柄：它删不掉，前面的删除就得整体回滚。
        using (File.Open(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            ProcessResult result = await fixture.RunInstallerAsync();

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("正在恢复原文件", result.StandardOutput);
            Assert.Equal("old-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
            Assert.True(File.Exists(deletedPath), "已经被删掉的旧文件必须被放回来");
            // 备份目录在失败时保留：里面得有它，才证明这个文件真的走到过"删前先备份"那一步，
            // 而不是因为删到它之前就失败了。
            string backupRoot = Path.Combine(fixture.UserDataRoot, "cache", "manual_updates");
            Assert.NotEmpty(Directory.EnumerateFiles(backupRoot, "gone.dll", SearchOption.AllDirectories));
        }
    }

    /// <summary>比已安装版本更旧的增量包仍然要拦住，避免把程序降级。</summary>
    [Fact]
    public async Task Installer_SkipsPatchOlderThanInstalledVersion()
    {
        using var fixture = new ManualPatchFixture();
        fixture.InstallRealAppDll();
        fixture.CreatePatch("new-content", useValidHash: true, latestVersion: "0.0.1", baselineVersion: "0.0.1");

        ProcessResult result = await fixture.RunInstallerAsync(verifyVersion: true);

        Assert.True(
            result.ExitCode == 0,
            $"exit={result.ExitCode}{Environment.NewLine}stdout={result.StandardOutput}{Environment.NewLine}stderr={result.StandardError}");
        Assert.Equal("old-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
        Assert.Contains("无需更新", result.StandardOutput);
    }

    [Theory]
    [InlineData("missing", "package-root")]
    [InlineData("invalid", "app-directory")]
    [InlineData("legacy", "launcher-exe")]
    [InlineData("stale", "app-exe")]
    [InlineData("missing", "quoted-package-root")]
    [InlineData("missing", "legacy-flat-root")]
    [InlineData("missing", "shortcut-package-root")]
    [InlineData("missing", "shortcut-app-exe")]
    public async Task Installer_AcceptsDraggedInstallLocationsWhenConfigCannotBeUsed(
        string configState,
        string candidateKind)
    {
        using var fixture = new ManualPatchFixture();
        fixture.CreatePatch("new-content", useValidHash: true);
        fixture.SetConfigState(configState);

        ProcessResult result = await fixture.RunInstallerAsync(fixture.GetManualCandidate(candidateKind));

        Assert.True(
            result.ExitCode == 0,
            $"exit={result.ExitCode}{Environment.NewLine}stdout={result.StandardOutput}{Environment.NewLine}stderr={result.StandardError}");
        Assert.Equal("new-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
    }

    [Theory]
    [InlineData("missing-target-shortcut", "路径不存在")]
    [InlineData("internet-shortcut", "不支持网络快捷方式")]
    public async Task Installer_RejectsInvalidShortcutInputs(string candidateKind, string expectedMessage)
    {
        using var fixture = new ManualPatchFixture();
        fixture.CreatePatch("new-content", useValidHash: true);
        fixture.SetConfigState("missing");

        ProcessResult result = await fixture.RunInstallerAsync(fixture.GetManualCandidate(candidateKind));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedMessage, result.StandardOutput + result.StandardError);
        Assert.Equal("old-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
    }

    [Fact]
    public async Task Installer_PromptsForDraggedLocationWhenAutomaticDetectionFails()
    {
        using var fixture = new ManualPatchFixture();
        fixture.CreatePatch("new-content", useValidHash: true);
        fixture.SetConfigState("missing");

        ProcessResult result = await fixture.RunInstallerAsync(
            standardInput: fixture.GetManualCandidate("quoted-package-root"));

        Assert.True(
            result.ExitCode == 0,
            $"exit={result.ExitCode}{Environment.NewLine}stdout={result.StandardOutput}{Environment.NewLine}stderr={result.StandardError}");
        Assert.Contains("请把安装文件夹或 ExpressPackingMonitoring.exe 拖到此窗口", result.StandardOutput);
        Assert.Equal("new-content", File.ReadAllText(fixture.TargetFilePath, Encoding.UTF8));
    }

    private sealed class ManualPatchFixture : IDisposable
    {
        private readonly string _root;
        private readonly string _patchRoot;
        private readonly string _configPath;
        private readonly string _scriptPath;
        private readonly string _installedRoot;
        private readonly string _appRoot;
        private readonly string _mutexName;

        public string TargetFilePath { get; private set; }

        public string AppRootPath => _appRoot;

        /// <summary>假装的用户数据目录（配置、日志与手动更新的备份都在它下面）。</summary>
        public string UserDataRoot => Path.GetDirectoryName(_configPath)!;

        public ManualPatchFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "epm-manual-patch-tests", Guid.NewGuid().ToString("N"));
            _patchRoot = Path.Combine(_root, "patch");
            _installedRoot = Path.Combine(_root, "installed package");
            _appRoot = Path.Combine(_installedRoot, "app");
            _mutexName = @"Local\EPM_ManualPatch_Test_" + Guid.NewGuid().ToString("N");
            string userDataRoot = Path.Combine(_root, "user-data");
            _configPath = Path.Combine(userDataRoot, "config.json");
            TargetFilePath = Path.Combine(_appRoot, "Web", "index.html");
            _scriptPath = Path.Combine(FindRepositoryRoot(), "tools", "Apply-AppPatch.ps1");

            Directory.CreateDirectory(Path.GetDirectoryName(TargetFilePath)!);
            Directory.CreateDirectory(userDataRoot);
            File.WriteAllText(Path.Combine(_installedRoot, "ExpressPackingMonitoring.exe"), "launcher", Encoding.UTF8);
            File.WriteAllText(Path.Combine(_appRoot, "ExpressPackingMonitoring.exe"), "test", Encoding.UTF8);
            File.WriteAllText(Path.Combine(_appRoot, "ExpressPackingMonitoring.dll"), "test", Encoding.UTF8);
            File.WriteAllText(TargetFilePath, "old-content", Encoding.UTF8);
            File.WriteAllText(
                _configPath,
                JsonSerializer.Serialize(new { AppRootDirectory = _appRoot }),
                Encoding.UTF8);
        }

        public void MarkAsStoreEdition(string markerFileName)
        {
            File.WriteAllText(
                Path.Combine(_appRoot, markerFileName),
                markerFileName == "AppxManifest.xml" ? "<Package />" : "{\"edition\":\"store\"}",
                Encoding.UTF8);
        }

        /// <summary>
        /// 把真实主程序 DLL 放进安装目录，返回它被解析出的版本号。
        /// 版本判断用例需要真实 FileVersion，普通用例用假 DLL 并跳过版本检查。
        /// </summary>
        public string InstallRealAppDll()
        {
            string source = Path.Combine(AppContext.BaseDirectory, "ExpressPackingMonitoring.dll");
            string target = Path.Combine(_appRoot, "ExpressPackingMonitoring.dll");
            File.Copy(source, target, overwrite: true);
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(target);
            foreach (string? candidate in new[] { info.ProductVersion, info.FileVersion })
            {
                Match match = Regex.Match(candidate ?? "", @"\d+\.\d+\.\d+(?:\.\d+)?");
                if (match.Success)
                    return match.Value;
            }
            throw new InvalidOperationException("测试用主程序 DLL 没有可解析的版本号");
        }

        public void CreatePatch(
            string content,
            bool useValidHash,
            string latestVersion = "0.0.2",
            string baselineVersion = "0.0.1",
            object[]? deletes = null,
            string? productName = null,
            bool includeBrand = true)
        {
            string sourcePath = Path.Combine(_patchRoot, "files", "Web", "index.html");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(sourcePath, content, Encoding.UTF8);
            byte[] bytes = File.ReadAllBytes(sourcePath);
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!useValidHash)
                hash = new string('0', 64);

            WriteManifest(
            [
                new
                {
                    path = "Web/index.html",
                    sha256 = hash,
                    size = bytes.LongLength
                }
            ],
            latestVersion,
            baselineVersion,
            deletes,
            productName ?? BrandMetadata.ProductName,
            includeBrand);
        }

        /// <summary>在安装目录的 app\ 下造一个"老版本遗留"的文件，返回它的完整路径。</summary>
        public string CreateObsoleteFile(string relativePath)
        {
            string path = Path.Combine(_appRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "obsolete-content", Encoding.UTF8);
            return path;
        }

        public void CreatePartiallyFailingPatch()
        {
            string firstSource = Path.Combine(_patchRoot, "files", "Web", "index.html");
            string blockedSource = Path.Combine(_patchRoot, "files", "blocked");
            Directory.CreateDirectory(Path.GetDirectoryName(firstSource)!);
            File.WriteAllText(firstSource, "new-content", Encoding.UTF8);
            File.WriteAllText(blockedSource, "blocked-content", Encoding.UTF8);
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(TargetFilePath)!)!, "blocked"));

            byte[] firstBytes = File.ReadAllBytes(firstSource);
            byte[] blockedBytes = File.ReadAllBytes(blockedSource);
            WriteManifest(
            [
                new
                {
                    path = "Web/index.html",
                    sha256 = Convert.ToHexString(SHA256.HashData(firstBytes)).ToLowerInvariant(),
                    size = firstBytes.LongLength
                },
                new
                {
                    path = "blocked",
                    sha256 = Convert.ToHexString(SHA256.HashData(blockedBytes)).ToLowerInvariant(),
                    size = blockedBytes.LongLength
                }
            ]);
        }

        public void SetConfigState(string state)
        {
            switch (state)
            {
                case "missing":
                    File.Delete(_configPath);
                    break;
                case "invalid":
                    File.WriteAllText(_configPath, "{invalid-json", Encoding.UTF8);
                    break;
                case "legacy":
                    File.WriteAllText(_configPath, "{}", Encoding.UTF8);
                    break;
                case "stale":
                    File.WriteAllText(
                        _configPath,
                        JsonSerializer.Serialize(new { AppRootDirectory = Path.Combine(_root, "moved-app") }),
                        Encoding.UTF8);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(state), state, null);
            }
        }

        public string GetManualCandidate(string kind)
        {
            return kind switch
            {
                "package-root" => _installedRoot,
                "app-directory" => _appRoot,
                "launcher-exe" => Path.Combine(_installedRoot, "ExpressPackingMonitoring.exe"),
                "app-exe" => Path.Combine(_appRoot, "ExpressPackingMonitoring.exe"),
                "quoted-package-root" => $"\"{_installedRoot}\"",
                "legacy-flat-root" => PrepareLegacyFlatRoot(),
                "shortcut-package-root" => CreateShortcut("package-root.lnk", _installedRoot),
                "shortcut-app-exe" => CreateShortcut(
                    "app-exe.lnk",
                    Path.Combine(_appRoot, "ExpressPackingMonitoring.exe")),
                "missing-target-shortcut" => CreateShortcut(
                    "missing-target.lnk",
                    Path.Combine(_root, "missing", "ExpressPackingMonitoring.exe")),
                "internet-shortcut" => CreateInternetShortcut(),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };
        }

        private string PrepareLegacyFlatRoot()
        {
            string flatTargetPath = Path.Combine(_installedRoot, "Web", "index.html");
            Directory.CreateDirectory(Path.GetDirectoryName(flatTargetPath)!);
            File.WriteAllText(Path.Combine(_installedRoot, "ExpressPackingMonitoring.dll"), "test", Encoding.UTF8);
            File.WriteAllText(flatTargetPath, "old-content", Encoding.UTF8);
            TargetFilePath = flatTargetPath;
            return _installedRoot;
        }

        private string CreateShortcut(string fileName, string targetPath)
        {
            string shortcutPath = Path.Combine(_root, fileName);
            Type shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("WScript.Shell is unavailable");
            object shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Unable to create WScript.Shell");
            object? shortcut = null;
            try
            {
                shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod,
                    null,
                    shell,
                    [shortcutPath]);
                Type shortcutType = shortcut!.GetType();
                shortcutType.InvokeMember(
                    "TargetPath",
                    System.Reflection.BindingFlags.SetProperty,
                    null,
                    shortcut,
                    [targetPath]);
                shortcutType.InvokeMember(
                    "Save",
                    System.Reflection.BindingFlags.InvokeMethod,
                    null,
                    shortcut,
                    null);
            }
            finally
            {
                if (shortcut is not null && Marshal.IsComObject(shortcut))
                    Marshal.FinalReleaseComObject(shortcut);
                if (Marshal.IsComObject(shell))
                    Marshal.FinalReleaseComObject(shell);
            }
            return shortcutPath;
        }

        private string CreateInternetShortcut()
        {
            string path = Path.Combine(_root, "website.url");
            File.WriteAllText(path, "[InternetShortcut]\r\nURL=https://example.com/\r\n", Encoding.UTF8);
            return path;
        }

        private void WriteManifest(
            object[] files,
            string latestVersion = "0.0.2",
            string baselineVersion = "0.0.1",
            object[]? deletes = null,
            string productName = "PackingProof",
            bool includeBrand = true)
        {
            var manifest = new
            {
                type = "baseline_patch",
                patch_baseline_version = baselineVersion,
                latest_version = latestVersion,
                brand = includeBrand ? BrandManifest(productName) : null,
                files
            };
            Directory.CreateDirectory(_patchRoot);
            string manifestJson = deletes is null
                ? JsonSerializer.Serialize(manifest)
                : JsonSerializer.Serialize(new
                {
                    type = "baseline_patch",
                    patch_baseline_version = baselineVersion,
                    latest_version = latestVersion,
                    brand = includeBrand ? BrandManifest(productName) : null,
                    files,
                    deletes
                });
            File.WriteAllText(
                Path.Combine(_patchRoot, "patch_manifest.json"),
                manifestJson,
                Encoding.UTF8);
        }

        private static object BrandManifest(string productName) => new
        {
            profile_id = "test",
            product_name = productName,
            display_name = productName + " 快递打包监控",
            artifact_prefix = "TestPacking",
            compatibility_id = "Test.PackingProof",
            data_directory_name = "TestPackingProof",
            instance_name_prefix = "TestPackingProof",
            package_edition_marker = "Test.PackingProof.PackageEdition.json"
        };

        public async Task<ProcessResult> RunInstallerAsync(
            string appRootPath = "",
            string? configPath = null,
            string? standardInput = null,
            bool verifyVersion = false)
        {
            string powershellPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = powershellPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = standardInput is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.Environment["EPM_TEST_PATCH_SCRIPT"] = _scriptPath;
            startInfo.Environment["EPM_TEST_PATCH_ROOT"] = _patchRoot;
            startInfo.Environment["EPM_TEST_CONFIG_PATH"] = configPath ?? _configPath;
            startInfo.Environment["EPM_TEST_APP_ROOT_PATH"] = appRootPath;
            startInfo.Environment["EPM_TEST_MUTEX_NAME"] = _mutexName;
            string versionSwitch = verifyVersion ? "" : " -SkipVersionCheck";
            foreach (string argument in new[]
            {
                "-NoProfile",
                "-ExecutionPolicy", "Bypass",
                "-Command",
                "$scriptText=[System.IO.File]::ReadAllText($env:EPM_TEST_PATCH_SCRIPT,[System.Text.Encoding]::UTF8); & ([ScriptBlock]::Create($scriptText)) -PatchRoot $env:EPM_TEST_PATCH_ROOT -ConfigPath $env:EPM_TEST_CONFIG_PATH -AppRootPath $env:EPM_TEST_APP_ROOT_PATH -SkipProcessCheck" + versionSwitch + " -MutexName $env:EPM_TEST_MUTEX_NAME"
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start Windows PowerShell");
            if (standardInput is not null)
            {
                await process.StandardInput.WriteLineAsync(standardInput);
                process.StandardInput.Close();
            }
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            try
            {
                await process.WaitForExitAsync(TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }
            catch (TimeoutException)
            {
                // 超时后必须结束整个进程树，否则孤儿进程会一直持有补丁安装互斥锁，连累后续用例。
                try { process.Kill(entireProcessTree: true); } catch { }
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                throw;
            }
            return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

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
