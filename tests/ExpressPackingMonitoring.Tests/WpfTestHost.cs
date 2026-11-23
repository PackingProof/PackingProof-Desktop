using System.Text;
using System.Windows;
using System.Windows.Threading;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ExpressPackingMonitoring.Tests;

[CollectionDefinition("WPF render tests", DisableParallelization = true)]
public sealed class WpfRenderTestCollection;

/// <summary>
/// 每个测试进程共用一个 STA Dispatcher 和 Application。
/// WPF 的 Application、资源字典和控件都有线程亲和性，各个测试类自行创建 STA 线程
/// 会留下归属于已结束线程的全局 Application，后续测试再访问时就会跨线程失败。
/// </summary>
internal static class WpfTestHost
{
    private static Exception? _unhandledDispatcherException;

    private static readonly Lazy<Dispatcher> SharedDispatcher =
        new(CreateDispatcher, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static void Run(
        Action action,
        TimeSpan? timeout = null,
        string timeoutMessage = "WPF 测试执行超时")
    {
        Dispatcher dispatcher = SharedDispatcher.Value;
        if (dispatcher.CheckAccess())
        {
            ThrowPendingDispatcherException();
            action();
            ThrowPendingDispatcherException();
            return;
        }

        ThrowPendingDispatcherException();
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        dispatcher.BeginInvoke(() =>
        {
            try
            {
                ThrowPendingDispatcherException();
                action();
                ThrowPendingDispatcherException();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                completed.Set();
            }
        });

        if (!completed.Wait(timeout ?? TimeSpan.FromSeconds(120)))
            throw new Xunit.Sdk.XunitException(timeoutMessage);
        ThrowPendingDispatcherException();
        if (failure == null)
            return;

        var detail = new StringBuilder();
        for (Exception? current = failure; current != null; current = current.InnerException)
            detail.AppendLine($"{current.GetType().Name}: {current.Message}");
        detail.AppendLine(failure.StackTrace);
        throw new Xunit.Sdk.XunitException(detail.ToString());
    }

    private static void ThrowPendingDispatcherException()
    {
        Exception? exception = Interlocked.Exchange(ref _unhandledDispatcherException, null);
        if (exception != null)
            throw new Xunit.Sdk.XunitException(
                "WPF Dispatcher 出现未处理异常：" + Environment.NewLine + exception);
    }

    private static Dispatcher CreateDispatcher()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            ExpressPackingMonitoring.App.StartWithoutMainWindowForTests = true;
            var app = new ExpressPackingMonitoring.App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.UnhandledException += (_, args) =>
            {
                _unhandledDispatcherException = args.Exception;
                args.Handled = true;
            };
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            app.Startup += (_, _) => ready.Set();
            app.Run();
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(30)))
            throw new InvalidOperationException("WPF 测试 Dispatcher 启动超时");
        return dispatcher ?? throw new InvalidOperationException("WPF 测试 Dispatcher 未创建");
    }
}
