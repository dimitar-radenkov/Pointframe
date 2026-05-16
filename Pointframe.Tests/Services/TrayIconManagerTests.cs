using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pointframe.Models;
using Pointframe.Services;
using Pointframe.Tests.Services.Handlers;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class TrayIconManagerTests
{
    [Fact]
    public void HandleUpdateAvailable_ThenOnTrayBalloonClicked_InstallsUpdate()
    {
        StaTestHelper.Run(() =>
        {
            var autoUpdateMock = new Mock<IAutoUpdateService>();
            autoUpdateMock.Setup(service => service.ConfirmAndInstall(It.IsAny<UpdateCheckResult>()))
                .Returns(Task.CompletedTask);
            var manager = CreateManager(autoUpdate: autoUpdateMock.Object);
            var update = new UpdateCheckResult(true, new Version(1, 2, 3), "https://example.com/download");

            manager.HandleUpdateAvailable(update);
            var pending = (UpdateCheckResult?)GetField(manager, "_pendingUpdate");
            Assert.Same(update, pending);

            InvokePrivate(manager, "OnTrayBalloonClicked", manager, new RoutedEventArgs());

            autoUpdateMock.Verify(service => service.ConfirmAndInstall(update), Times.Once);
            Assert.Null(GetField(manager, "_pendingUpdate"));
        });
    }

    [Fact]
    public void CheckForUpdates_Click_WhenAlreadyUpToDate_ShowsInformation()
    {
        StaTestHelper.RunAsync(async () =>
        {
            var updateService = new Mock<IUpdateService>();
            updateService
                .Setup(service => service.CheckForUpdates())
                .ReturnsAsync(new UpdateCheckResult(false, new Version(1, 2, 3), string.Empty));

            var messageBox = new Mock<IMessageBoxService>();
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            messageBox
                .Setup(service => service.ShowInformation(It.IsAny<string>(), "Check for Updates"))
                .Callback(() => shown.TrySetResult());

            var manager = CreateManager(
                updateService: updateService.Object,
                appVersionService: Mock.Of<IAppVersionService>(v => v.Current == new Version(1, 2, 3)),
                messageBox: messageBox.Object,
                autoUpdate: Mock.Of<IAutoUpdateService>());

            var menuItem = new System.Windows.Controls.MenuItem();
            InvokePrivate(manager, "CheckForUpdates_Click", menuItem, new RoutedEventArgs());

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
            var updateService = new Mock<IUpdateService>();
            updateService
                .Setup(service => service.CheckForUpdates())
                .ThrowsAsync(new InvalidOperationException("boom"));

            var messageBox = new Mock<IMessageBoxService>();
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            messageBox
                .Setup(service => service.ShowWarning(It.IsAny<string>(), "Check for Updates"))
                .Callback(() => shown.TrySetResult());

            var manager = CreateManager(
                updateService: updateService.Object,
                messageBox: messageBox.Object,
                autoUpdate: Mock.Of<IAutoUpdateService>());

            var menuItem = new System.Windows.Controls.MenuItem();
            InvokePrivate(manager, "CheckForUpdates_Click", menuItem, new RoutedEventArgs());

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
            var autoUpdateMock = new Mock<IAutoUpdateService>();
            var manager = CreateManager(autoUpdate: autoUpdateMock.Object);

            InvokePrivate(manager, "OnTrayBalloonClicked", manager, new RoutedEventArgs());

            autoUpdateMock.Verify(service => service.ConfirmAndInstall(It.IsAny<UpdateCheckResult>()), Times.Never);
        });
    }

    [Fact]
    public void OnTrayBalloonClicked_WithPendingRecording_OpensSavedRecording()
    {
        StaTestHelper.Run(() =>
        {
            var processMock = new Mock<IProcessService>();
            var messageBoxMock = new Mock<IMessageBoxService>();
            var tempFilePath = Path.GetTempFileName();
            var manager = CreateManager(processService: processMock.Object, messageBox: messageBoxMock.Object);
            SetField(manager, "_pendingRecordingBalloonPath", tempFilePath);

            try
            {
                InvokePrivate(manager, "OnTrayBalloonClicked", manager, new RoutedEventArgs());

                processMock.Verify(
                    process => process.Start(It.Is<ProcessStartInfo>(info => info.FileName == tempFilePath && info.UseShellExecute)),
                    Times.Once);
                Assert.Null(GetField(manager, "_pendingRecordingBalloonPath"));
            }
            finally
            {
                File.Delete(tempFilePath);
            }
        });
    }

    [Fact]
    public void CreateTrayMenuItem_AssignsHeaderAndClickHandler()
    {
        StaTestHelper.Run(() =>
        {
            var clicked = false;
            var menuItem = TrayIconManager.CreateTrayMenuItem(
                "Test Item",
                (_, _) => clicked = true);

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
            var processMock = new Mock<IProcessService>();
            var manager = CreateManager(processService: processMock.Object);

            var recentRecording = CreateRecentRecordingItem(@"C:\\temp\\video.mp4", "00:10");
            var menuItem = new System.Windows.Controls.MenuItem { Tag = recentRecording };

            InvokePrivate(manager, "OpenRecentRecordingFolder_Click", menuItem, new RoutedEventArgs());

            processMock.Verify(process => process.Start(It.Is<ProcessStartInfo>(info =>
                info.FileName == "explorer.exe" && info.Arguments == "\"C:\\temp\"")), Times.Once);
        });
    }

    [Fact]
    public void ExportRecentRecordingGif_Click_WhenRecordingMissing_ShowsWarning()
    {
        StaTestHelper.Run(() =>
        {
            var messageBoxMock = new Mock<IMessageBoxService>();
            var manager = CreateManager(
                messageBox: messageBoxMock.Object,
                gifExportService: Mock.Of<IGifExportService>());

            var missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid()}.mp4");
            var recentRecording = CreateRecentRecordingItem(missingPath, "00:07");
            var menuItem = new System.Windows.Controls.MenuItem { Tag = recentRecording };

            InvokePrivate(manager, "ExportRecentRecordingGif_Click", menuItem, new RoutedEventArgs());

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
            var messageBoxMock = new Mock<IMessageBoxService>();
            var userSettings = new UserSettings { GifFps = 12 };
            var userSettingsMock = new Mock<IUserSettingsService>();
            userSettingsMock.SetupGet(service => service.Current).Returns(userSettings);
            var gifExportMock = new Mock<IGifExportService>();
            gifExportMock
                .Setup(service => service.Export(It.IsAny<string>(), It.IsAny<string>(), 12, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("gif failed"));

            var manager = CreateManager(
                messageBox: messageBoxMock.Object,
                userSettings: userSettingsMock.Object,
                gifExportService: gifExportMock.Object);

            var tempMp4 = Path.GetTempFileName();
            var recentRecording = CreateRecentRecordingItem(tempMp4, "00:09");
            var menuItem = new System.Windows.Controls.MenuItem { Tag = recentRecording };

            try
            {
                InvokePrivate(manager, "ExportRecentRecordingGif_Click", menuItem, new RoutedEventArgs());
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
            var messageBoxMock = new Mock<IMessageBoxService>();
            var manager = CreateManager(
                messageBox: messageBoxMock.Object,
                processService: Mock.Of<IProcessService>());

            InvokePrivate(manager, "OpenPath", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid()}.mp4"));

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
            var processMock = new Mock<IProcessService>();
            var manager = CreateManager(
                messageBox: Mock.Of<IMessageBoxService>(),
                processService: processMock.Object);

            var tempPath = Path.GetTempFileName();
            try
            {
                InvokePrivate(manager, "OpenPath", tempPath);

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
    public void OpenLogsFolder_Click_OpensLocalLogsFolder()
    {
        StaTestHelper.Run(() =>
        {
            var processMock = new Mock<IProcessService>();
            var manager = CreateManager(processService: processMock.Object);

            InvokePrivate(manager, "OpenLogsFolder_Click", new object(), new RoutedEventArgs());

            processMock.Verify(process => process.Start(It.Is<ProcessStartInfo>(info =>
                info.FileName == "explorer.exe" && info.Arguments == $"\"{AppPaths.LogsDirectory}\"")), Times.Once);
        });
    }

    [Fact]
    public void OpenSnipsFolder_Click_UsesConfiguredScreenshotPath()
    {
        StaTestHelper.Run(() =>
        {
            var processMock = new Mock<IProcessService>();
            var userSettings = new UserSettings
            {
                ScreenshotSavePath = Path.Combine(Path.GetTempPath(), $"pointframe-snips-{Guid.NewGuid():N}"),
            };
            var userSettingsMock = new Mock<IUserSettingsService>();
            userSettingsMock.SetupGet(service => service.Current).Returns(userSettings);
            var manager = CreateManager(processService: processMock.Object, userSettings: userSettingsMock.Object);

            try
            {
                InvokePrivate(manager, "OpenSnipsFolder_Click", new object(), new RoutedEventArgs());

                processMock.Verify(process => process.Start(It.Is<ProcessStartInfo>(info =>
                    info.FileName == "explorer.exe" && info.Arguments == $"\"{userSettings.ScreenshotSavePath}\"")), Times.Once);
            }
            finally
            {
                if (Directory.Exists(userSettings.ScreenshotSavePath))
                {
                    Directory.Delete(userSettings.ScreenshotSavePath);
                }
            }
        });
    }

    [Fact]
    public void OpenVideosFolder_Click_UsesConfiguredRecordingPath()
    {
        StaTestHelper.Run(() =>
        {
            var processMock = new Mock<IProcessService>();
            var userSettings = new UserSettings
            {
                RecordingOutputPath = Path.Combine(Path.GetTempPath(), $"pointframe-videos-{Guid.NewGuid():N}"),
            };
            var userSettingsMock = new Mock<IUserSettingsService>();
            userSettingsMock.SetupGet(service => service.Current).Returns(userSettings);
            var manager = CreateManager(processService: processMock.Object, userSettings: userSettingsMock.Object);

            try
            {
                InvokePrivate(manager, "OpenVideosFolder_Click", new object(), new RoutedEventArgs());

                processMock.Verify(process => process.Start(It.Is<ProcessStartInfo>(info =>
                    info.FileName == "explorer.exe" && info.Arguments == $"\"{userSettings.RecordingOutputPath}\"")), Times.Once);
            }
            finally
            {
                if (Directory.Exists(userSettings.RecordingOutputPath))
                {
                    Directory.Delete(userSettings.RecordingOutputPath);
                }
            }
        });
    }

    [Fact]
    public void OpenRecentRecording_Click_WithInvalidSender_DoesNothing()
    {
        StaTestHelper.Run(() =>
        {
            var processMock = new Mock<IProcessService>();
            var manager = CreateManager(
                messageBox: Mock.Of<IMessageBoxService>(),
                processService: processMock.Object);

            InvokePrivate(manager, "OpenRecentRecording_Click", new object(), new RoutedEventArgs());

            processMock.Verify(process => process.Start(It.IsAny<ProcessStartInfo>()), Times.Never);
        });
    }

    [Fact]
    public void ClearRecentCaptures_Click_EmptiesCaptures()
    {
        StaTestHelper.Run(() =>
        {
            var manager = CreateManager();
            var captures = (List<string>)GetField(manager, "_recentCaptures")!;
            captures.Add("C:\\test1.png");
            captures.Add("C:\\test2.png");

            InvokePrivate(manager, "ClearRecentCaptures_Click", new object(), new RoutedEventArgs());

            Assert.Empty(captures);
        });
    }

    [Fact]
    public void ClearRecentRecordings_Click_EmptiesRecordings()
    {
        StaTestHelper.Run(() =>
        {
            var manager = CreateManager();
            var recordingsField = manager.GetType().GetField("_recentRecordings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(recordingsField);
            var recordings = (System.Collections.IList)recordingsField.GetValue(manager)!;
            
            var rec1 = CreateRecentRecordingItem("C:\\test1.mp4", "00:05");
            var rec2 = CreateRecentRecordingItem("C:\\test2.mp4", "00:10");
            recordings.Add(rec1);
            recordings.Add(rec2);

            InvokePrivate(manager, "ClearRecentRecordings_Click", new object(), new RoutedEventArgs());

            Assert.Empty(recordings);
        });
    }

    [Fact]
    public void RebuildRecentCapturesMenu_WhenEmpty_ShowsOpenSnipsFolderShortcut()
    {
        StaTestHelper.Run(() =>
        {
            var manager = CreateManager();
            var menuItem = new System.Windows.Controls.MenuItem();
            SetField(manager, "_recentCapturesMenuItem", menuItem);

            InvokePrivate(manager, "RebuildRecentCapturesMenu");

            Assert.True(HasMenuItemHeader(menuItem, "No recent captures"));
            Assert.True(HasMenuItemHeader(menuItem, "Open Snips folder"));
        });
    }

    [Fact]
    public void RebuildRecentCapturesMenu_WhenNotEmpty_HidesOpenSnipsFolderShortcut()
    {
        StaTestHelper.Run(() =>
        {
            var manager = CreateManager();
            var menuItem = new System.Windows.Controls.MenuItem();
            SetField(manager, "_recentCapturesMenuItem", menuItem);

            var captures = (List<string>)GetField(manager, "_recentCaptures")!;
            captures.Add(@"C:\temp\snip-1.png");

            InvokePrivate(manager, "RebuildRecentCapturesMenu");

            Assert.False(HasMenuItemHeader(menuItem, "Open Snips folder"));
            Assert.True(HasMenuItemHeader(menuItem, "Clear recent captures"));
        });
    }

    [Fact]
    public void RebuildRecentRecordingsMenu_WhenEmpty_ShowsOpenVideosFolderShortcut()
    {
        StaTestHelper.Run(() =>
        {
            var manager = CreateManager();
            var menuItem = new System.Windows.Controls.MenuItem();
            SetField(manager, "_recentRecordingsMenuItem", menuItem);

            InvokePrivate(manager, "RebuildRecentRecordingsMenu");

            Assert.True(HasMenuItemHeader(menuItem, "No recent recordings"));
            Assert.True(HasMenuItemHeader(menuItem, "Open Videos folder"));
        });
    }

    [Fact]
    public void RebuildRecentRecordingsMenu_WhenNotEmpty_HidesOpenVideosFolderShortcut()
    {
        StaTestHelper.Run(() =>
        {
            var manager = CreateManager();
            var menuItem = new System.Windows.Controls.MenuItem();
            SetField(manager, "_recentRecordingsMenuItem", menuItem);

            var recordingsField = manager.GetType().GetField("_recentRecordings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(recordingsField);
            var recordings = (System.Collections.IList)recordingsField.GetValue(manager)!;
            recordings.Add(CreateRecentRecordingItem(@"C:\temp\rec-1.mp4", "00:06"));

            InvokePrivate(manager, "RebuildRecentRecordingsMenu");

            Assert.False(HasMenuItemHeader(menuItem, "Open Videos folder"));
            Assert.True(HasMenuItemHeader(menuItem, "Clear recent recordings"));
        });
    }

    [Fact]
    public void OpenConfiguredFolder_WhenPathIsInvalid_ShowsWarning()
    {
        StaTestHelper.Run(() =>
        {
            var messageBoxMock = new Mock<IMessageBoxService>();
            var manager = CreateManager(messageBox: messageBoxMock.Object);

            InvokePrivate(manager, "OpenConfiguredFolder", "bad\0path");

            messageBoxMock.Verify(service => service.ShowWarning(
                "Could not open the configured folder. Please verify the path in Settings.",
                "Open Folder"), Times.Once);
        });
    }

    private static TrayIconManager CreateManager(
        IMessageBoxService? messageBox = null,
        IProcessService? processService = null,
        IUpdateService? updateService = null,
        IAppVersionService? appVersionService = null,
        IAutoUpdateService? autoUpdate = null,
        IUserSettingsService? userSettings = null,
        IGifExportService? gifExportService = null)
    {
        return new TrayIconManager(
            NullLogger<TrayIconManager>.Instance,
            messageBox ?? Mock.Of<IMessageBoxService>(),
            processService ?? Mock.Of<IProcessService>(),
            updateService ?? Mock.Of<IUpdateService>(),
            appVersionService ?? Mock.Of<IAppVersionService>(),
            autoUpdate ?? Mock.Of<IAutoUpdateService>(),
            userSettings ?? Mock.Of<IUserSettingsService>(),
            gifExportService ?? Mock.Of<IGifExportService>(),
            Mock.Of<ITelemetryService>(),
            onNewSnip: static () => { },
            onWholeScreenSnip: static () => { },
            onOpenImage: static () => { },
            onShowSettings: static () => { },
            onShowAbout: static () => { });
    }

    private static void InvokePrivate(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(target, args);
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

    private static object CreateRecentRecordingItem(string outputPath, string elapsedText)
    {
        var recentRecordingType = typeof(TrayIconManager).GetNestedType("RecentRecordingItem", System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(recentRecordingType);
        return Activator.CreateInstance(recentRecordingType!, [outputPath, elapsedText])!;
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

    private static bool HasMenuItemHeader(System.Windows.Controls.MenuItem menuItem, string expectedHeader)
    {
        foreach (var item in menuItem.Items)
        {
            if (item is System.Windows.Controls.MenuItem child &&
                string.Equals(child.Header?.ToString(), expectedHeader, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
