using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pointframe.Models;
using Pointframe.Services;
using Pointframe.Services.Messaging;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class AutoUpdateServiceTests
{
    private static readonly UpdateCheckResult NoUpdate =
        new(IsUpdateAvailable: false, LatestVersion: new Version(1, 0, 0), DownloadUrl: string.Empty);

    private static readonly UpdateCheckResult UpdateAvailable =
        new(IsUpdateAvailable: true, LatestVersion: new Version(9, 9, 0), DownloadUrl: "https://example.com/setup.exe");

    private static AutoUpdateService CreateService(
        IEventAggregator eventAggregator,
        Mock<IUpdateService> updateService,
        Mock<IUserSettingsService> userSettings,
        Mock<IUpdateDownloadService> downloadService,
        Mock<IMessageBoxService> messageBox)
    {
        return new AutoUpdateService(
            eventAggregator,
            updateService.Object,
            userSettings.Object,
            downloadService.Object,
            messageBox.Object,
            NullLogger<AutoUpdateService>.Instance);
    }

    private static Mock<IUserSettingsService> SettingsMock(UpdateCheckInterval interval = UpdateCheckInterval.Never)
    {
        var settings = new UserSettings { AutoUpdateCheckInterval = interval };
        var mock = new Mock<IUserSettingsService>();
        mock.SetupGet(s => s.Current).Returns(() => settings);
        mock.Setup(s => s.Save(It.IsAny<UserSettings>()));
        mock.Setup(s => s.Update(It.IsAny<Action<UserSettings>>()))
            .Callback<Action<UserSettings>>(mutate => mutate(settings));
        return mock;
    }

    [Fact]
    public async Task ExecuteAsync_NoUpdateAvailable_DoesNotPublishMessage()
    {
        var eventAggregator = new DefaultEventAggregator(NullLogger<DefaultEventAggregator>.Instance);
        var updateService = new Mock<IUpdateService>();
        updateService
            .Setup(s => s.CheckForUpdates(It.IsAny<CancellationToken>()))
            .ReturnsAsync(NoUpdate);

        var sut = CreateService(eventAggregator, updateService, SettingsMock(), new Mock<IUpdateDownloadService>(), new Mock<IMessageBoxService>());
        var recorder = new UpdateAvailableRecorder();
        using var subscription = eventAggregator.Subscribe<UpdateAvailableMessage>(recorder.HandleAsync);

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        Assert.Null(recorder.Message);
    }

    [Fact]
    public async Task ExecuteAsync_UpdateAvailable_PublishesMessage()
    {
        var eventAggregator = new DefaultEventAggregator(NullLogger<DefaultEventAggregator>.Instance);
        var updateService = new Mock<IUpdateService>();
        updateService
            .Setup(s => s.CheckForUpdates(It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateAvailable);

        var sut = CreateService(eventAggregator, updateService, SettingsMock(), new Mock<IUpdateDownloadService>(), new Mock<IMessageBoxService>());
        var recorder = new UpdateAvailableRecorder();
        using var subscription = eventAggregator.Subscribe<UpdateAvailableMessage>(recorder.HandleAsync);

        await sut.StartAsync(CancellationToken.None);
        var message = await recorder.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);

        Assert.Equal(new Version(9, 9, 0), message.Result.LatestVersion);
    }

    [Fact]
    public async Task ExecuteAsync_UpdatesLastCheckTimestamp()
    {
        var updateService = new Mock<IUpdateService>();
        updateService
            .Setup(s => s.CheckForUpdates(It.IsAny<CancellationToken>()))
            .ReturnsAsync(NoUpdate);

        var settingsMock = SettingsMock();
        var saveCalled = new TaskCompletionSource();
        settingsMock
            .Setup(s => s.Update(It.IsAny<Action<UserSettings>>()))
            .Callback<Action<UserSettings>>(mutate =>
            {
                mutate(settingsMock.Object.Current);
                saveCalled.TrySetResult();
            });

        var sut = CreateService(new DefaultEventAggregator(NullLogger<DefaultEventAggregator>.Instance), updateService, settingsMock, new Mock<IUpdateDownloadService>(), new Mock<IMessageBoxService>());

        await sut.StartAsync(CancellationToken.None);
        await saveCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);

        settingsMock.Verify(
            s => s.Update(It.IsAny<Action<UserSettings>>()),
            Times.AtLeastOnce);
        Assert.NotNull(settingsMock.Object.Current.LastAutoUpdateCheckUtc);
        settingsMock.Verify(s => s.Save(It.IsAny<UserSettings>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_CheckFails_DoesNotThrow()
    {
        var updateService = new Mock<IUpdateService>();
        updateService
            .Setup(s => s.CheckForUpdates(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network error"));

        var sut = CreateService(new DefaultEventAggregator(NullLogger<DefaultEventAggregator>.Instance), updateService, SettingsMock(), new Mock<IUpdateDownloadService>(), new Mock<IMessageBoxService>());

        var ex = await Record.ExceptionAsync(async () =>
        {
            await sut.StartAsync(CancellationToken.None);
            await Task.Delay(50);
            await sut.StopAsync(CancellationToken.None);
        });

        Assert.Null(ex);
    }

    [Fact]
    public async Task ConfirmAndInstall_UserDeclines_DoesNotDownload()
    {
        var updateService = new Mock<IUpdateService>();
        var downloadService = new Mock<IUpdateDownloadService>();
        var messageBox = new Mock<IMessageBoxService>();
        messageBox.Setup(m => m.Confirm(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        var sut = CreateService(new DefaultEventAggregator(NullLogger<DefaultEventAggregator>.Instance), updateService, SettingsMock(), downloadService, messageBox);

        await sut.ConfirmAndInstall(UpdateAvailable);

        downloadService.Verify(d => d.Show(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmAndInstall_UserConfirms_CallsDownload()
    {
        var updateService = new Mock<IUpdateService>();
        var downloadService = new Mock<IUpdateDownloadService>();
        downloadService.Setup(d => d.Show(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
        var messageBox = new Mock<IMessageBoxService>();
        messageBox.Setup(m => m.Confirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        var sut = CreateService(new DefaultEventAggregator(NullLogger<DefaultEventAggregator>.Instance), updateService, SettingsMock(), downloadService, messageBox);

        await sut.ConfirmAndInstall(UpdateAvailable);

        downloadService.Verify(
            d => d.Show(
                UpdateAvailable.DownloadUrl,
                It.Is<string>(p => p.Contains("setup.exe"))),
            Times.Once);
    }

    [Fact]
    public async Task ConfirmAndInstall_UsesInstallerNameFromDownloadUrl()
    {
        var updateService = new Mock<IUpdateService>();
        var downloadService = new Mock<IUpdateDownloadService>();
        downloadService.Setup(d => d.Show(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
        var messageBox = new Mock<IMessageBoxService>();
        messageBox.Setup(m => m.Confirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        var update = new UpdateCheckResult(true, new Version(9, 9, 0), "https://github.com/dimitar-radenkov/Pointframe/releases/download/v9.9.0/Pointframe-9.9.0-Setup.exe");

        var sut = CreateService(new DefaultEventAggregator(NullLogger<DefaultEventAggregator>.Instance), updateService, SettingsMock(), downloadService, messageBox);

        await sut.ConfirmAndInstall(update);

        downloadService.Verify(
            d => d.Show(
                update.DownloadUrl,
                It.Is<string>(p => p.Contains("Pointframe-9.9.0-Setup.exe"))),
            Times.Once);
    }

    [Fact]
    public async Task ConfirmAndInstall_WithoutExeFileName_UsesPointframeFallbackPattern()
    {
        var updateService = new Mock<IUpdateService>();
        var downloadService = new Mock<IUpdateDownloadService>();
        downloadService.Setup(d => d.Show(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
        var messageBox = new Mock<IMessageBoxService>();
        messageBox.Setup(m => m.Confirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        var update = new UpdateCheckResult(true, new Version(9, 9, 0), "https://github.com/dimitar-radenkov/Pointframe/releases/download/v9.9.0/download");

        var sut = CreateService(new DefaultEventAggregator(NullLogger<DefaultEventAggregator>.Instance), updateService, SettingsMock(), downloadService, messageBox);

        await sut.ConfirmAndInstall(update);

        downloadService.Verify(
            d => d.Show(
                update.DownloadUrl,
                It.Is<string>(p => p.Contains("Pointframe-9.9.0-Setup.exe"))),
            Times.Once);
    }

    [Fact]
    public async Task ConfirmAndInstall_DownloadSucceeds_DoesNotThrow()
    {
        var updateService = new Mock<IUpdateService>();
        var downloadService = new Mock<IUpdateDownloadService>();
        downloadService.Setup(d => d.Show(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        var messageBox = new Mock<IMessageBoxService>();
        messageBox.Setup(m => m.Confirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        var sut = CreateService(new DefaultEventAggregator(NullLogger<DefaultEventAggregator>.Instance), updateService, SettingsMock(), downloadService, messageBox);

        var ex = await Record.ExceptionAsync(() => sut.ConfirmAndInstall(UpdateAvailable));

        Assert.Null(ex);
        downloadService.Verify(d => d.Show(UpdateAvailable.DownloadUrl, It.IsAny<string>()), Times.Once);
    }

    [Theory]
    [InlineData(UpdateCheckInterval.EveryDay, 1)]
    [InlineData(UpdateCheckInterval.EveryTwoDays, 2)]
    [InlineData(UpdateCheckInterval.EveryThreeDays, 3)]
    public void GetTimerInterval_MapsIntervalsToExpectedDays(UpdateCheckInterval interval, int expectedDays)
    {
        var method = typeof(AutoUpdateService).GetMethod("GetTimerInterval", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var result = (TimeSpan)method.Invoke(null, [interval])!;

        Assert.Equal(TimeSpan.FromDays(expectedDays), result);
    }

    [Fact]
    public async Task ConfirmAndInstall_ShowsCorrectVersionInPrompt()
    {
        var updateService = new Mock<IUpdateService>();
        var downloadService = new Mock<IUpdateDownloadService>();
        var messageBox = new Mock<IMessageBoxService>();
        messageBox.Setup(m => m.Confirm(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        var sut = CreateService(new DefaultEventAggregator(NullLogger<DefaultEventAggregator>.Instance), updateService, SettingsMock(), downloadService, messageBox);

        await sut.ConfirmAndInstall(UpdateAvailable);

        messageBox.Verify(
            m => m.Confirm(
                It.Is<string>(msg => msg.Contains("9.9.0")),
                It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_IntervalNever_DoesNotStartPeriodicLoop()
    {
        var updateService = new Mock<IUpdateService>();
        updateService
            .Setup(s => s.CheckForUpdates(It.IsAny<CancellationToken>()))
            .ReturnsAsync(NoUpdate);

        var sut = CreateService(new DefaultEventAggregator(NullLogger<DefaultEventAggregator>.Instance), updateService, SettingsMock(UpdateCheckInterval.Never), new Mock<IUpdateDownloadService>(), new Mock<IMessageBoxService>());

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        // Only the startup check — no periodic ticks
        updateService.Verify(s => s.CheckForUpdates(It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class UpdateAvailableRecorder
    {
        public TaskCompletionSource<UpdateAvailableMessage> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public UpdateAvailableMessage? Message { get; private set; }

        public ValueTask HandleAsync(UpdateAvailableMessage message)
        {
            Message = message;
            Received.TrySetResult(message);
            return ValueTask.CompletedTask;
        }
    }
}
