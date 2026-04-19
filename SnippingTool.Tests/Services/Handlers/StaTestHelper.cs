using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace Pointframe.Tests.Services.Handlers;

internal static class StaTestHelper
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static void Run(Action action)
    {
        Exception? exception = null;

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(DefaultTimeout))
        {
            throw new TimeoutException($"STA test exceeded timeout of {DefaultTimeout.TotalSeconds:F0} seconds.");
        }

        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    public static void RunAsync(Func<Task> action)
    {
        ExceptionDispatchInfo? capturedException = null;
        Dispatcher? dispatcher = null;

        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;

            async void RunCore()
            {
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
        });

        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(DefaultTimeout))
        {
            dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
            throw new TimeoutException($"Async STA test exceeded timeout of {DefaultTimeout.TotalSeconds:F0} seconds.");
        }

        capturedException?.Throw();
    }
}