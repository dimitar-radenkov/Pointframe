using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pointframe.Services;
using Pointframe.Services.Messaging;
using Pointframe.Tests.Services.Handlers;
using Pointframe.ViewModels;
using Xunit;

namespace Pointframe.Tests;

[Collection("WpfUi")]
public sealed class RecordingOverlayWindowCameraTests
{
    private sealed class FakeSession : IRecordingCameraSession
    {
        private CameraFrame? _pending;

        public bool IsOn { get; set; } = true;

        public bool IsBubbleVisible { get; set; }

        public int DisposeCalls { get; private set; }

        public event Action? FrameAvailable;

        public event Action? BubbleVisibilityChanged;

        public event Action? StateChanged;

        public Task StartAsync() => Task.CompletedTask;

        public Task SetOnAsync(bool on)
        {
            IsOn = on;
            StateChanged?.Invoke();
            return Task.CompletedTask;
        }

        public CameraFrame? TryTakeFrame()
        {
            var frame = _pending;
            _pending = null;
            return frame;
        }

        public void Dispose() => DisposeCalls++;

        public void PushFrame(int width = 16, int height = 12)
        {
            _pending = CameraFrame.Rent(width, height);
            FrameAvailable?.Invoke();
        }

        public void SetBubbleVisible(bool visible)
        {
            IsBubbleVisible = visible;
            BubbleVisibilityChanged?.Invoke();
        }
    }

    private sealed record Context(RecordingOverlayWindow Window, FakeSession? Session, RecordingSessionGeometry Geometry);

    private static Context CreateContext(FakeSession? session, UserSettings? settings = null)
    {
        var recorderMock = new Mock<IScreenRecordingService>();
        recorderMock.SetupGet(service => service.IsRecording).Returns(false);
        recorderMock.SetupGet(service => service.CanToggleMicrophone).Returns(true);

        var screenCaptureMock = new Mock<IScreenCaptureService>();
        var userSettings = settings ?? new UserSettings { HudGapPixels = 8, RecordCamera = true };
        var userSettingsMock = new Mock<IUserSettingsService>();
        userSettingsMock.SetupGet(service => service.Current).Returns(userSettings);

        var eventAggregator = new DefaultEventAggregator(NullLogger<DefaultEventAggregator>.Instance);
        var annotationViewModel = new RecordingAnnotationViewModel(
            new AnnotationGeometryService(),
            NullLogger<RecordingAnnotationViewModel>.Instance,
            userSettingsMock.Object,
            eventAggregator,
            Mock.Of<ITelemetryService>());

        var geometry = new RecordingSessionGeometry(
            new Int32Rect(0, 0, 1000, 700),
            new Int32Rect(100, 100, 600, 400),
            new Int32Rect(0, 0, 1920, 1080),
            new Rect(0, 0, 1000, 700),
            new Rect(0, 0, 1920, 1040),
            new Rect(100, 100, 600, 400),
            "DISPLAY1",
            1d,
            1d);

        var window = new RecordingOverlayWindow(
            geometry,
            @"C:\recordings\sample.mp4",
            recorderMock.Object,
            screenCaptureMock.Object,
            Mock.Of<IMouseHookService>(),
            (service, outputPath) => new RecordingHudViewModel(service, outputPath, eventAggregator, NullLogger<RecordingHudViewModel>.Instance),
            eventAggregator,
            NullLoggerFactory.Instance,
            userSettingsMock.Object,
            annotationViewModel,
            getCursorScreenPoint: () => null,
            cameraSession: session);
        return new Context(window, session, geometry);
    }

    private static void Flush()
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    private static T Find<T>(RecordingOverlayWindow window, string name) where T : class
    {
        return (T)window.FindName(name);
    }

