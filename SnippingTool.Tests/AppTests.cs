using System.Windows.Threading;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using System.Windows;
using SnippingTool.Services;
using SnippingTool.Services.Messaging;
using SnippingTool.Models;
using SnippingTool.ViewModels;
using SnippingTool.Tests.Services.Handlers;
using Xunit;
using System.IO;

namespace SnippingTool.Tests;

public sealed class AppTests
{
    [Fact]
    public void ConfigureServices_RegistersCoreServicesAndFactories()
    {
        var services = new ServiceCollection();

        typeof(App)
            .GetMethod("ConfigureServices", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [services]);

        using var provider = services.BuildServiceProvider();

        Assert.IsType<DialogService>(provider.GetRequiredService<IDialogService>());
        Assert.IsType<MessageBoxService>(provider.GetRequiredService<IMessageBoxService>());
        Assert.NotNull(provider.GetRequiredService<Func<IScreenRecordingService, string, RecordingHudViewModel>>());
    }

    [Fact]
    public void OnDispatcherUnhandledException_ShowsRecoveryMessageAndMarksHandled()
    {
        StaTestHelper.Run(() =>
        {
            var app = CreateAppWithoutRunning();
            var messageBoxMock = new Mock<IMessageBoxService>();
            SetField(app, "_messageBox", messageBoxMock.Object);
            var args = CreateDispatcherUnhandledExceptionArgs(new InvalidOperationException("boom"));

            InvokePrivateHandler(app, "OnDispatcherUnhandledException", app, args);

            Assert.True(args.Handled);
            messageBoxMock.Verify(service => service.ShowError(
                It.Is<string>(message => message.Contains("boom", StringComparison.Ordinal)),
                "SnippingTool — Recovered From Error"), Times.Once);
        });
    }

