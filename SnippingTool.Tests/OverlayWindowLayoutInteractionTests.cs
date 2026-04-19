using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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
public sealed class OverlayWindowLayoutInteractionTests
{
    [Fact]
    public void CreateAnnotatingBackdropCrop_ReturnsExpectedPixelRegion()
    {
        var monitorSnapshot = CreateBitmap(120, 80);
        var method = typeof(OverlayWindow).GetMethod("CreateAnnotatingBackdropCrop", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = Assert.IsType<CroppedBitmap>(method.Invoke(null, [new Rect(10, 5, 20, 10), 2d, 2d, monitorSnapshot]));

        Assert.Equal(40, result.PixelWidth);
        Assert.Equal(20, result.PixelHeight);
    }

    [Fact]
    public void MeasureFloatingElement_WhenCollapsed_RestoresVisibility()
    {
        StaTestHelper.Run(() =>
        {
            var element = new Border
            {
                Width = 80,
                Height = 24,
                Visibility = Visibility.Collapsed,
            };

            var method = typeof(OverlayWindow).GetMethod("MeasureFloatingElement", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var size = (Size)method.Invoke(null, [element])!;

            Assert.True(size.Width >= 80);
            Assert.True(size.Height >= 24);
            Assert.Equal(Visibility.Collapsed, element.Visibility);
        });
    }

    [Fact]
    public void LayoutDimStrips_SetsExpectedRectanglesAroundSelection()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                context.Window.Width = 500;
                context.Window.Height = 300;
                var selection = new Rect(100, 40, 200, 120);

                InvokePrivate(context.Window, "LayoutDimStrips", selection);

                var dimTop = Assert.IsType<Rectangle>(context.Window.FindName("DimTop"));
                var dimBottom = Assert.IsType<Rectangle>(context.Window.FindName("DimBottom"));
                var dimLeft = Assert.IsType<Rectangle>(context.Window.FindName("DimLeft"));
                var dimRight = Assert.IsType<Rectangle>(context.Window.FindName("DimRight"));

                Assert.Equal(Visibility.Visible, dimTop.Visibility);
                Assert.Equal(500, dimTop.Width);
                Assert.Equal(40, dimTop.Height);

                Assert.Equal(Visibility.Visible, dimBottom.Visibility);
                Assert.Equal(160, Canvas.GetTop(dimBottom));
                Assert.Equal(140, dimBottom.Height);

                Assert.Equal(Visibility.Visible, dimLeft.Visibility);
                Assert.Equal(100, dimLeft.Width);
                Assert.Equal(120, dimLeft.Height);

                Assert.Equal(Visibility.Visible, dimRight.Visibility);
                Assert.Equal(300, Canvas.GetLeft(dimRight));
                Assert.Equal(200, dimRight.Width);
                Assert.Equal(120, dimRight.Height);
            }
            finally
            {
                context.Dispose();
            }
        });
    }

    [Fact]
    public void EnterAnnotatingSession_WithRecordingDisabled_HidesRecordButtonsAndShowsCanvas()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                context.Window.Width = 800;
                context.Window.Height = 600;
                var selection = new Rect(50, 60, 300, 180);
                var background = CreateBitmap(300, 180);

                InvokePrivate(context.Window, "EnterAnnotatingSession", selection, background, 1d, 1d, false);

                var recordBtn = Assert.IsType<Button>(context.Window.FindName("RecordBtn"));
                var compactRecordBtn = Assert.IsType<Button>(context.Window.FindName("CompactRecordBtn"));
                var annotationCanvas = Assert.IsType<Canvas>(context.Window.FindName("AnnotationCanvas"));
                var selectionBorder = Assert.IsType<Rectangle>(context.Window.FindName("SelectionBorder"));
                var snapshot = Assert.IsType<Image>(context.Window.FindName("ScreenSnapshot"));

                Assert.Equal(Visibility.Collapsed, recordBtn.Visibility);
                Assert.Equal(Visibility.Collapsed, compactRecordBtn.Visibility);
                Assert.Equal(Visibility.Visible, annotationCanvas.Visibility);
                Assert.Equal(300, annotationCanvas.Width);
                Assert.Equal(180, annotationCanvas.Height);
                Assert.Equal(Visibility.Visible, selectionBorder.Visibility);
                Assert.Equal(Visibility.Collapsed, snapshot.Visibility);
                Assert.Equal(OverlayViewModel.Phase.Annotating, context.ViewModel.CurrentPhase);
            }
            finally
            {
                context.Dispose();
            }
        });
    }

    [Fact]
    public void EnterAnnotatingSession_WithRecordingEnabled_RehostsAndKeepsRecordButtonsVisible()
    {
        StaTestHelper.Run(() =>
        {
            var context = CreateContext();
            try
            {
                context.Window.Left = 200;
                context.Window.Top = 100;
                context.Window.Width = 1000;
                context.Window.Height = 700;
                var originalWidth = context.Window.Width;
                var originalHeight = context.Window.Height;
                var selection = new Rect(100, 120, 600, 320);
                var background = CreateBitmap(600, 320);

                InvokePrivate(context.Window, "EnterAnnotatingSession", selection, background, 1d, 1d, true);

                var recordBtn = Assert.IsType<Button>(context.Window.FindName("RecordBtn"));
                var compactRecordBtn = Assert.IsType<Button>(context.Window.FindName("CompactRecordBtn"));
                var annotationCanvas = Assert.IsType<Canvas>(context.Window.FindName("AnnotationCanvas"));

                Assert.Equal(Visibility.Visible, recordBtn.Visibility);
                Assert.Equal(Visibility.Visible, compactRecordBtn.Visibility);
                Assert.Equal(Visibility.Visible, annotationCanvas.Visibility);
                Assert.True(context.Window.Width > 0 && context.Window.Width <= originalWidth);
                Assert.True(context.Window.Height > 0 && context.Window.Height <= originalHeight);
                Assert.Equal(OverlayViewModel.Phase.Annotating, context.ViewModel.CurrentPhase);
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
        };
        var userSettingsMock = new Mock<IUserSettingsService>();
        userSettingsMock.SetupGet(service => service.Current).Returns(userSettings);

        var viewModel = new OverlayViewModel(
            new AnnotationGeometryService(),
            NullLogger<OverlayViewModel>.Instance,
            userSettingsMock.Object,
            Mock.Of<IDialogService>(),
            Mock.Of<IClipboardService>(),
            Mock.Of<IFileSystemService>(),
            eventAggregator);

        var recorderMock = new Mock<IScreenRecordingService>();
        recorderMock.SetupGet(service => service.IsRecording).Returns(false);
        recorderMock.SetupGet(service => service.IsPaused).Returns(false);
        recorderMock.SetupGet(service => service.CanToggleMicrophone).Returns(true);
        recorderMock.SetupGet(service => service.IsMicrophoneMuted).Returns(false);

        var screenCaptureMock = new Mock<IScreenCaptureService>();
        screenCaptureMock
            .Setup(service => service.Capture(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(CreateBitmap());

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
            Mock.Of<IMessageBoxService>(),
            Mock.Of<IFileSystemService>(),
            Mock.Of<IOcrService>(),
            recordingAnnotationViewModel);

        return new TestContext(window, viewModel, eventAggregator);
    }

    private static object? InvokePrivate(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.Invoke(target, args);
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
        DefaultEventAggregator EventAggregator)
    {
        public void Dispose()
        {
            Window.Close();
            EventAggregator.Dispose();
        }
    }
}
