using System.Runtime.CompilerServices;
using ExpressPackingMonitoring.Localization;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// CI runner 的默认 UI 语言是英文，而大量行为测试断言的是软件默认中文文案。
/// 测试程序集加载时统一到软件默认语言；需要英文/日文的本地化用例仍会自行切换
/// CultureInfo.CurrentUICulture。
/// </summary>
internal static class AssemblyCultureInitializer
{
    [ModuleInitializer]
    internal static void Initialize() => AppLanguage.Initialize(AppLanguage.Chinese);
}
