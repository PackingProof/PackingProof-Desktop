using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.UpdateCore;
using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;

namespace ExpressPackingMonitoring;

public sealed class WorkstationInstanceCoordinator : IDisposable
{
    private const string LegacyMutexName = @"Local\ExpressPackingMonitoring.Mutex";
    private const string LegacyActivationPipeName = "ExpressPackingMonitoring.Activate";
    private static string NamePrefix => BrandMetadata.InstanceNamePrefix;
    private static string HostMutexName => $@"Local\{NamePrefix}.Host.Mutex";
    private readonly Mutex _mutex;
    private readonly Mutex? _legacyMutex;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;
    private Window? _window;
    private bool _disposed;

    private WorkstationInstanceCoordinator(Mutex mutex, Mutex? legacyMutex)
    {
        _mutex = mutex;
        _legacyMutex = legacyMutex;
    }

    public static bool TryCreate(out WorkstationInstanceCoordinator? coordinator)
        => TryCreate(out coordinator, out _);

    /// <summary>
    /// 同版本重复启动与“旧版本仍在运行”要区分处理：前者激活已有窗口，
    /// 后者必须先阻止新进程写同一份配置和 SQLite 数据库。
    /// </summary>
    public static bool TryCreate(
        out WorkstationInstanceCoordinator? coordinator,
        out InstanceStartConflict conflict)
    {
        string? explicitScope = Environment.GetEnvironmentVariable("EPM_INSTANCE_SCOPE");
        bool packaged = PackagingIdentity.IsPackaged;
        // 只有隔离测试显式设置这个开关时才跳过旧锁兼容；普通 EPM_INSTANCE_SCOPE
        // 不能再关闭兼容锁，避免环境变量泄漏后升级窗口重新出现双实例。
        bool skipLegacyAlias = string.Equals(
            Environment.GetEnvironmentVariable("EPM_SKIP_LEGACY_INSTANCE_LOCK"),
            "1",
            StringComparison.OrdinalIgnoreCase);
        bool acquireLegacyAlias = !packaged
            && !skipLegacyAlias
            && string.Equals(BrandMetadata.CompatibilityId, "PackingProof", StringComparison.OrdinalIgnoreCase);
        return TryCreateCore(
            out coordinator,
            out conflict,
            explicitScope,
            LegacyMutexName,
            packaged,
            acquireLegacyAlias);
    }

    internal static bool TryCreateForTest(
        out WorkstationInstanceCoordinator? coordinator,
        out InstanceStartConflict conflict,
        string explicitScope,
        string legacyMutexName,
        bool packaged = false,
        bool acquireLegacyAlias = true)
        => TryCreateCore(
            out coordinator,
            out conflict,
            explicitScope,
            legacyMutexName,
            packaged,
            acquireLegacyAlias);

    private static bool TryCreateCore(
        out WorkstationInstanceCoordinator? coordinator,
        out InstanceStartConflict conflict,
        string? explicitScope,
        string legacyMutexName,
        bool packaged,
        bool acquireLegacyAlias)
    {
        coordinator = null;
        conflict = InstanceStartConflict.None;
        var mutex = new Mutex(
            initiallyOwned: true,
            GetMutexName(packaged, explicitScope),
            out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            conflict = InstanceStartConflict.AlreadyRunning;
            return false;
        }

        Mutex? legacyMutex = null;
        if (acquireLegacyAlias)
        {
            legacyMutex = new Mutex(initiallyOwned: true, legacyMutexName, out bool legacyCreatedNew);
            if (!legacyCreatedNew)
            {
                try { mutex.ReleaseMutex(); } catch { }
                mutex.Dispose();
                legacyMutex.Dispose();
                conflict = InstanceStartConflict.LegacyVersionRunning;
                return false;
            }
        }

        coordinator = new WorkstationInstanceCoordinator(mutex, legacyMutex);
        return true;
    }

    public static bool IsRunning()
    {
        if (MutexExists(GetMutexName()))
            return true;

        return !PackagingIdentity.IsPackaged
            && string.Equals(BrandMetadata.CompatibilityId, "PackingProof", StringComparison.OrdinalIgnoreCase)
            && MutexExists(LegacyMutexName);
    }

