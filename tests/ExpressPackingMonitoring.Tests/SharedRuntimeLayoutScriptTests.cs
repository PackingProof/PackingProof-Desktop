using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ExpressPackingMonitoring.UpdateCore;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 共享运行时布局（runtime\）的构建与校验脚本回归。用假的应用目录与假的 .NET 安装目录跑，
/// 不需要真实的 140MB 运行时，因此可以进默认测试套件。
/// </summary>
public sealed class SharedRuntimeLayoutScriptTests
{
    [Fact]
    public void SharedRuntimeLayout_ExcludesRetiredWinFormsIntegration()
    {
        string script = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "tools", "SharedRuntimeLayout.Common.ps1"),
            Encoding.UTF8);

        Assert.Contains("'WindowsFormsIntegration.dll'", script, StringComparison.Ordinal);
        Assert.Contains("'WindowsFormsIntegration.resources.dll'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WritesManifestWithVersionsAndMigrationMap()
    {
        using var fixture = new Fixture();

        ProcessResult build = fixture.Build();

        Assert.True(build.ExitCode == 0, build.Output + build.Error);
        Assert.True(File.Exists(Path.Combine(fixture.LayoutPath, "dotnet.exe")));
        Assert.True(File.Exists(Path.Combine(fixture.LayoutPath, "host", "fxr", Fixture.HostFxrVersion, "hostfxr.dll")));

        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(fixture.LayoutPath, "layout.json"), Encoding.UTF8));
        JsonElement root = manifest.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Fixture.HostFxrVersion, root.GetProperty("hostFxrVersion").GetString());

        JsonElement frameworks = root.GetProperty("frameworks");
        Assert.Equal(2, frameworks.GetArrayLength());
        foreach (JsonElement framework in frameworks.EnumerateArray())
        {
            Assert.Equal(Fixture.RuntimeVersion, framework.GetProperty("version").GetString());
            Assert.Equal("8.0.0", framework.GetProperty("requiredVersion").GetString());
        }

        JsonElement files = root.GetProperty("files");
        Assert.Equal(fixture.ExpectedLayoutFileCount, files.GetArrayLength());

        // 两个共享框架里都有的程序集必须给出两个候选，迁移时才能按“布局里已有就删掉老副本”处理。
        JsonElement map = root.GetProperty("appFileMap");
        Assert.Equal(1, map.GetProperty("System.Private.CoreLib.dll").GetArrayLength());
        Assert.Equal(2, map.GetProperty("Microsoft.VisualBasic.dll").GetArrayLength());
        Assert.Equal(1, map.GetProperty("hostfxr.dll").GetArrayLength());
    }

    [Fact]
    public void Build_PrefersTheExactRuntimeVersionDeclaredByTheApp()
    {
        using var fixture = new Fixture(requiredNetCoreVersion: "8.0.31");
        fixture.AddHostFxrVersion("8.0.31");
        fixture.AddNewerRuntimeVersion("8.0.32", "8.0.12");

        ProcessResult build = fixture.Build();

        Assert.True(build.ExitCode == 0, build.Output + build.Error);
        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(fixture.LayoutPath, "layout.json"), Encoding.UTF8));
        JsonElement root = manifest.RootElement;
        Assert.Equal("8.0.31", root.GetProperty("hostFxrVersion").GetString());
        foreach (JsonElement framework in root.GetProperty("frameworks").EnumerateArray())
            Assert.Equal(Fixture.RuntimeVersion, framework.GetProperty("version").GetString());
    }

    [Fact]
    public void Verify_DetectsTamperedFile()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Build().ExitCode == 0, "构建应成功");
        File.WriteAllText(
            Path.Combine(fixture.LayoutPath, "shared", "Microsoft.NETCore.App", Fixture.RuntimeVersion, "System.Private.CoreLib.dll"),
            "tampered");

        ProcessResult verify = fixture.Verify();

        Assert.NotEqual(0, verify.ExitCode);
        Assert.Contains("大小与清单不一致", verify.Output + verify.Error);
    }

    [Fact]
    public void Verify_DetectsUnlistedFile()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Build().ExitCode == 0, "构建应成功");
        File.WriteAllText(
            Path.Combine(fixture.LayoutPath, "shared", "Microsoft.NETCore.App", Fixture.RuntimeVersion, "extra.dll"),
            "extra");

        ProcessResult verify = fixture.Verify();

        Assert.NotEqual(0, verify.ExitCode);
        Assert.Contains("清单之外的文件", verify.Output + verify.Error);
    }

    /// <summary>
    /// 安装目录里那份布局是主程序用硬链接落成的，根目录会带一个落成标记。校验脚本必须认这个名字
    /// （C# 侧的同一个常量），否则真机自检会把合法的落成布局判成"有清单之外的文件"。
    /// </summary>
    [Fact]
    public void Verify_AllowsTheSeedMarkerWrittenByTheApp()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Build().ExitCode == 0, "构建应成功");
        File.WriteAllText(
            Path.Combine(fixture.LayoutPath, RuntimeLayoutSeeder.SeedMarkerFileName),
            "seeded",
            new UTF8Encoding(false));

        ProcessResult verify = fixture.Verify();

        Assert.True(verify.ExitCode == 0, verify.Output + verify.Error);
    }

    [Fact]
    public void Build_RejectsUnavailableFrameworkVersion()
    {
        using var fixture = new Fixture(requiredNetCoreVersion: "8.0.99");

        ProcessResult build = fixture.Build();

        Assert.NotEqual(0, build.ExitCode);
        Assert.Contains("版本过低", build.Output + build.Error);
    }

    [Fact]
    public void Build_RejectsExistingDestinationDirectory()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.LayoutPath);

        ProcessResult build = fixture.Build();

        Assert.NotEqual(0, build.ExitCode);
        Assert.Contains("目标目录已存在", build.Output + build.Error);
    }

    private sealed class Fixture : IDisposable
    {
        internal const string HostFxrVersion = "8.0.10";
        internal const string RuntimeVersion = "8.0.31";
        private const string RequiredNetCoreVersion = "8.0.0";

        private readonly string _root;
        private readonly string _repositoryRoot;
        private readonly string _dotNetRoot;
        private readonly string _appDir;

        public Fixture(string requiredNetCoreVersion = RequiredNetCoreVersion)
        {
            _repositoryRoot = FindRepositoryRoot();
            _root = Path.Combine(Path.GetTempPath(), $"packingproof-shared-runtime-{Guid.NewGuid():N}");
            _dotNetRoot = Path.Combine(_root, "dotnet");
            _appDir = Path.Combine(_root, "app");
            LayoutPath = Path.Combine(_root, "runtime");

            WriteFile(Path.Combine(_dotNetRoot, "dotnet.exe"), "muxer");
            WriteFile(Path.Combine(_dotNetRoot, "host", "fxr", HostFxrVersion, "hostfxr.dll"), "hostfxr");
            foreach (string framework in new[] { "Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App" })
            {
                string frameworkRoot = Path.Combine(_dotNetRoot, "shared", framework, RuntimeVersion);
                WriteFile(Path.Combine(frameworkRoot, framework + ".deps.json"), "{}");
                WriteFile(Path.Combine(frameworkRoot, framework + ".runtimeconfig.json"), "{}");
            }
            WriteFile(Path.Combine(_dotNetRoot, "shared", "Microsoft.NETCore.App", RuntimeVersion, "System.Private.CoreLib.dll"), "corelib");
            WriteFile(Path.Combine(_dotNetRoot, "shared", "Microsoft.WindowsDesktop.App", RuntimeVersion, "PresentationFramework.dll"), "wpf");
            // 两个框架里都存在同名程序集：迁移映射必须给出两个候选。
            WriteFile(Path.Combine(_dotNetRoot, "shared", "Microsoft.NETCore.App", RuntimeVersion, "Microsoft.VisualBasic.dll"), "vb-core");
            WriteFile(Path.Combine(_dotNetRoot, "shared", "Microsoft.WindowsDesktop.App", RuntimeVersion, "Microsoft.VisualBasic.dll"), "vb-desktop");

            Directory.CreateDirectory(_appDir);
            File.WriteAllText(
                Path.Combine(_appDir, "ExpressPackingMonitoring.runtimeconfig.json"),
                $$"""
                {
                  "runtimeOptions": {
                    "tfm": "net8.0",
                    "frameworks": [
                      { "name": "Microsoft.NETCore.App", "version": "{{requiredNetCoreVersion}}" },
                      { "name": "Microsoft.WindowsDesktop.App", "version": "{{requiredNetCoreVersion}}" }
                    ]
                  }
                }
                """,
                new UTF8Encoding(false));
        }

        public string LayoutPath { get; }

        /// <summary>布局里应有的文件数：dotnet.exe + hostfxr + 两个框架各 4 个文件。</summary>
        public int ExpectedLayoutFileCount => 1 + 1 + 4 + 4;

        public void AddHostFxrVersion(string version)
            => WriteFile(Path.Combine(_dotNetRoot, "host", "fxr", version, "hostfxr.dll"), "exact-hostfxr");

        public void AddNewerRuntimeVersion(string runtimeVersion, string hostFxrVersion)
        {
            WriteFile(Path.Combine(_dotNetRoot, "host", "fxr", hostFxrVersion, "hostfxr.dll"), "newer-hostfxr");
            foreach (string framework in new[] { "Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App" })
            {
                string frameworkRoot = Path.Combine(_dotNetRoot, "shared", framework, runtimeVersion);
                WriteFile(Path.Combine(frameworkRoot, framework + ".deps.json"), "{}");
                WriteFile(Path.Combine(frameworkRoot, framework + ".runtimeconfig.json"), "{}");
                WriteFile(Path.Combine(frameworkRoot, "newer-only.dll"), "newer");
            }
        }

        public ProcessResult Build()
        {
            string script = Path.Combine(_repositoryRoot, "tools", "Build-SharedRuntimeLayout.ps1");
            return RunPowerShell(
                $"& '{EscapePowerShell(script)}' -AppDir '{EscapePowerShell(_appDir)}' " +
                $"-OutputPath '{EscapePowerShell(LayoutPath)}' -DotNetRoot '{EscapePowerShell(_dotNetRoot)}'",
                _repositoryRoot);
        }

        public ProcessResult Verify()
        {
            string script = Path.Combine(_repositoryRoot, "tools", "Test-SharedRuntimeLayout.ps1");
            return RunPowerShell(
                $"& '{EscapePowerShell(script)}' -LayoutPath '{EscapePowerShell(LayoutPath)}'",
                _repositoryRoot);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private static void WriteFile(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
    }

    private static ProcessResult RunPowerShell(string command, string workingDirectory)
    {
        command = "$ErrorActionPreference = 'Stop'\n$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)\n" + command;
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var startInfo = new ProcessStartInfo("pwsh.exe")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encoded);
        using Process process = Process.Start(startInfo)!;
        // 两个流必须同时异步读：PowerShell 会把 Write-Host 的信息流序列化成 CLIXML 写到 stderr，
        // 顺序读会写满 stderr 缓冲区，双方互相等，直接把测试挂死。
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, outputTask.Result, errorTask.Result);
    }

    private static string EscapePowerShell(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

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

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
