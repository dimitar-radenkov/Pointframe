using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
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
public sealed class OverlayWindowInteractionTests
{
    [Fact]
    public void Constructor_AssignsViewModelAsDataContext()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                Assert.Same(context.ViewModel, context.Window.DataContext);
            }
            finally
            {
                context.Dispose();
            }
        });
    }

    [Fact]
    public void InitializeFromImage_StoresOpenedImageAndPath()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                var image = CreateBitmap();
                context.Window.InitializeFromImage(image, @"C:\\images\\sample.png");

                var openedImage = GetPrivateField<BitmapSource?>(context.Window, "_openedImage");
                var openedImagePath = GetPrivateField<string?>(context.Window, "_openedImagePath");

                Assert.Same(image, openedImage);
                Assert.Equal(@"C:\\images\\sample.png", openedImagePath);
            }
            finally
            {
                context.Dispose();
            }
        });
    }

    [Fact]
    public void InitializeFromSelectionSession_StoresPendingSession()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                var selectionSession = new SelectionSessionResult(
                    "DISPLAY1",
                    CreateBitmap(8, 8),
                    CreateBitmap(8, 8),
                    new Rect(10, 20, 400, 300),
                    new Int32Rect(20, 40, 800, 600),
                    new Rect(30, 50, 200, 120),
                    new Int32Rect(60, 100, 400, 240),
                    2d,
                    2d);

                context.Window.InitializeFromSelectionSession(selectionSession);

                var pending = GetPrivateField<SelectionSessionResult?>(context.Window, "_pendingSelectionSession");
                Assert.Equal(selectionSession, pending);
            }
            finally
            {
                context.Dispose();
            }
        });
    }

    [Fact]
    public void ToolClick_SetsSelectedToolAndCursor()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                context.ViewModel.IsTextLassoActive = true;
                var annotationCanvas = Assert.IsType<Canvas>(context.Window.FindName("AnnotationCanvas"));
                var toolButton = new RadioButton { Tag = nameof(AnnotationTool.Text) };

                InvokePrivate(context.Window, "Tool_Click", toolButton, new RoutedEventArgs());

                Assert.False(context.ViewModel.IsTextLassoActive);
                Assert.Equal(AnnotationTool.Text, context.ViewModel.SelectedTool);
                Assert.Equal(Cursors.IBeam, annotationCanvas.Cursor);
            }
            finally
            {
                context.Dispose();
            }
        });
    }

    [Fact]
    public void WindowKeyDown_Escape_WhenTextLassoActive_ClearsLassoState()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                var lassoRect = Assert.IsType<Rectangle>(context.Window.FindName("OcrLassoRect"));
                lassoRect.Visibility = Visibility.Visible;
                context.ViewModel.IsTextLassoActive = true;
                SetPrivateField(context.Window, "_lassoStart", new Point(12d, 14d));

                var args = CreateKeyArgs(Key.Escape);
                InvokePrivate(context.Window, "Window_KeyDown", context.Window, args);

                Assert.False(context.ViewModel.IsTextLassoActive);
                Assert.Equal(Visibility.Collapsed, lassoRect.Visibility);
                Assert.Null(GetPrivateField<Point?>(context.Window, "_lassoStart"));
            }
            finally
            {
                context.Dispose();
            }
        });
    }

    [Fact]
    public void EventAggregatorRedoAndUndo_UpdateAnnotationCanvasAndNumberCounter()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                var annotationCanvas = Assert.IsType<Canvas>(context.Window.FindName("AnnotationCanvas"));
                var numberElement = new TextBlock { Tag = "number" };

                context.EventAggregator.Publish(new RedoGroupMessage([numberElement])).GetAwaiter().GetResult();

                Assert.Single(annotationCanvas.Children);
                Assert.Same(numberElement, annotationCanvas.Children[0]);
                Assert.Equal(1, context.ViewModel.NumberCounter);

                context.EventAggregator.Publish(new UndoGroupMessage([numberElement])).GetAwaiter().GetResult();

                Assert.Empty(annotationCanvas.Children);
                Assert.Equal(0, context.ViewModel.NumberCounter);
            }
            finally
            {
                context.Dispose();
            }
        });
    }

    [Fact]
    public void DoPin_HidesOverlayAndStoresPendingBitmap()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                var pinnedBitmap = CreateBitmap(12, 8);

                InvokePrivate(context.Window, "DoPin", pinnedBitmap);

                Assert.Equal(Visibility.Hidden, context.Window.Visibility);
                Assert.Same(pinnedBitmap, GetPrivateField<BitmapSource?>(context.Window, "_pendingPinnedBitmap"));
            }
            finally
            {
                context.Dispose();
            }
        });
    }

    [Fact]
    public void DoLassoOcr_WhenBackgroundIsMissing_DoesNotInvokeOcrService()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                var task = Assert.IsAssignableFrom<Task>(InvokePrivate(context.Window, "DoLassoOcr", new Rect(1d, 2d, 30d, 16d)));
                task.GetAwaiter().GetResult();

                context.OcrServiceMock.Verify(service => service.Recognize(It.IsAny<BitmapSource>()), Times.Never);
            }
            finally
            {
                context.Dispose();
            }
        });
    }

    [Fact]
    public void OnClosed_WhenRecorderIsRecording_StopsRecorder()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext(isRecorderRecording: true);

            InvokePrivate(context.Window, "OnClosed", EventArgs.Empty);

            context.RecorderMock.Verify(service => service.Stop(), Times.Once);
            context.EventAggregator.Dispose();
        });
    }

    private static TestContext CreateContext(bool isRecorderRecording = false)
    {
        var eventAggregator = new DefaultEventAggregator(NullLogger<DefaultEventAggregator>.Instance);

        var userSettings = new UserSettings
        {
            DefaultAnnotationColor = "#FFFF0000",
            RecordingOutputPath = @"C:\\recordings",
            ScreenshotSavePath = @"C:\\shots",
        };
        var userSettingsMock = new Mock<IUserSettingsService>();
        userSettingsMock.SetupGet(service => service.Current).Returns(userSettings);

        var clipboardMock = new Mock<IClipboardService>();
        var fileSystemMock = new Mock<IFileSystemService>();
        fileSystemMock.Setup(service => service.CombinePath(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string left, string right) => System.IO.Path.Combine(left, right));
        var dialogMock = new Mock<IDialogService>();

        var viewModel = new OverlayViewModel(
            new AnnotationGeometryService(),
            NullLogger<OverlayViewModel>.Instance,
            userSettingsMock.Object,
            dialogMock.Object,
            clipboardMock.Object,
            fileSystemMock.Object,
            eventAggregator);

        var recorderMock = new Mock<IScreenRecordingService>();
        recorderMock.SetupGet(service => service.IsRecording).Returns(isRecorderRecording);
        recorderMock.SetupGet(service => service.IsPaused).Returns(false);
        recorderMock.SetupGet(service => service.CanToggleMicrophone).Returns(true);
        recorderMock.SetupGet(service => service.IsMicrophoneMuted).Returns(false);

        var screenCaptureMock = new Mock<IScreenCaptureService>();
        screenCaptureMock
            .Setup(service => service.Capture(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(CreateBitmap());

        var mouseHookMock = new Mock<IMouseHookService>();
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
            mouseHookMock.Object,
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

        return new TestContext(
            window,
            viewModel,
            recorderMock,
            ocrServiceMock,
            eventAggregator);
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

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }

    private static KeyEventArgs CreateKeyArgs(Key key)
    {
        var source = new HwndSource(new HwndSourceParameters("OverlayWindowInteractionTests")
        {
            Width = 1,
            Height = 1,
        });

        return new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
        };
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

    private sealed record TestContext(
        OverlayWindow Window,
        OverlayViewModel ViewModel,
        Mock<IScreenRecordingService> RecorderMock,
        Mock<IOcrService> OcrServiceMock,
        DefaultEventAggregator EventAggregator)
    {
        public void Dispose()
        {
            Window.Close();
            EventAggregator.Dispose();
        }
    }
}