    private static bool MutexExists(string name)
    {
        try
        {
            using var existing = Mutex.OpenExisting(name);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static bool RequestActivate()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", GetPipeName(), PipeDirection.Out);
            pipe.Connect(400);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine("activate");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// v0.0.84 及更早的 Setup 版使用无后缀的实例锁和激活管道。
    /// 新版检测到旧锁时先唤醒旧窗口，避免升级期间出现两个实例。
    /// </summary>
    public static bool RequestActivateLegacy()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", LegacyActivationPipeName, PipeDirection.Out);
            pipe.Connect(800);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine("activate");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 备份主机跨安装形态共享：Store 版和 Setup 版可以同时运行，
    /// 但同一时间只能有一个进程作为备份主机。
    /// </summary>
    internal static bool TryAcquireHostLease(out IDisposable? lease, string? testScope = null)
    {
        lease = null;
        string mutexName = string.IsNullOrWhiteSpace(testScope)
            ? HostMutexName
            : $@"Local\{NamePrefix}.Host.{NormalizeScope(testScope)}.Mutex";
        var mutex = new Mutex(initiallyOwned: false, mutexName);
        bool acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                return false;
            }

            lease = new HostLease(mutex);
            return true;
        }
        catch
        {
            if (!acquired) mutex.Dispose();
            throw;
        }
    }

    public void StartActivationListener(Window window)
    {
        if (_disposed || _listenTask != null) return;
        _window = window;
        _listenTask = Task.Run(ListenForActivationAsync);
    }

    private async Task ListenForActivationAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    GetPipeName(),
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(_cts.Token);
                ActivateWindow();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                if (_cts.IsCancellationRequested) break;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Instance", $"Activation listener error: {ex.Message}");
                await Task.Delay(300, _cts.Token).ContinueWith(_ => { });
            }
        }
    }

    private void ActivateWindow()
    {
        var window = _window;
        if (window == null || _disposed) return;

        _ = window.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;
                window.Show();
                window.Activate();
                window.Topmost = true;
                window.Topmost = false;
                window.Focus();
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Instance", $"Activate window failed: {ex.Message}");
            }
        }), DispatcherPriority.Normal);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { }
        try { _mutex.ReleaseMutex(); } catch { }
        try { _mutex.Dispose(); } catch { }
        try { _legacyMutex?.ReleaseMutex(); } catch { }
        try { _legacyMutex?.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
    }

    private static string GetMutexName() => $@"Local\{GetScopedNamePrefix()}.Mutex";
    private static string GetMutexName(bool packaged, string? explicitScope) =>
        $@"Local\{GetInstanceScopeName(packaged, explicitScope)}.Mutex";
    private static string GetPipeName() => $"{GetScopedNamePrefix()}.Activate";
    internal static string GetInstanceScopeName(bool packaged, string? explicitScope = null)
    {
        string edition = packaged ? "Store" : "Portable";
        string normalizedScope = NormalizeScope(
            explicitScope ?? Environment.GetEnvironmentVariable("EPM_INSTANCE_SCOPE"));
        return string.IsNullOrEmpty(normalizedScope)
            ? $"{NamePrefix}.{edition}"
            : $"{NamePrefix}.{edition}.{normalizedScope}";
    }

    private static string GetScopedNamePrefix() =>
        GetInstanceScopeName(PackagingIdentity.IsPackaged);

    private static string NormalizeScope(string? scope) =>
        new((scope ?? "").Where(char.IsLetterOrDigit).Take(48).ToArray());

    private sealed class HostLease : IDisposable
    {
        private readonly Mutex _mutex;

        public HostLease(Mutex mutex)
        {
            _mutex = mutex;
        }

        public void Dispose()
        {
            try { _mutex.ReleaseMutex(); } catch { }
            try { _mutex.Dispose(); } catch { }
        }
    }
}

public enum InstanceStartConflict
{
    None,
    AlreadyRunning,
    LegacyVersionRunning
}
