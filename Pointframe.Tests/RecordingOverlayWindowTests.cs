using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
public sealed class RecordingOverlayWindowTests
{
    [Fact]
    public void Constructor_SetsWindowSizeFromHostGeometry()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                Assert.Equal(context.Geometry.HostBoundsDips.Width, context.Window.Width);
                Assert.Equal(context.Geometry.HostBoundsDips.Height, context.Window.Height);
            }
            finally
            {
                context.Window.Close();
            }
        });
    }

    [Fact]
    public void ToggleRecordingAnnotationInput_TogglesInputStateAndCursor()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                var canvas = Assert.IsType<Canvas>(context.Window.FindName("RecordingAnnotationCanvas"));

                var armed = (bool)InvokePrivate(context.Window, "ToggleRecordingAnnotationInput")!;
                Assert.True(armed);
                Assert.True(context.AnnotationViewModel.IsInputArmed);
                Assert.Equal(Cursors.Pen, canvas.Cursor);

                var disarmed = (bool)InvokePrivate(context.Window, "ToggleRecordingAnnotationInput")!;
                Assert.False(disarmed);
                Assert.False(context.AnnotationViewModel.IsInputArmed);
                Assert.Equal(Cursors.Arrow, canvas.Cursor);
            }
            finally
            {
                context.Window.Close();
            }
        });
    }

    [Fact]
    public void WindowKeyDown_Escape_DisarmsAnnotationInput()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                InvokePrivate(context.Window, "SetRecordingAnnotationInputArmed", true, false);
                Assert.True(context.AnnotationViewModel.IsInputArmed);

                var args = CreateKeyArgs(Key.Escape);
                InvokePrivate(context.Window, "Window_KeyDown", context.Window, args);

                Assert.True(args.Handled);
                Assert.False(context.AnnotationViewModel.IsInputArmed);
            }
            finally
            {
                context.Window.Close();
            }
        });
    }

    [Fact]
    public void EventAggregatorRedoAndUndo_UpdateCanvasAndAnnotationState()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                var canvas = Assert.IsType<Canvas>(context.Window.FindName("RecordingAnnotationCanvas"));
                var numberElement = new TextBlock { Tag = "number" };

                context.EventAggregator.Publish(new RedoGroupMessage([numberElement])).GetAwaiter().GetResult();

                Assert.Single(canvas.Children);
                Assert.Same(numberElement, canvas.Children[0]);
                Assert.True(context.AnnotationViewModel.HasActiveAnnotations);
                Assert.Equal(1, context.AnnotationViewModel.NumberCounter);

                context.EventAggregator.Publish(new UndoGroupMessage([numberElement])).GetAwaiter().GetResult();

                Assert.Empty(canvas.Children);
                Assert.False(context.AnnotationViewModel.HasActiveAnnotations);
                Assert.Equal(0, context.AnnotationViewModel.NumberCounter);
            }
            finally
            {
                context.Window.Close();
                context.EventAggregator.Dispose();
            }
        });
    }

    [Fact]
    public void ShowRecordingHudAndHideRecordingHud_UpdatesPanelBindingAndVisibility()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                var panel = Assert.IsType<Border>(context.Window.FindName("RecordingHudPanel"));
                var hudViewModel = CreateHudViewModel(context.RecorderMock.Object, context.EventAggregator);

                InvokePrivate(context.Window, "ShowRecordingHud", hudViewModel);
                Assert.Same(hudViewModel, panel.DataContext);
                Assert.Equal(Visibility.Visible, panel.Visibility);

                InvokePrivate(context.Window, "HideRecordingHud");
                Assert.Null(panel.DataContext);
                Assert.Equal(Visibility.Collapsed, panel.Visibility);
            }
            finally
            {
                context.Window.Close();
                context.EventAggregator.Dispose();
            }
        });
    }

    [Fact]
    public void WndProc_HitTestInsideCapture_WhenInteractive_ReturnsTransparent()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                var result = InvokeWndProc(
                    context.Window,
                    hwnd: IntPtr.Zero,
                    msg: 0x0084,
                    lParam: BuildLParam(150, 150),
                    out var handled);

                Assert.Equal(new IntPtr(-1), result);
                Assert.True(handled);
            }
            finally
            {
                context.Window.Close();
                context.EventAggregator.Dispose();
            }
        });
    }

    [Fact]
    public void WndProc_HitTestInsideCapture_WhenDrawing_ReturnsClientHitTest()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                InvokePrivate(context.Window, "SetRecordingAnnotationInputArmed", true, false);

                var result = InvokeWndProc(
                    context.Window,
                    hwnd: IntPtr.Zero,
                    msg: 0x0084,
                    lParam: BuildLParam(150, 150),
                    out var handled);

                Assert.Equal(IntPtr.Zero, result);
                Assert.False(handled);
            }
            finally
            {
                context.Window.Close();
                context.EventAggregator.Dispose();
            }
        });
    }

    [Fact]
    public void WndProc_HitTestOutsideHudAndCanvas_ReturnsTransparent()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                var result = InvokeWndProc(
                    context.Window,
                    hwnd: IntPtr.Zero,
                    msg: 0x0084,
                    lParam: BuildLParam(20, 30),
                    out var handled);

                Assert.Equal(new IntPtr(-1), result);
                Assert.True(handled);
            }
            finally
            {
                context.Window.Close();
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
            context.MouseHookMock.Verify(service => service.Stop(), Times.Once);
            context.EventAggregator.Dispose();
        });
    }

    private static RecordingHudViewModel CreateHudViewModel(IScreenRecordingService recorder, IEventAggregator eventAggregator)
    {
        return new RecordingHudViewModel(
            recorder,
            @"C:\\recordings\\sample.mp4",
            eventAggregator,
            NullLogger<RecordingHudViewModel>.Instance);
    }

    private static TestContext CreateContext(
        bool isRecorderRecording = false,
        Point? cursorScreenPoint = null)
    {
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
        var settings = new UserSettings { HudGapPixels = 8 };
        var userSettingsMock = new Mock<IUserSettingsService>();
        userSettingsMock.SetupGet(service => service.Current).Returns(settings);

        var eventAggregator = new DefaultEventAggregator(NullLogger<DefaultEventAggregator>.Instance);
        var annotationViewModel = new RecordingAnnotationViewModel(
            new AnnotationGeometryService(),
            NullLogger<RecordingAnnotationViewModel>.Instance,
            userSettingsMock.Object,
            eventAggregator);

        var geometry = CreateGeometry();
        var window = new RecordingOverlayWindow(
            geometry,
            @"C:\\recordings\\sample.mp4",
            recorderMock.Object,
            screenCaptureMock.Object,
            mouseHookMock.Object,
            (service, outputPath) => new RecordingHudViewModel(
                service,
                outputPath,
                eventAggregator,
                NullLogger<RecordingHudViewModel>.Instance),
            eventAggregator,
            NullLoggerFactory.Instance,
            userSettingsMock.Object,
            annotationViewModel,
            getCursorScreenPoint: () => cursorScreenPoint);

        return new TestContext(window, geometry, annotationViewModel, recorderMock, mouseHookMock, eventAggregator);
    }

    private static RecordingSessionGeometry CreateGeometry()
    {
        return new RecordingSessionGeometry(
            new Int32Rect(0, 0, 1000, 700),
            new Int32Rect(100, 100, 600, 400),
            new Int32Rect(0, 0, 1920, 1080),
            new Rect(0, 0, 1000, 700),
            new Rect(0, 0, 1920, 1040),
            new Rect(100, 100, 600, 400),
            "DISPLAY1",
            1d,
            1d);
    }

    private static BitmapSource CreateBitmap()
    {
        var pixels = new int[4];
        var bitmap = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            4);
        bitmap.Freeze();
        return bitmap;
    }

    private static KeyEventArgs CreateKeyArgs(Key key)
    {
        var source = new HwndSource(new HwndSourceParameters("RecordingOverlayWindowTests")
        {
            Width = 1,
            Height = 1,
        });

        return new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
        };
    }

    private static object? InvokePrivate(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.Invoke(target, args);
    }

    private static IntPtr InvokeWndProc(RecordingOverlayWindow window, IntPtr hwnd, int msg, IntPtr lParam, out bool handled)
    {
        var method = typeof(RecordingOverlayWindow).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var args = new object[]
        {
            hwnd,
            msg,
            IntPtr.Zero,
            lParam,
            false,
        };

        var result = (IntPtr)method.Invoke(window, args)!;
        handled = (bool)args[4];
        return result;
    }

    private static IntPtr BuildLParam(int x, int y)
    {
        return new IntPtr((y << 16) | (x & 0xFFFF));
    }

    private sealed record TestContext(
        RecordingOverlayWindow Window,
        RecordingSessionGeometry Geometry,
        RecordingAnnotationViewModel AnnotationViewModel,
        Mock<IScreenRecordingService> RecorderMock,
        Mock<IMouseHookService> MouseHookMock,
        DefaultEventAggregator EventAggregator);
}