    [Fact]
    public void HandleUpdateAvailable_StoresPendingUpdateAndClickInstallsIt()
    {
        StaTestHelper.Run(() =>
        {
            var app = CreateAppWithoutRunning();
            var autoUpdateMock = new Mock<IAutoUpdateService>();
            autoUpdateMock.Setup(service => service.ConfirmAndInstall(It.IsAny<UpdateCheckResult>()))
                .Returns(Task.CompletedTask);
            SetField(app, "_autoUpdate", autoUpdateMock.Object);
            var update = new UpdateCheckResult(true, new Version(1, 2, 3), "https://example.com/download");

            InvokePrivateHandler(app, "HandleUpdateAvailable", new UpdateAvailableMessage(update));
            var pending = (UpdateCheckResult?)GetField(app, "_pendingUpdate");
            Assert.Same(update, pending);

            InvokePrivateHandler(app, "OnTrayBalloonClicked", app, new RoutedEventArgs());

            autoUpdateMock.Verify(service => service.ConfirmAndInstall(update), Times.Once);
            Assert.Null(GetField(app, "_pendingUpdate"));
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
    public void CheckForUpdates_Click_WhenAlreadyUpToDate_ShowsInformation()
    {
        StaTestHelper.RunAsync(async () =>
        {
            var app = CreateAppWithoutRunning();
            var updateService = new Mock<IUpdateService>();
            updateService
                .Setup(service => service.CheckForUpdates())
                .ReturnsAsync(new UpdateCheckResult(false, new Version(1, 2, 3), string.Empty));

            var messageBox = new Mock<IMessageBoxService>();
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            messageBox
                .Setup(service => service.ShowInformation(It.IsAny<string>(), "Check for Updates"))
                .Callback(() => shown.TrySetResult());

            SetField(app, "_host", CreateHost(updateService.Object, messageBox.Object));
            SetField(app, "_messageBox", messageBox.Object);
            SetField(app, "_autoUpdate", Mock.Of<IAutoUpdateService>());

            var menuItem = new System.Windows.Controls.MenuItem();
            InvokePrivateHandler(app, "CheckForUpdates_Click", menuItem, new RoutedEventArgs());

            await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(menuItem.IsEnabled);

            messageBox.Verify(service => service.ShowInformation(
                It.Is<string>(message => message.Contains("1.2.3", StringComparison.Ordinal)),
                "Check for Updates"), Times.Once);
        });
    }

    [Fact]
    public void CheckForUpdates_Click_WhenUpdateCheckFails_ShowsWarning()
    {
        StaTestHelper.RunAsync(async () =>
        {
            var app = CreateAppWithoutRunning();
            var updateService = new Mock<IUpdateService>();
            updateService
                .Setup(service => service.CheckForUpdates())
                .ThrowsAsync(new InvalidOperationException("boom"));

            var messageBox = new Mock<IMessageBoxService>();
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            messageBox
                .Setup(service => service.ShowWarning(It.IsAny<string>(), "Check for Updates"))
                .Callback(() => shown.TrySetResult());

            SetField(app, "_host", CreateHost(updateService.Object, messageBox.Object));
            SetField(app, "_messageBox", messageBox.Object);
            SetField(app, "_autoUpdate", Mock.Of<IAutoUpdateService>());

            var menuItem = new System.Windows.Controls.MenuItem();
            InvokePrivateHandler(app, "CheckForUpdates_Click", menuItem, new RoutedEventArgs());

            await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(menuItem.IsEnabled);

            messageBox.Verify(service => service.ShowWarning(
                It.Is<string>(message => message.Contains("Could not check for updates", StringComparison.Ordinal)),
                "Check for Updates"), Times.Once);
        });
    }

    [Fact]
    public void OnTrayBalloonClicked_WithoutPendingUpdate_DoesNothing()
    {
        StaTestHelper.Run(() =>
        {
            var app = CreateAppWithoutRunning();
            var autoUpdateMock = new Mock<IAutoUpdateService>();
            SetField(app, "_autoUpdate", autoUpdateMock.Object);

            InvokePrivateHandler(app, "OnTrayBalloonClicked", app, new RoutedEventArgs());

            autoUpdateMock.Verify(service => service.ConfirmAndInstall(It.IsAny<UpdateCheckResult>()), Times.Never);
        });
    }

    [Fact]
    public void OnTrayBalloonClicked_WithPendingRecording_OpensSavedRecording()
    {
        StaTestHelper.Run(() =>
        {
            var app = CreateAppWithoutRunning();
            var processMock = new Mock<IProcessService>();
            var messageBoxMock = new Mock<IMessageBoxService>();
            var tempFilePath = Path.GetTempFileName();
            SetField(app, "_host", CreateHost(processService: processMock.Object, messageBox: messageBoxMock.Object));
            SetField(app, "_messageBox", messageBoxMock.Object);
            SetField(app, "_pendingRecordingBalloonPath", tempFilePath);

            try
            {
                InvokePrivateHandler(app, "OnTrayBalloonClicked", app, new RoutedEventArgs());

                processMock.Verify(
                    process => process.Start(It.Is<ProcessStartInfo>(info => info.FileName == tempFilePath && info.UseShellExecute)),
                    Times.Once);
                Assert.Null(GetField(app, "_pendingRecordingBalloonPath"));
            }
            finally
            {
                File.Delete(tempFilePath);
            }
        });
    }

    [Fact]
    public void OnUnobservedTaskException_MarksObserved()
    {
        StaTestHelper.Run(() =>
        {
            var app = CreateAppWithoutRunning();
            var args = new UnobservedTaskExceptionEventArgs(new AggregateException(new InvalidOperationException("boom")));

            InvokePrivateHandler(app, "OnUnobservedTaskException", app, args);

            Assert.True(args.Observed);
        });
    }

    private static App CreateAppWithoutRunning()
    {
        return (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
    }

    private static void InvokePrivateHandler(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(target, args);
    }

    private static void InvokePrivateStatic(string methodName, params object[] args)
    {
        var method = typeof(App).GetMethod(methodName, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(null, args);
    }

    private static object? GetField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field.GetValue(target);
    }

    private static void SetField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }

    private static IHost CreateHost(
        IUpdateService? updateService = null,
        IMessageBoxService? messageBox = null,
        IProcessService? processService = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(updateService ?? Mock.Of<IUpdateService>());
        services.AddSingleton(Mock.Of<IAppVersionService>(version => version.Current == new Version(1, 2, 3)));
        services.AddSingleton(messageBox ?? Mock.Of<IMessageBoxService>());
        services.AddSingleton(processService ?? Mock.Of<IProcessService>());

        var provider = services.BuildServiceProvider();
        var host = new Mock<IHost>();
        host.SetupGet(current => current.Services).Returns(provider);
        return host.Object;
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
