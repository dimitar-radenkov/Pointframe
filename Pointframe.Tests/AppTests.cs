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
    public void AddPointframeAppServices_RegistersCoreServicesAndFactories()
    {
        var services = new ServiceCollection();
        // The production host registers logging and configuration; mirror that here.
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        services.AddPointframeAppServices();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<DialogService>(provider.GetRequiredService<IDialogService>());
        Assert.IsType<MessageBoxService>(provider.GetRequiredService<IMessageBoxService>());
        Assert.IsType<TrayIconManager>(provider.GetRequiredService<ITrayIconManager>());
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
    public void HandleUpdateAvailable_StartupCheck_TracksTelemetry()
    {
        StaTestHelper.Run(() =>
        {
            var app = CreateAppWithoutRunning();
            var telemetry = new Mock<ITelemetryService>();
            var trayIconManager = new Mock<ITrayIconManager>();
            var update = new UpdateCheckResult(true, new Version(1, 2, 3), "https://example.com/download.exe");

            SetField(app, "_telemetry", telemetry.Object);
            SetField(app, "_trayIconManager", trayIconManager.Object);

            InvokeHandleUpdateAvailable(app, new UpdateAvailableMessage(update, IsStartupCheck: true));

            trayIconManager.Verify(manager => manager.HandleUpdateAvailable(update), Times.Once);
            telemetry.Verify(
                service => service.TrackEvent(
                    "update_available",
                    It.Is<IReadOnlyDictionary<string, string>?>(props =>
                        props != null
                        && props.ContainsKey("version")
                        && props["version"] == "1.2.3")),
                Times.Once);
        });
    }

    [Fact]
    public void HandleUpdateAvailable_PeriodicCheck_TracksTelemetry()
    {
        StaTestHelper.Run(() =>
        {
            var app = CreateAppWithoutRunning();
            var telemetry = new Mock<ITelemetryService>();
            var trayIconManager = new Mock<ITrayIconManager>();
            var update = new UpdateCheckResult(true, new Version(1, 2, 3), "https://example.com/download.exe");

            SetField(app, "_telemetry", telemetry.Object);
            SetField(app, "_trayIconManager", trayIconManager.Object);

            InvokeHandleUpdateAvailable(app, new UpdateAvailableMessage(update, IsStartupCheck: false));

            trayIconManager.Verify(manager => manager.HandleUpdateAvailable(update), Times.Once);
            telemetry.Verify(
                service => service.TrackEvent(
                    "update_available",
                    It.Is<IReadOnlyDictionary<string, string>?>(props =>
                        props != null
                        && props.ContainsKey("version")
                        && props["version"] == "1.2.3")),
                Times.Once);
        });
    }

    [Fact]
    public void HandleCaptureCompleted_WithOutputPath_ForwardsToTrayAndActivationTelemetry()
    {
        StaTestHelper.Run(() =>
        {
            var app = CreateAppWithoutRunning();
            var trayIconManager = new Mock<ITrayIconManager>();
            var activationTelemetry = new Mock<IActivationTelemetryService>();

            SetField(app, "_trayIconManager", trayIconManager.Object);
            SetField(app, "_activationTelemetry", activationTelemetry.Object);

            InvokeHandleCaptureCompleted(app, new CaptureCompletedMessage(@"C:\\captures\\shot.png", "save"));

            trayIconManager.Verify(manager => manager.HandleCaptureCompleted(@"C:\\captures\\shot.png"), Times.Once);
            activationTelemetry.Verify(service => service.TrackCaptureCompleted("save"), Times.Once);
        });
    }

    [Fact]
    public void HandleCaptureCompleted_WithoutOutputPath_TracksActivationTelemetryOnly()
    {
        StaTestHelper.Run(() =>
        {
            var app = CreateAppWithoutRunning();
            var trayIconManager = new Mock<ITrayIconManager>();
            var activationTelemetry = new Mock<IActivationTelemetryService>();

            SetField(app, "_trayIconManager", trayIconManager.Object);
            SetField(app, "_activationTelemetry", activationTelemetry.Object);

            InvokeHandleCaptureCompleted(app, new CaptureCompletedMessage(null, "copy"));

            trayIconManager.Verify(manager => manager.HandleCaptureCompleted(It.IsAny<string>()), Times.Never);
            activationTelemetry.Verify(service => service.TrackCaptureCompleted("copy"), Times.Once);
        });
    }

    private static App CreateAppWithoutRunning()
    {
        return (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
    }

    private static void InvokeHandleUpdateAvailable(App app, UpdateAvailableMessage message)
    {
        var method = typeof(App).GetMethod("HandleUpdateAvailable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method.Invoke(app, [message]);
        if (result is ValueTask task)
        {
            task.GetAwaiter().GetResult();
        }
    }

    private static void InvokeHandleCaptureCompleted(App app, CaptureCompletedMessage message)
    {
        var method = typeof(App).GetMethod("HandleCaptureCompleted", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method.Invoke(app, [message]);
        if (result is ValueTask task)
        {
            task.GetAwaiter().GetResult();
        }
    }

    private static void SetField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }
}
