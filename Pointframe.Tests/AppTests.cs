using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Pointframe.Models;
using Pointframe.Services;
using Pointframe.Services.Messaging;
using Pointframe.Tests.Services.Handlers;
using Pointframe.ViewModels;
using Xunit;

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
    public void ShowUpdateAvailable_StartupCheck_ShowsMessageAndTrayNotification()
    {
        StaTestHelper.Run(() =>
        {
            var app = CreateAppWithoutRunning();
            var trayIconManager = new Mock<ITrayIconManager>();
            var messageBox = new Mock<IMessageBoxService>();
            var update = new UpdateCheckResult(true, new Version(1, 2, 3), "https://example.com/download.exe");

            SetField(app, "_trayIconManager", trayIconManager.Object);
            SetField(app, "_messageBox", messageBox.Object);

            InvokeShowUpdateAvailable(app, new UpdateAvailableMessage(update, IsStartupCheck: true));

            trayIconManager.Verify(service => service.HandleUpdateAvailable(update), Times.Once);
            messageBox.Verify(
                service => service.ShowInformation(
                    It.Is<string>(text => text.Contains("1.2.3", StringComparison.Ordinal)),
                    "Update Available"),
                Times.Once);
        });
    }

    [Fact]
    public void ShowUpdateAvailable_PeriodicCheck_ShowsMessageAndTrayNotification()
    {
        StaTestHelper.Run(() =>
        {
            var app = CreateAppWithoutRunning();
            var trayIconManager = new Mock<ITrayIconManager>();
            var messageBox = new Mock<IMessageBoxService>();
            var update = new UpdateCheckResult(true, new Version(1, 2, 3), "https://example.com/download.exe");

            SetField(app, "_trayIconManager", trayIconManager.Object);
            SetField(app, "_messageBox", messageBox.Object);

            InvokeShowUpdateAvailable(app, new UpdateAvailableMessage(update, IsStartupCheck: false));

            trayIconManager.Verify(service => service.HandleUpdateAvailable(update), Times.Once);
            messageBox.Verify(
                service => service.ShowInformation(
                    It.Is<string>(text => text.Contains("1.2.3", StringComparison.Ordinal)),
                    "Update Available"),
                Times.Once);
        });
    }

    private static App CreateAppWithoutRunning()
    {
        return (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
    }

    private static void InvokeShowUpdateAvailable(App app, UpdateAvailableMessage message)
    {
        var method = typeof(App).GetMethod("ShowUpdateAvailable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        _ = method.Invoke(app, [message]);
    }

    private static void SetField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }
}
