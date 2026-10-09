using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Pointframe.Engine;
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
        Assert.NotNull(provider.GetRequiredService<Func<CancellationTokenSource, ScrollingCaptureProgressViewModel>>());
        Assert.NotNull(provider.GetRequiredService<Func<ScrollingCaptureProgressViewModel, Int32Rect, ScrollingCaptureProgressWindow>>());
        Assert.IsType<CaptureIndexWorker>(provider.GetRequiredService<ICaptureIndexWorker>());
        Assert.IsType<ScrollingCaptureService>(provider.GetRequiredService<IScrollingCaptureService>());
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
            SetField(app, "_userSettings", SettingsMock());

            InvokeHandleUpdateAvailable(app, new UpdateAvailableMessage(update, IsStartupCheck: true));

            trayIconManager.Verify(manager => manager.HandleUpdateAvailable(update), Times.Once);
            telemetry.Verify(
                service => service.TrackEvent(
                    "update_available",
                    It.Is<IReadOnlyDictionary<string, string>?>(props =>
                        props != null
                        && props.ContainsKey("from_version")
                        && props["from_version"] == "unknown"
                        && props.ContainsKey("target_version")
                        && props["target_version"] == "1.2.3"
                        && !props.ContainsKey("version"))),
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
            SetField(app, "_userSettings", SettingsMock());

            InvokeHandleUpdateAvailable(app, new UpdateAvailableMessage(update, IsStartupCheck: false));

            trayIconManager.Verify(manager => manager.HandleUpdateAvailable(update), Times.Once);
            telemetry.Verify(
                service => service.TrackEvent(
                    "update_available",
                    It.Is<IReadOnlyDictionary<string, string>?>(props =>
                        props != null
                        && props.ContainsKey("from_version")
                        && props["from_version"] == "unknown"
                        && props.ContainsKey("target_version")
                        && props["target_version"] == "1.2.3"
                        && !props.ContainsKey("version"))),
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

    [Fact]
    public void TrackAppliedUpdate_EmitsOnceWhenCurrentVersionIsNewer()
    {
        var app = CreateAppWithoutRunning();
        var settings = new UserSettings { LastRunVersion = "1.2.3" };
        var settingsService = new Mock<IUserSettingsService>();
        settingsService.SetupGet(service => service.Current).Returns(() => settings);
        settingsService.Setup(service => service.Update(It.IsAny<Action<UserSettings>>()))
            .Callback<Action<UserSettings>>(update => update(settings));
        var telemetry = new Mock<ITelemetryService>();
        var serviceProvider = new ServiceCollection()
            .AddSingleton(Mock.Of<IAppVersionService>(service => service.Current == new Version(1, 2, 4)))
            .BuildServiceProvider();
        var host = Host.CreateDefaultBuilder().ConfigureServices(services => services.AddSingleton<IAppVersionService>(serviceProvider.GetRequiredService<IAppVersionService>())).Build();

        SetField(app, "_host", host);
        SetField(app, "_userSettings", settingsService.Object);
        SetField(app, "_telemetry", telemetry.Object);
        var method = typeof(App).GetMethod("TrackAppliedUpdate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        method.Invoke(app, null);
        method.Invoke(app, null);

        telemetry.Verify(service => service.TrackEvent(
            "update_applied",
            It.Is<IReadOnlyDictionary<string, string>?>(properties =>
                properties != null && properties["from_version"] == "1.2.3" && properties["target_version"] == "1.2.4")), Times.Once);
        Assert.Equal("1.2.4", settings.LastRunVersion);
        host.Dispose();
        serviceProvider.Dispose();
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

    private static IUserSettingsService SettingsMock()
    {
        var settings = new UserSettings();
        var mock = new Mock<IUserSettingsService>();
        mock.SetupGet(service => service.Current).Returns(() => settings);
        mock.Setup(service => service.Update(It.IsAny<Action<UserSettings>>()))
            .Callback<Action<UserSettings>>(update => update(settings));
        return mock.Object;
    }
}
