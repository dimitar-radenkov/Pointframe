using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pointframe.Models;
using Pointframe.Services;
using Pointframe.Services.Messaging;
using Pointframe.Tests.Services.Handlers;
using Pointframe.ViewModels;
using Xunit;

namespace Pointframe.Tests;

[Collection("OverlayWindowUi")]
public sealed class OverlayWindowRecordingFlowTests
{
    [Fact]
    public void CreateRecordingSessionGeometry_WithZeroSelectionDimensions_UsesUnitScale()
    {
        var result = OverlayWindow.CreateRecordingSessionGeometry(
            new Rect(0, 0, 0, 0),
            new Int32Rect(200, 300, 640, 360),
            "DISPLAY1",
            new Int32Rect(100, 100, 1920, 1080),
            new Int32Rect(100, 100, 1880, 1040));

        Assert.Equal(1d, result.MonitorScaleX);
        Assert.Equal(1d, result.MonitorScaleY);
        Assert.Equal(640, result.CaptureRectDips.Width);
        Assert.Equal(360, result.CaptureRectDips.Height);
    }

    [Fact]
    public void CloseRecordingSessionWindows_ResetsStoredGeometry()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                SetPrivateField(context.Window, "_recordingSessionGeometry", new RecordingSessionGeometry(
                    new Int32Rect(0, 0, 100, 100),
                    new Int32Rect(0, 0, 100, 100),
                    new Int32Rect(0, 0, 100, 100),
                    new Rect(0, 0, 100, 100),
                    new Rect(0, 0, 100, 100),
                    new Rect(0, 0, 100, 100),
                    "DISPLAY1",
                    1d,
                    1d));

                InvokePrivate(context.Window, "CloseRecordingSessionWindows");

                var geometry = GetPrivateField<RecordingSessionGeometry>(context.Window, "_recordingSessionGeometry");
                Assert.Equal(RecordingSessionGeometry.Empty, geometry);
            }
            finally
            {
                context.Dispose();
            }
        });
    }

    [Fact]
    public void StartRecordingSession_WhenFfmpegMissing_ShowsWarningAndKeepsRecordingSessionClosed()
    {
        StaTestHelper.RunAsync(async () =>
        {
            var context = CreateContext();
            try
            {
                context.Window.InitializeFromSelectionSession(CreateSelectionSession());
                context.ViewModel.CommitSelection(
                    new Rect(20, 40, 200, 120),
                    new Int32Rect(100, 200, 400, 240));

                context.RecorderMock
                    .Setup(service => service.Start(100, 200, 400, 240, It.IsAny<string>()))
                    .Throws(new FileNotFoundException("ffmpeg missing"));

                var previousContext = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                try
                {
                    var task = Assert.IsAssignableFrom<Task>(InvokePrivate(context.Window, "StartRecordingSession"));
                    await task;
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }

                context.MessageBoxMock.Verify(
                    service => service.ShowWarning("ffmpeg missing", "ffmpeg not found"),
                    Times.Once);
                context.FileSystemMock.Verify(service => service.CreateDirectory(@"C:\\recordings"), Times.Once);
                context.FileSystemMock.Verify(
                    service => service.CombinePath(@"C:\\recordings", It.IsRegex(@"^SnipRec-\d{8}-\d{6}\.mp4$")),
                    Times.Once);
                Assert.False(GetPrivateField<bool>(context.Window, "_closeLeavesRecorderRunning"));
            }
            finally
            {
                context.Dispose();
            }
        });
    }

    private static TestContext CreateContext()
    {
        var eventAggregator = new DefaultEventAggregator(NullLogger<DefaultEventAggregator>.Instance);

        var userSettings = new UserSettings
        {
            DefaultAnnotationColor = "#FFFF0000",
            RecordingOutputPath = @"C:\\recordings",
            ScreenshotSavePath = @"C:\\shots",
            RecordMicrophone = false,
        };
        var userSettingsMock = new Mock<IUserSettingsService>();
        userSettingsMock.SetupGet(service => service.Current).Returns(userSettings);

        var fileSystemMock = new Mock<IFileSystemService>();
        fileSystemMock
            .Setup(service => service.CombinePath(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string left, string right) => Path.Combine(left, right));

        var viewModel = new OverlayViewModel(
            new AnnotationGeometryService(),
            NullLogger<OverlayViewModel>.Instance,
            userSettingsMock.Object,
            Mock.Of<IDialogService>(),
            Mock.Of<IClipboardService>(),
            fileSystemMock.Object,
            eventAggregator);

        var recorderMock = new Mock<IScreenRecordingService>();
        recorderMock.SetupGet(service => service.IsRecording).Returns(false);
        recorderMock.SetupGet(service => service.IsPaused).Returns(false);
        recorderMock.SetupGet(service => service.CanToggleMicrophone).Returns(true);
        recorderMock.SetupGet(service => service.IsMicrophoneMuted).Returns(false);
        recorderMock.SetupGet(service => service.IsRecordingMicrophoneEnabled).Returns(false);

        var screenCaptureMock = new Mock<IScreenCaptureService>();
        screenCaptureMock
            .Setup(service => service.Capture(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(CreateBitmap());

        var messageBoxMock = new Mock<IMessageBoxService>();
        var ocrServiceMock = new Mock<IOcrService>();

        var recordingAnnotationViewModel = new RecordingAnnotationViewModel(
            new AnnotationGeometryService(),
            NullLogger<RecordingAnnotationViewModel>.Instance,
            userSettingsMock.Object,
            eventAggregator);

        var window = new OverlayWindow(
            viewModel,
            screenCaptureMock.Object,
            recorderMock.Object,
            Mock.Of<IMouseHookService>(),
            (service, outputPath) => new RecordingHudViewModel(
                service,
                outputPath,
                eventAggregator,
                NullLogger<RecordingHudViewModel>.Instance),
            eventAggregator,
            NullLoggerFactory.Instance,
            userSettingsMock.Object,
            messageBoxMock.Object,
            fileSystemMock.Object,
            ocrServiceMock.Object,
            recordingAnnotationViewModel);

        return new TestContext(window, viewModel, recorderMock, fileSystemMock, messageBoxMock, eventAggregator);
    }

    private static object? InvokePrivate(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.Invoke(target, args);
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (T)field.GetValue(target)!;
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }

    private static BitmapSource CreateBitmap(int width = 2, int height = 2)
    {
        var pixels = new byte[width * height * 4];
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static SelectionSessionResult CreateSelectionSession()
    {
        return new SelectionSessionResult(
            "DISPLAY1",
            CreateBitmap(120, 80),
            CreateBitmap(120, 80),
            new Rect(0, 0, 120, 80),
            new Int32Rect(0, 0, 120, 80),
            new Rect(10, 10, 80, 40),
            new Int32Rect(10, 10, 80, 40),
            1d,
            1d);
    }

    private sealed record TestContext(
        OverlayWindow Window,
        OverlayViewModel ViewModel,
        Mock<IScreenRecordingService> RecorderMock,
        Mock<IFileSystemService> FileSystemMock,
        Mock<IMessageBoxService> MessageBoxMock,
        DefaultEventAggregator EventAggregator)
    {
        public void Dispose()
        {
            if (Window.Dispatcher.CheckAccess())
            {
                Window.Close();
            }
            else
            {
                Window.Dispatcher.Invoke(Window.Close);
            }

            EventAggregator.Dispose();
        }
    }
}
