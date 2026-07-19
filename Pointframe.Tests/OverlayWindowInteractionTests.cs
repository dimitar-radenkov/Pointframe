using System.Reflection;
using System.IO;
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
    public void InitializeFromImage_WithWindowCleanMode_PreservesSessionMode()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                var image = CreateBitmap(32, 24);

                context.Window.InitializeFromImage(image, @"C:\\images\\window-clean.png", SelectionSessionMode.WindowClean);

                var sessionMode = GetPrivateField<SelectionSessionMode>(context.Window, "_selectionSessionMode");
                Assert.Equal(SelectionSessionMode.WindowClean, sessionMode);
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
                    2d,
                    SelectionSessionMode.Region);

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
                context.ViewModel.InitializeAnnotatingSession(new Rect(0d, 0d, 100d, 80d), 1d, 1d);
                context.ViewModel.IsTextLassoActive = true;
                var lasso = GetPrivateField<OcrLassoController>(context.Window, "_ocrLasso");
                lasso.HandlePointerDown(new Point(12d, 14d));
                Assert.Equal(Visibility.Visible, lassoRect.Visibility);
                Assert.True(lasso.HasPendingLasso);

                var args = CreateKeyArgs(Key.Escape);
                InvokePrivate(context.Window, "Window_KeyDown", context.Window, args);

                Assert.False(context.ViewModel.IsTextLassoActive);
                Assert.Equal(Visibility.Collapsed, lassoRect.Visibility);
                Assert.False(lasso.HasPendingLasso);
                Assert.True(args.Handled);
            }
            finally
            {
                context.Dispose();
            }
        });
    }

    [Fact]
    public void HandleOverlayShortcut_F1_TogglesShortcutsPopupVisibility()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                context.ViewModel.InitializeAnnotatingSession(new Rect(10d, 10d, 100d, 80d), 1d, 1d);
                var popup = Assert.IsType<Border>(context.Window.FindName("ShortcutsPopup"));
                Assert.Equal(Visibility.Collapsed, popup.Visibility);

                var opened = Assert.IsType<bool>(InvokePrivate(context.Window, "HandleOverlayShortcut", Key.F1, ModifierKeys.None));
                Assert.True(opened);
                Assert.Equal(Visibility.Visible, popup.Visibility);

                var closed = Assert.IsType<bool>(InvokePrivate(context.Window, "HandleOverlayShortcut", Key.F1, ModifierKeys.None));
                Assert.True(closed);
                Assert.Equal(Visibility.Collapsed, popup.Visibility);
            }
            finally
            {
                context.Dispose();
            }
        });
    }

    [Fact]
    public void HandleOverlayShortcut_CtrlShiftS_ExecutesSaveAsCommand()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                context.ViewModel.InitializeAnnotatingSession(new Rect(0d, 0d, 120d, 90d), 1d, 1d);
                var captureMock = new Mock<IOverlayBitmapCapture>();
                captureMock.Setup(c => c.ComposeBitmap()).Returns(CreateBitmap());
                context.ViewModel.SetBitmapCapture(captureMock.Object);
                context.DialogMock.Setup(d => d.PickSaveImageFile(It.IsAny<string>(), It.IsAny<string>())).Returns(@"D:\exports\saved-as.png");
                context.FileSystemMock.Setup(f => f.OpenWrite(@"D:\exports\saved-as.png")).Returns(new MemoryStream());

                var handled = Assert.IsType<bool>(InvokePrivate(context.Window, "HandleOverlayShortcut", Key.S, ModifierKeys.Control | ModifierKeys.Shift));

                Assert.True(handled);
                context.DialogMock.Verify(d => d.PickSaveImageFile(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
                context.FileSystemMock.Verify(f => f.OpenWrite(@"D:\exports\saved-as.png"), Times.Once);
            }
            finally
            {
                context.Dispose();
            }
        });
    }

    [Fact]
    public void HandleOverlayShortcut_CloseShortcut_WorksInSelectingPhase()
    {
        StaTestHelper.Run(() =>
        {
            var settings = new UserSettings
            {
                DefaultAnnotationColor = "#FFFF0000",
                RecordingOutputPath = @"C:\\recordings",
                ScreenshotSavePath = @"C:\\shots",
                OverlayCloseHotkey = 0x41, // A
                OverlayCloseHotkeyModifiers = HotkeyModifiers.Ctrl,
            };
            var context = CreateContext(userSettings: settings);
            try
            {
                var handled = Assert.IsType<bool>(InvokePrivate(context.Window, "HandleOverlayShortcut", Key.A, ModifierKeys.Control));
                Assert.True(handled);
            }
            finally
            {
                context.Dispose();
            }
        });
    }

    [Fact]
    public void HandleOverlayShortcut_UsesConfiguredCopyShortcut()
    {
        StaTestHelper.Run(() =>
        {
            var settings = new UserSettings
            {
                DefaultAnnotationColor = "#FFFF0000",
                RecordingOutputPath = @"C:\\recordings",
                ScreenshotSavePath = @"C:\\shots",
                AutoSaveScreenshots = false,
                OverlayCopyHotkey = 0x58, // X
                OverlayCopyHotkeyModifiers = HotkeyModifiers.Alt,
            };
            var context = CreateContext(userSettings: settings);
            try
            {
                context.ViewModel.InitializeAnnotatingSession(new Rect(0d, 0d, 120d, 90d), 1d, 1d);
                var captureMock = new Mock<IOverlayBitmapCapture>();
                captureMock.Setup(c => c.ComposeBitmap()).Returns(CreateBitmap());
                context.ViewModel.SetBitmapCapture(captureMock.Object);

                var handled = Assert.IsType<bool>(InvokePrivate(context.Window, "HandleOverlayShortcut", Key.X, ModifierKeys.Alt));

                Assert.True(handled);
                captureMock.Verify(c => c.ComposeBitmap(), Times.Once);
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
                var lasso = GetPrivateField<OcrLassoController>(context.Window, "_ocrLasso");
                lasso.RecognizeAsync(new Rect(1d, 2d, 30d, 16d)).GetAwaiter().GetResult();

                context.OcrServiceMock.Verify(service => service.Recognize(It.IsAny<BitmapSource>()), Times.Never);
            }
            finally
            {
                context.Dispose();
            }
        });
    }

    [Fact]
    public void DoLassoOcr_WhenNoTextDetected_TracksAttemptAndNoTextTelemetry()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                SetRendererBackground(context.Window, CreateBitmap(30, 20), dpiX: 1d, dpiY: 1d);
                context.OcrServiceMock
                    .Setup(service => service.Recognize(It.IsAny<BitmapSource>()))
                    .ReturnsAsync("   ");

                var lasso = GetPrivateField<OcrLassoController>(context.Window, "_ocrLasso");
                lasso.RecognizeAsync(new Rect(1d, 2d, 10d, 6d)).GetAwaiter().GetResult();

                context.TelemetryMock.Verify(
                    telemetry => telemetry.TrackEvent(
                        "ocr_attempted",
                        It.Is<IReadOnlyDictionary<string, string>?>(props =>
                            props != null
                            && props["selection_width_px"] == "10"
                            && props["selection_height_px"] == "6")),
                    Times.Once);
                context.TelemetryMock.Verify(
                    telemetry => telemetry.TrackEvent(
                        "ocr_no_text",
                        It.Is<IReadOnlyDictionary<string, string>?>(props =>
                            props != null
                            && props["selection_width_px"] == "10"
                            && props["selection_height_px"] == "6")),
                    Times.Once);
                context.TelemetryMock.Verify(
                    telemetry => telemetry.TrackEvent("ocr_used", It.IsAny<IReadOnlyDictionary<string, string>?>()),
                    Times.Never);
            }
            finally
            {
                context.Dispose();
            }
        });
    }

    [Fact]
    public void DoLassoOcr_WhenTextDetected_TracksAttemptAndUsedTelemetry()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                SetRendererBackground(context.Window, CreateBitmap(32, 24), dpiX: 1d, dpiY: 1d);
                context.OcrServiceMock
                    .Setup(service => service.Recognize(It.IsAny<BitmapSource>()))
                    .ReturnsAsync("copied text");

                var lasso = GetPrivateField<OcrLassoController>(context.Window, "_ocrLasso");
                lasso.RecognizeAsync(new Rect(2d, 3d, 8d, 5d)).GetAwaiter().GetResult();

                context.TelemetryMock.Verify(
                    telemetry => telemetry.TrackEvent(
                        "ocr_attempted",
                        It.Is<IReadOnlyDictionary<string, string>?>(props =>
                            props != null
                            && props["selection_width_px"] == "8"
                            && props["selection_height_px"] == "5")),
                    Times.Once);
                context.TelemetryMock.Verify(
                    telemetry => telemetry.TrackEvent(
                        "ocr_used",
                        It.Is<IReadOnlyDictionary<string, string>?>(props =>
                            props != null
                            && props["selection_width_px"] == "8"
                            && props["selection_height_px"] == "5")),
                    Times.Once);
                context.TelemetryMock.Verify(
                    telemetry => telemetry.TrackEvent("ocr_no_text", It.IsAny<IReadOnlyDictionary<string, string>?>()),
                    Times.Never);
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

    private static TestContext CreateContext(bool isRecorderRecording = false, UserSettings? userSettings = null)
    {
        var eventAggregator = new DefaultEventAggregator(NullLogger<DefaultEventAggregator>.Instance);

        var effectiveUserSettings = userSettings ?? new UserSettings
        {
            DefaultAnnotationColor = "#FFFF0000",
            RecordingOutputPath = @"C:\\recordings",
            ScreenshotSavePath = @"C:\\shots",
        };
        var userSettingsMock = new Mock<IUserSettingsService>();
        userSettingsMock.SetupGet(service => service.Current).Returns(effectiveUserSettings);

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
            eventAggregator,
            Mock.Of<ITelemetryService>(),
            Mock.Of<IScreenshotWatermarkService>());

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
        var telemetryMock = new Mock<ITelemetryService>();

        var recordingAnnotationViewModel = new RecordingAnnotationViewModel(
            new AnnotationGeometryService(),
            NullLogger<RecordingAnnotationViewModel>.Instance,
            userSettingsMock.Object,
            eventAggregator,
            Mock.Of<ITelemetryService>());

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
            telemetryMock.Object,
            recordingAnnotationViewModel,
            _ => throw new NotImplementedException());

        return new TestContext(
            window,
            viewModel,
            recorderMock,
            ocrServiceMock,
            telemetryMock,
            dialogMock,
            fileSystemMock,
            eventAggregator);
    }

    private static void SetRendererBackground(OverlayWindow window, BitmapSource background, double dpiX, double dpiY)
    {
        var renderer = GetPrivateField<object>(window, "_renderer");
        var setBackgroundMethod = renderer.GetType().GetMethod("SetBackground", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(setBackgroundMethod);
        setBackgroundMethod.Invoke(renderer, [background, dpiX, dpiY]);
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
        Mock<ITelemetryService> TelemetryMock,
        Mock<IDialogService> DialogMock,
        Mock<IFileSystemService> FileSystemMock,
        DefaultEventAggregator EventAggregator)
    {
        public void Dispose()
        {
            Window.Close();
            EventAggregator.Dispose();
        }
    }
}
