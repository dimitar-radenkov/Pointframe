using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SnippingTool.Models;
using SnippingTool.Services;
using Xunit;

namespace SnippingTool.Tests.Services;

public sealed class AutoUpdateServiceTests
{
    private static readonly UpdateCheckResult NoUpdate =
        new(IsUpdateAvailable: false, LatestVersion: new Version(1, 0, 0), DownloadUrl: string.Empty);

    private static readonly UpdateCheckResult UpdateAvailable =
        new(IsUpdateAvailable: true, LatestVersion: new Version(9, 9, 0), DownloadUrl: "https://example.com/setup.exe");

    private static AutoUpdateService CreateService(
        Mock<IUpdateService> updateService,
        Mock<IUserSettingsService> userSettings,
        Mock<IUpdateDownloadService> downloadService,
        Mock<IMessageBoxService> messageBox)
    {
        return new AutoUpdateService(
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
        mock.SetupGet(s => s.Current).Returns(settings);
        mock.Setup(s => s.Save(It.IsAny<UserSettings>()));
        return mock;
    }

    [Fact]
    public async Task ExecuteAsync_NoUpdateAvailable_DoesNotRaiseEvent()
    {
        var updateService = new Mock<IUpdateService>();
        updateService
            .Setup(s => s.CheckForUpdatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(NoUpdate);

        var sut = CreateService(updateService, SettingsMock(), new Mock<IUpdateDownloadService>(), new Mock<IMessageBoxService>());

        UpdateCheckResult? raised = null;
        sut.UpdateAvailable += r => raised = r;

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50); // let ExecuteAsync run
        await sut.StopAsync(CancellationToken.None);

        Assert.Null(raised);
    }

    [Fact]
    public async Task ExecuteAsync_UpdateAvailable_RaisesEvent()
    {
        var updateService = new Mock<IUpdateService>();
        updateService
            .Setup(s => s.CheckForUpdatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateAvailable);

        var sut = CreateService(updateService, SettingsMock(), new Mock<IUpdateDownloadService>(), new Mock<IMessageBoxService>());

        UpdateCheckResult? raised = null;
        sut.UpdateAvailable += r => raised = r;

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        Assert.NotNull(raised);
        Assert.Equal(new Version(9, 9, 0), raised!.LatestVersion);
    }

    [Fact]
    public async Task ExecuteAsync_SavesLastCheckTimestamp()
    {
        var updateService = new Mock<IUpdateService>();
        updateService
            .Setup(s => s.CheckForUpdatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(NoUpdate);

        var settingsMock = SettingsMock();

        var sut = CreateService(updateService, settingsMock, new Mock<IUpdateDownloadService>(), new Mock<IMessageBoxService>());

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        settingsMock.Verify(s => s.Save(It.IsAny<UserSettings>()), Times.AtLeastOnce);
        Assert.NotNull(settingsMock.Object.Current.LastAutoUpdateCheckUtc);
    }

    [Fact]
    public async Task ExecuteAsync_CheckFails_DoesNotThrow()
    {
        var updateService = new Mock<IUpdateService>();
        updateService
            .Setup(s => s.CheckForUpdatesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network error"));

        var sut = CreateService(updateService, SettingsMock(), new Mock<IUpdateDownloadService>(), new Mock<IMessageBoxService>());

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

        var sut = CreateService(updateService, SettingsMock(), downloadService, messageBox);

        await sut.ConfirmAndInstallAsync(UpdateAvailable);

        downloadService.Verify(d => d.ShowAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmAndInstall_UserConfirms_CallsDownload()
    {
        var updateService = new Mock<IUpdateService>();
        var downloadService = new Mock<IUpdateDownloadService>();
        downloadService.Setup(d => d.ShowAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
        var messageBox = new Mock<IMessageBoxService>();
        messageBox.Setup(m => m.Confirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        var sut = CreateService(updateService, SettingsMock(), downloadService, messageBox);

        await sut.ConfirmAndInstallAsync(UpdateAvailable);

        downloadService.Verify(
            d => d.ShowAsync(
                UpdateAvailable.DownloadUrl,
                It.Is<string>(p => p.Contains("SnippingTool-Setup-9.9.0.exe"))),
            Times.Once);
    }

    [Fact]
    public async Task ConfirmAndInstall_ShowsCorrectVersionInPrompt()
    {
        var updateService = new Mock<IUpdateService>();
        var downloadService = new Mock<IUpdateDownloadService>();
        var messageBox = new Mock<IMessageBoxService>();
        messageBox.Setup(m => m.Confirm(It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        var sut = CreateService(updateService, SettingsMock(), downloadService, messageBox);

        await sut.ConfirmAndInstallAsync(UpdateAvailable);

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
            .Setup(s => s.CheckForUpdatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(NoUpdate);

        var sut = CreateService(updateService, SettingsMock(UpdateCheckInterval.Never), new Mock<IUpdateDownloadService>(), new Mock<IMessageBoxService>());

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        // Only the startup check — no periodic ticks
        updateService.Verify(s => s.CheckForUpdatesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
