using System.Windows;
using System.Windows.Controls;
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
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Pointframe.Tests.Services;

public sealed class AnnotationCanvasRendererTests
{
    [Fact]
    public void SetBackground_StoresBitmapAndDpi()
    {
        StaTestHelper.Run(() =>
        {
            var renderer = CreateRenderer(out _, out _);
            var background = CreateSolidBitmap(20, 10, Colors.Blue);

            renderer.SetBackground(background, 2.0, 1.5);

            Assert.Same(background, renderer.BackgroundCapture);
        });
    }

    [Fact]
    public void BeginUpdateCommit_WithRectangle_TracksCommittedElement()
    {
        StaTestHelper.Run(() =>
        {
            var renderer = CreateRenderer(out var canvas, out var vm, out var tracked);
            var start = new Point(10, 20);
            var end = new Point(70, 80);

            vm.SelectedTool = AnnotationTool.Rectangle;
            vm.BeginDrawing(start);
            renderer.BeginShape(start);
            vm.UpdateDrawing(end);
            renderer.UpdateShape(end);
            renderer.CommitShape(end);

            var rectangle = Assert.IsType<Rectangle>(Assert.Single(canvas.Children));
            Assert.Single(tracked);
            Assert.Same(rectangle, tracked[0]);
            Assert.Equal(10, Canvas.GetLeft(rectangle));
            Assert.Equal(20, Canvas.GetTop(rectangle));
            Assert.Equal(60, rectangle.Width);
            Assert.Equal(60, rectangle.Height);
        });
    }

    [Fact]
    public void CancelShape_RemovesActiveElement()
    {
        StaTestHelper.Run(() =>
        {
            var renderer = CreateRenderer(out var canvas, out var vm);
            var start = new Point(5, 5);
            var end = new Point(50, 50);

            vm.SelectedTool = AnnotationTool.Circle;
            vm.BeginDrawing(start);
            vm.UpdateDrawing(end);
            renderer.BeginShape(start);

            Assert.Single(canvas.Children);

            renderer.CancelShape();

            Assert.Empty(canvas.Children);
        });
    }

    [Fact]
    public void BeginShape_WithUnknownTool_IsNoOp()
    {
        StaTestHelper.Run(() =>
        {
            var renderer = CreateRenderer(out var canvas, out var vm);

            vm.SelectedTool = (AnnotationTool)999;
            renderer.BeginShape(new Point(1, 1));
            renderer.UpdateShape(new Point(2, 2));
            renderer.CommitShape(new Point(3, 3));

            Assert.Empty(canvas.Children);
        });
    }

    [Fact]
    public void BeginCommit_WithNumberTool_TracksBadge()
    {
        StaTestHelper.Run(() =>
        {
            var renderer = CreateRenderer(out var canvas, out var vm, out var tracked);
            var point = new Point(40, 50);

            vm.SelectedTool = AnnotationTool.Number;
            renderer.BeginShape(point);
            renderer.CommitShape(point);

            var badge = Assert.IsType<Grid>(Assert.Single(canvas.Children));
            Assert.Single(tracked);
            Assert.Same(badge, tracked[0]);
        });
    }

    private static AnnotationCanvasRenderer CreateRenderer(out Canvas canvas, out AnnotationViewModel vm)
    {
        var renderer = CreateRenderer(out canvas, out vm, out _);
        return renderer;
    }

    private static AnnotationCanvasRenderer CreateRenderer(out Canvas canvas, out AnnotationViewModel vm, out List<UIElement> tracked)
    {
        canvas = new Canvas();
        tracked = [];
        var settings = Mock.Of<IUserSettingsService>(service => service.Current == new UserSettings());
        vm = new AnnotationViewModel(
            new AnnotationGeometryService(),
            NullLogger<AnnotationViewModel>.Instance,
            settings,
            new DefaultEventAggregator(NullLogger<DefaultEventAggregator>.Instance));

        return new AnnotationCanvasRenderer(canvas, vm, tracked.Add, NullLogger<AnnotationCanvasRenderer>.Instance);
    }

    private static BitmapSource CreateSolidBitmap(int width, int height, Color color)
    {
        var pixels = new byte[width * height * 4];

        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = color.A;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
