using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pointframe.Services;
using Pointframe.Tests.Services.Handlers;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class AppErrorHandlerTests
{
    [Fact]
    public void OnDispatcherUnhandledException_ShowsRecoveryMessageAndMarksHandled()
    {
        StaTestHelper.Run(() =>
        {
            var messageBoxMock = new Mock<IMessageBoxService>();
            var handler = new AppErrorHandler(NullLogger<AppErrorHandler>.Instance, messageBoxMock.Object, Mock.Of<ITelemetryService>());
            var args = CreateDispatcherUnhandledExceptionArgs(new InvalidOperationException("boom"));

            InvokePrivate(handler, "OnDispatcherUnhandledException", handler, args);

            Assert.True(args.Handled);
            messageBoxMock.Verify(service => service.ShowError(
                It.Is<string>(message => message.Contains("boom", StringComparison.Ordinal)),
                "Pointframe — Recovered From Error"), Times.Once);
        });
    }

    [Fact]
    public void CloseWindowTree_ClosesOwnedWindowsRecursively()
    {
        StaTestHelper.Run(() =>
        {
            var root = new Window { Title = "Root", Width = 100, Height = 100 };
            root.Show();
            var child = new Window { Title = "Child", Width = 100, Height = 100, Owner = root };
            child.Show();

            InvokePrivateStatic("CloseWindowTree", root);

            Assert.False(root.IsVisible);
            Assert.False(child.IsVisible);
        });
    }

    [Fact]
    public void OnUnobservedTaskException_MarksObserved()
    {
        StaTestHelper.Run(() =>
        {
            var handler = new AppErrorHandler(NullLogger<AppErrorHandler>.Instance, Mock.Of<IMessageBoxService>(), Mock.Of<ITelemetryService>());
            var args = new UnobservedTaskExceptionEventArgs(new AggregateException(new InvalidOperationException("boom")));

            InvokePrivate(handler, "OnUnobservedTaskException", handler, args);

            Assert.True(args.Observed);
        });
    }

    private static void InvokePrivate(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(target, args);
    }

    private static void InvokePrivateStatic(string methodName, params object[] args)
    {
        var method = typeof(AppErrorHandler).GetMethod(methodName, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(null, args);
    }

    private static DispatcherUnhandledExceptionEventArgs CreateDispatcherUnhandledExceptionArgs(Exception exception)
    {
        var constructor = typeof(DispatcherUnhandledExceptionEventArgs).GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            [typeof(Dispatcher)],
            modifiers: null);
        Assert.NotNull(constructor);

        var args = (DispatcherUnhandledExceptionEventArgs)constructor.Invoke([Dispatcher.CurrentDispatcher]);
        var exceptionField = typeof(DispatcherUnhandledExceptionEventArgs).GetField(
            "_exception",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(exceptionField);
        exceptionField.SetValue(args, exception);

        return args;
    }
}