    private static bool InvokeHudPredicate(RecordingOverlayWindow window, Point screenPoint)
    {
        var method = typeof(RecordingOverlayWindow).GetMethod("IsPointInsideRecordingHud", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (bool)method.Invoke(window, [screenPoint])!;
    }

    private static bool InvokeCaptureSurfacePredicate(RecordingOverlayWindow window, Point screenPoint)
    {
        var method = typeof(RecordingOverlayWindow).GetMethod("IsPointInsideRecordingCaptureSurface", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (bool)method.Invoke(window, [screenPoint])!;
    }

    [Fact]
    public void TheBubble_IsAnOverlayRootElementOutsideTheAnnotationCanvas_NotInteractive_AndBetweenAnnotationsAndTheHud()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext(new FakeSession());
            try
            {
                var bubble = Find<FrameworkElement>(context.Window, "RecordingCameraBubble");
                var annotationCanvas = Find<Canvas>(context.Window, "RecordingAnnotationCanvas");
                var cursorEffects = Find<Canvas>(context.Window, "RecordingCursorEffectsCanvas");
                var hud = Find<FrameworkElement>(context.Window, "RecordingHudPanel");
                var compactHud = Find<FrameworkElement>(context.Window, "RecordingHudCompactPanel");
                var toast = Find<FrameworkElement>(context.Window, "RecordingFailureToast");
                var root = Find<Canvas>(context.Window, "Root");

                Assert.DoesNotContain(bubble, annotationCanvas.Children.Cast<UIElement>());
                Assert.DoesNotContain(bubble, cursorEffects.Children.Cast<UIElement>());
                Assert.Same(root, bubble.Parent);
                Assert.False(bubble.IsHitTestVisible);
                Assert.False(bubble.Focusable);

                var children = root.Children.Cast<UIElement>().ToList();
                Assert.True(children.IndexOf(bubble) > children.IndexOf(annotationCanvas));
                Assert.True(children.IndexOf(bubble) > children.IndexOf(cursorEffects));
                Assert.True(children.IndexOf(bubble) < children.IndexOf(hud));
                Assert.True(children.IndexOf(bubble) < children.IndexOf(compactHud));
                Assert.True(Panel.GetZIndex(toast) > Panel.GetZIndex(bubble));
                Assert.Equal(Visibility.Collapsed, bubble.Visibility);
            }
            finally
            {
                context.Window.Close();
            }
        });
    }

    [Fact]
    public void TheBubbleVideo_IsMirrored()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext(new FakeSession());
            try
            {
                var brush = Find<ImageBrush>(context.Window, "RecordingCameraBubbleBrush");

                var transform = Assert.IsType<ScaleTransform>(brush.RelativeTransform);
                Assert.Equal(-1d, transform.ScaleX);
                Assert.Equal(1d, transform.ScaleY);
            }
            finally
            {
                context.Window.Close();
            }
        });
    }

    [Fact]
    public void WithoutACameraSession_NothingIsCreatedAndTheBubbleStaysHidden()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext(null, new UserSettings { RecordCamera = false });
            try
            {
                var field = typeof(RecordingOverlayWindow).GetField("_recordingCameraBubbleCoordinator", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(field);
                Assert.Null(field.GetValue(context.Window));
                Assert.Equal(Visibility.Collapsed, Find<FrameworkElement>(context.Window, "RecordingCameraBubble").Visibility);
            }
            finally
            {
                context.Window.Close();
            }
        });
    }

    [Fact]
    public void FramesAndVisibility_PlaceTheBubbleInTheChosenCornerOfTheCaptureRectangle()
    {
        StaTestHelper.Run(() =>
        {
            var session = new FakeSession();
            var context = CreateContext(session);
            try
            {
                var bubble = Find<FrameworkElement>(context.Window, "RecordingCameraBubble");
                var brush = Find<ImageBrush>(context.Window, "RecordingCameraBubbleBrush");

                session.IsBubbleVisible = true;
                session.PushFrame();
                Flush();

                Assert.Equal(Visibility.Visible, bubble.Visibility);
                Assert.Equal(100d + 600d - 16d - 128d, Canvas.GetLeft(bubble), 3);
                Assert.Equal(100d + 400d - 16d - 128d, Canvas.GetTop(bubble), 3);
                Assert.Equal(128d, bubble.Width, 3);
                Assert.Equal(128d, bubble.Height, 3);
                var bitmap = Assert.IsType<WriteableBitmap>(brush.ImageSource);
                Assert.Equal(16, bitmap.PixelWidth);
                Assert.Equal(12, bitmap.PixelHeight);

                session.SetBubbleVisible(false);
                Flush();
                Assert.Equal(Visibility.Collapsed, bubble.Visibility);
            }
            finally
            {
                context.Window.Close();
            }
        });
    }

    [Fact]
    public void TheBubble_IsOutsideTheInteractiveHitRegions_SoAnnotationInputGoesThroughIt()
    {
        StaTestHelper.Run(() =>
        {
            var session = new FakeSession { IsBubbleVisible = true };
            var context = CreateContext(session);
            try
            {
                session.PushFrame();
                Flush();
                var bubble = Find<FrameworkElement>(context.Window, "RecordingCameraBubble");
                var insideBubble = new Point(
                    Canvas.GetLeft(bubble) + (bubble.Width / 2d),
                    Canvas.GetTop(bubble) + (bubble.Height / 2d));
                var screenPoint = context.Geometry.MapHostDipPointToScreenPixels(insideBubble);

                // Not part of the HUD hit region, and the (unarmed) capture surface is passthrough there as everywhere else.
                Assert.False(InvokeHudPredicate(context.Window, screenPoint));
                Assert.False(InvokeCaptureSurfacePredicate(context.Window, screenPoint));
                Assert.False(bubble.IsHitTestVisible);
            }
            finally
            {
                context.Window.Close();
            }
        });
    }
    [Fact]
    public void AFrameThatArrivedBeforeTheOverlayWasBuilt_StillShowsTheBubble()
    {
        StaTestHelper.Run(() =>
        {
            var session = new FakeSession { IsBubbleVisible = true };
            session.PushFrame();
            var context = CreateContext(session);
            try
            {
                Flush();

                var bubble = Find<FrameworkElement>(context.Window, "RecordingCameraBubble");
                Assert.Equal(Visibility.Visible, bubble.Visibility);
                Assert.Equal(128d, bubble.Width, 3);
            }
            finally
            {
                context.Window.Close();
            }
        });
    }


    [Fact]
    public void ClosingTheWindow_ReleasesTheCameraSessionOnce()
    {
        StaTestHelper.Run(() =>
        {
            var session = new FakeSession();
            var context = CreateContext(session);

            context.Window.Close();

            Assert.Equal(1, session.DisposeCalls);
        });
    }

    [Fact]
    public void CallbacksAfterTheWindowClosed_AreIgnoredAndDoNotThrow()
    {
        StaTestHelper.Run(() =>
        {
            var session = new FakeSession();
            var context = CreateContext(session);
            var bubble = Find<FrameworkElement>(context.Window, "RecordingCameraBubble");
            context.Window.Close();

            session.IsBubbleVisible = true;
            session.PushFrame();
            session.SetBubbleVisible(true);
            Flush();

            Assert.Equal(Visibility.Collapsed, bubble.Visibility);
            Assert.Equal(1, session.DisposeCalls);
        });
    }
}
