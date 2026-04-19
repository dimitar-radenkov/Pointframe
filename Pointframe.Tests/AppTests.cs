using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pointframe.Models;
using Pointframe.Services;
using Pointframe.Services.Messaging;
using Pointframe.Tests.Services.Handlers;
using Pointframe.ViewModels;
using Xunit;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace Pointframe.Tests;

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
                "Pointframe — Recovered From Error"), Times.Once);
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

    [Fact]
    public void RegisterAutomationWindow_WhenAutomationDisabled_DoesNotAttachHandler()
    {
        StaTestHelper.Run(() =>
        {
            var app = CreateAppWithoutRunning();
            SetField(app, "_isAutomationMode", false);
            var window = new Window();

            var closedHandlers = 0;
            window.Closed += (_, _) => closedHandlers++;

            app.RegisterAutomationWindow(window);
            window.Close();

            Assert.Equal(1, closedHandlers);
        });
    }

    [Fact]
    public void CreateTrayMenuItem_AssignsHeaderAndClickHandler()
    {
        StaTestHelper.Run(() =>
        {
            var clicked = false;
            var menuItem = (WpfMenuItem)InvokePrivateStaticResult(
                "CreateTrayMenuItem",
                "Test Item",
                new RoutedEventHandler((_, _) => clicked = true));

            Assert.Equal("Test Item", menuItem.Header);
            menuItem.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            Assert.True(clicked);
        });
    }

    [Fact]
    public void OpenRecentRecordingFolder_Click_WithValidTag_OpensFolder()
    {
        StaTestHelper.Run(() =>
        {
            var app = CreateAppWithoutRunning();
            var processMock = new Mock<IProcessService>();
            SetField(app, "_host", CreateHost(processService: processMock.Object));

            var recentRecording = CreateRecentRecordingItem(@"C:\\temp\\video.mp4", "00:10");
            var menuItem = new WpfMenuItem { Tag = recentRecording };

            InvokePrivateHandler(app, "OpenRecentRecordingFolder_Click", menuItem, new RoutedEventArgs());

            processMock.Verify(process => process.Start(It.Is<ProcessStartInfo>(info =>
                info.FileName == "explorer.exe" && info.Arguments == @"C:\temp")), Times.Once);
        });
    }

    [Fact]
    public void ExportRecentRecordingGif_Click_WhenRecordingMissing_ShowsWarning()
    {
        StaTestHelper.Run(() =>
        {
            var app = CreateAppWithoutRunning();
            var messageBoxMock = new Mock<IMessageBoxService>();
            SetField(app, "_messageBox", messageBoxMock.Object);

            var missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid()}.mp4");
            var recentRecording = CreateRecentRecordingItem(missingPath, "00:07");
            var menuItem = new WpfMenuItem { Tag = recentRecording };

            InvokePrivateHandler(app, "ExportRecentRecordingGif_Click", menuItem, new RoutedEventArgs());

            messageBoxMock.Verify(service => service.ShowWarning(
                "The recording file could not be found.",
                "Export to GIF"), Times.Once);
            Assert.True(menuItem.IsEnabled);
        });
    }

    [Fact]
    public void ExportRecentRecordingGif_Click_WhenExportFails_ShowsWarningAndReenablesMenu()
    {
        StaTestHelper.RunAsync(async () =>
        {
            var app = CreateAppWithoutRunning();
            var messageBoxMock = new Mock<IMessageBoxService>();
            var userSettings = new UserSettings { GifFps = 12 };
            var userSettingsMock = new Mock<IUserSettingsService>();
            userSettingsMock.SetupGet(service => service.Current).Returns(userSettings);
            var gifExportMock = new Mock<IGifExportService>();
            gifExportMock
                .Setup(service => service.Export(It.IsAny<string>(), It.IsAny<string>(), 12, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("gif failed"));

            SetField(app, "_logger", NullLogger<App>.Instance);
            SetField(app, "_messageBox", messageBoxMock.Object);
            SetField(app, "_userSettings", userSettingsMock.Object);
            SetField(app, "_host", CreateHost(gifExportService: gifExportMock.Object));

            var tempMp4 = Path.GetTempFileName();
            var recentRecording = CreateRecentRecordingItem(tempMp4, "00:09");
            var menuItem = new WpfMenuItem { Tag = recentRecording };

            try
            {
                InvokePrivateHandler(app, "ExportRecentRecordingGif_Click", menuItem, new RoutedEventArgs());
                await WaitForCondition(() => menuItem.IsEnabled, TimeSpan.FromSeconds(3));

                messageBoxMock.Verify(service => service.ShowWarning(
                    "The GIF export failed. Please try again.",
                    "Export to GIF"), Times.Once);
            }
            finally
            {
                File.Delete(tempMp4);
            }
        });
    }

    [Fact]
    public void OpenPath_WhenFileMissing_ShowsWarning()
    {
        StaTestHelper.Run(() =>
        {
            var app = CreateAppWithoutRunning();
            var messageBoxMock = new Mock<IMessageBoxService>();
            SetField(app, "_messageBox", messageBoxMock.Object);
            SetField(app, "_host", CreateHost(processService: Mock.Of<IProcessService>()));

            InvokePrivateHandler(app, "OpenPath", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid()}.mp4"));

            messageBoxMock.Verify(service => service.ShowWarning(
                "The selected recording file could not be found.",
                "Open Recording"), Times.Once);
        });
    }

    [Fact]
    public void OpenPath_WhenFileExists_StartsProcessWithShellExecute()
    {
        StaTestHelper.Run(() =>
        {
            var app = CreateAppWithoutRunning();
            var processMock = new Mock<IProcessService>();
            SetField(app, "_messageBox", Mock.Of<IMessageBoxService>());
            SetField(app, "_host", CreateHost(processService: processMock.Object));

            var tempPath = Path.GetTempFileName();
            try
            {
                InvokePrivateHandler(app, "OpenPath", tempPath);

                processMock.Verify(process => process.Start(It.Is<ProcessStartInfo>(info =>
                    info.FileName == tempPath && info.UseShellExecute)), Times.Once);
            }
            finally
            {
                File.Delete(tempPath);
            }
        });
    }

    [Fact]
    public void OpenRecentRecording_Click_WithInvalidSender_DoesNothing()
    {
        StaTestHelper.Run(() =>
        {
            var app = CreateAppWithoutRunning();
            var processMock = new Mock<IProcessService>();
            SetField(app, "_messageBox", Mock.Of<IMessageBoxService>());
            SetField(app, "_host", CreateHost(processService: processMock.Object));

            InvokePrivateHandler(app, "OpenRecentRecording_Click", new object(), new RoutedEventArgs());

            processMock.Verify(process => process.Start(It.IsAny<ProcessStartInfo>()), Times.Never);
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
        IProcessService? processService = null,
        IGifExportService? gifExportService = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(updateService ?? Mock.Of<IUpdateService>());
        services.AddSingleton(Mock.Of<IAppVersionService>(version => version.Current == new Version(1, 2, 3)));
        services.AddSingleton(messageBox ?? Mock.Of<IMessageBoxService>());
        services.AddSingleton(processService ?? Mock.Of<IProcessService>());
        services.AddSingleton(gifExportService ?? Mock.Of<IGifExportService>());

        var provider = services.BuildServiceProvider();
        var host = new Mock<IHost>();
        host.SetupGet(current => current.Services).Returns(provider);
        return host.Object;
    }

    private static object CreateRecentRecordingItem(string outputPath, string elapsedText)
    {
        var recentRecordingType = typeof(App).GetNestedType("RecentRecordingItem", System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(recentRecordingType);
        return Activator.CreateInstance(recentRecordingType!, [outputPath, elapsedText])!;
    }

    private static object InvokePrivateStaticResult(string methodName, params object[] args)
    {
        var method = typeof(App).GetMethod(methodName, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.Invoke(null, args)!;
    }

    private static async Task WaitForCondition(Func<bool> condition, TimeSpan timeout)
    {
        var started = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - started > timeout)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }

            await Task.Delay(25);
        }
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
