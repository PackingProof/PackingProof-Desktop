using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace ExpressPackingMonitoring.Audio
{
    /// <summary>
    /// Windows 系统语音（WinRT）桥接：仅在 Windows 8+ 且辅助程序集存在时
    /// 按需加载 ExpressPackingMonitoring.WinTts.dll，避免 Windows 7 加载
    /// WinRT 运行时导致进程崩溃。
    /// </summary>
    internal sealed class WindowsTtsBridge : IDisposable
    {
        private const string HelperAssemblyFile = "ExpressPackingMonitoring.WinTts.dll";
        private const string HelperTypeName = "ExpressPackingMonitoring.WinTts.WindowsTtsFallback";

        private readonly object _instance;
        private readonly MethodInfo _trySynthesize;
        private readonly MethodInfo _dispose;

        private WindowsTtsBridge(object instance, MethodInfo trySynthesize, MethodInfo dispose)
        {
            _instance = instance;
            _trySynthesize = trySynthesize;
            _dispose = dispose;
        }

        public static WindowsTtsBridge? TryCreate()
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2))
                return null;

            try
            {
                string helperPath = Path.Combine(AppContext.BaseDirectory, HelperAssemblyFile);
                if (!File.Exists(helperPath))
                    return null;

                var helperAlc = new AssemblyLoadContext("ExpressPackingMonitoring.WinTts", isCollectible: false);
                helperAlc.Resolving += (context, name) =>
                {
                    string candidate = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
                    return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
                };
                Assembly helperAssembly = helperAlc.LoadFromAssemblyPath(helperPath);
                Type? helperType = helperAssembly.GetType(HelperTypeName);
                if (helperType == null)
                    return null;

                object? instance = Activator.CreateInstance(helperType);
                if (instance == null)
                    return null;

                MethodInfo? trySynthesize = helperType.GetMethod("TrySynthesize");
                MethodInfo? dispose = helperType.GetMethod("Dispose");
                if (trySynthesize == null || dispose == null)
                {
                    (instance as IDisposable)?.Dispose();
                    return null;
                }

                Logging.RuntimeLog.Info("Speech", "WindowsTts bridge loaded");
                return new WindowsTtsBridge(instance, trySynthesize, dispose);
            }
            catch
            {
                return null;
            }
        }

        public bool TrySynthesize(string text, bool isWarning, out byte[] wavData)
        {
            wavData = Array.Empty<byte>();
            try
            {
                object[] args = { text, isWarning, null! };
                bool ok = (bool)(_trySynthesize.Invoke(_instance, args) ?? false);
                if (ok)
                    wavData = (byte[])args[2]!;
                return ok;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            try
            {
                _dispose.Invoke(_instance, null);
            }
            catch
            {
            }
        }
    }
}
