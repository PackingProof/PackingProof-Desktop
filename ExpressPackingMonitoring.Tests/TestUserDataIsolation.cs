using System.Runtime.CompilerServices;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 测试进程必须把用户数据目录指到临时目录。
///
/// <c>AppPaths</c> 是静态只读的，一旦按默认路径初始化，整个测试进程的 RuntimeLog、配置、
/// 缓存和备份状态都会写进用户的 <c>%LOCALAPPDATA%\ExpressPackingMonitoring</c>。现场排查时
/// runtime.log 里就会混进单元测试的日志（归档、备份、更新等压根没打开的模块），分不清
/// 哪条是真实运行产生的。
///
/// 必须在任何代码碰到 <c>AppPaths</c> 之前设置，所以放在模块初始化器里；
/// 这也是 AutomationHost 已经在用的同一个环境变量。
/// </summary>
internal static class TestUserDataIsolation
{
    internal const string UserDataEnvironmentVariable = "EPM_USER_DATA_DIR";

    [ModuleInitializer]
    internal static void InitializeIsolatedUserDataDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "epm-tests-userdata");
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // 目录被占用时沿用已有目录，仍然比写进用户数据目录好。
        }

        Environment.SetEnvironmentVariable(UserDataEnvironmentVariable, directory);
    }
}
