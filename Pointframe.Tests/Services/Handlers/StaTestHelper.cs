using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace Pointframe.Tests.Services.Handlers;

internal static class StaTestHelper
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static int _liveThreadCount;

    public static void Run(
        Action action,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "") => Run(action, DefaultTimeout, callerMemberName, callerFilePath);

    internal static void Run(
        Action action,
        TimeSpan timeout,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "")
    {
        Exception? exception = null;
        var started = 0;
        var stopwatch = Stopwatch.StartNew();

        var thread = new Thread(() =>
        {
            Interlocked.Increment(ref _liveThreadCount);
            try
            {
                Volatile.Write(ref started, 1);
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                Interlocked.Decrement(ref _liveThreadCount);
            }
        });

        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(timeout))
        {
            throw CreateTimeoutException("STA", timeout, callerMemberName, callerFilePath, Volatile.Read(ref started) == 1, stopwatch.Elapsed);
        }

        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    public static void RunAsync(
        Func<Task> action,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "") => RunAsync(action, DefaultTimeout, callerMemberName, callerFilePath);

    internal static void RunAsync(
        Func<Task> action,
        TimeSpan timeout,
        [CallerMemberName] string callerMemberName = "",
        [CallerFilePath] string callerFilePath = "")
    {
        ExceptionDispatchInfo? capturedException = null;
        Dispatcher? dispatcher = null;
        var started = 0;
        var stopwatch = Stopwatch.StartNew();

        var thread = new Thread(() =>
        {
            Interlocked.Increment(ref _liveThreadCount);
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;

                async void RunCore()
                {
                    Volatile.Write(ref started, 1);
                    try
                    {
                        await action();
                    }
                    catch (Exception ex)
                    {
                        capturedException = ExceptionDispatchInfo.Capture(ex);
                    }
                    finally
                    {
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    }
                }

                RunCore();
                Dispatcher.Run();
            }
            finally
            {
                Interlocked.Decrement(ref _liveThreadCount);
            }
        });

        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(timeout))
        {
            dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
            throw CreateTimeoutException("Async STA", timeout, callerMemberName, callerFilePath, Volatile.Read(ref started) == 1, stopwatch.Elapsed);
        }

        capturedException?.Throw();
    }

    private static TimeoutException CreateTimeoutException(
        string operation,
        TimeSpan timeout,
        string callerMemberName,
        string callerFilePath,
        bool started,
        TimeSpan elapsed) => new(
            $"{operation} test '{callerMemberName}' in '{Path.GetFileName(callerFilePath)}' exceeded timeout of {timeout.TotalSeconds:F0} seconds; " +
            $"started={started}, elapsed={elapsed.TotalSeconds:F2} seconds, live StaTestHelper threads={Volatile.Read(ref _liveThreadCount)}.");
}
