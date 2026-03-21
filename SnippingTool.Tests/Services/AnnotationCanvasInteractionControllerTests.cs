using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SnippingTool.Models;
using SnippingTool.Services;
using SnippingTool.Services.Messaging;
using SnippingTool.Tests.Services.Handlers;
using SnippingTool.ViewModels;
using Xunit;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace SnippingTool.Tests.Services;

public sealed class AnnotationCanvasInteractionControllerTests
{
    [Fact]
    public void HandlePointerDown_WithTextTool_CommitsImmediately()
    {
        StaTestHelper.Run(() =>
        {
            // Arrange
            var controller = CreateController(out var canvas, out var viewModel, out _, out var committedCounter);
            viewModel.SelectedTool = AnnotationTool.Text;

            // Act
            controller.HandlePointerDown(new Point(25, 35));

            // Assert
            Assert.Single(canvas.Children);
            Assert.IsType<TextBox>(canvas.Children[0]);
            Assert.Equal(1, viewModel.UndoCount);
            Assert.Equal(1, committedCounter.Value);
            Assert.False(viewModel.IsDragging);
        });
    }

    [Fact]
    public void HandlePointerUp_WithRectangleTool_CommitsShapeAndGroup()
    {
        StaTestHelper.Run(() =>
        {
            // Arrange
            var controller = CreateController(out var canvas, out var viewModel, out _, out var committedCounter);
            viewModel.SelectedTool = AnnotationTool.Rectangle;

            // Act
            controller.HandlePointerDown(new Point(10, 20));
            controller.HandlePointerMove(new Point(70, 80));
            controller.HandlePointerUp(new Point(70, 80));

            // Assert
            var rectangle = Assert.IsType<Rectangle>(Assert.Single(canvas.Children));
            Assert.Equal(10, Canvas.GetLeft(rectangle));
            Assert.Equal(20, Canvas.GetTop(rectangle));
            Assert.Equal(60, rectangle.Width);
            Assert.Equal(60, rectangle.Height);
            Assert.Equal(1, viewModel.UndoCount);
            Assert.Equal(1, committedCounter.Value);
            Assert.False(viewModel.IsDragging);
        });
    }

    [Fact]
    public void Cancel_WhileDragging_RemovesDraftAndResetsDragState()
    {
        StaTestHelper.Run(() =>
        {
            // Arrange
            var controller = CreateController(out var canvas, out var viewModel, out _, out _);
            viewModel.SelectedTool = AnnotationTool.Circle;

            // Act
            controller.HandlePointerDown(new Point(5, 5));
            controller.HandlePointerMove(new Point(50, 50));

            // Assert
            Assert.Single(canvas.Children);
            Assert.True(viewModel.IsDragging);

            // Act
            controller.Cancel();

            // Assert
            Assert.Empty(canvas.Children);
            Assert.False(viewModel.IsDragging);
            Assert.Equal(0, viewModel.UndoCount);
        });
    }

    private static AnnotationCanvasInteractionController CreateController(
        out Canvas canvas,
        out AnnotationViewModel viewModel,
        out AnnotationCanvasRenderer renderer,
        out Counter committedCounter)
    {
        canvas = new Canvas();
        var counter = new Counter();
        committedCounter = counter;
        var settings = Mock.Of<IUserSettingsService>(service => service.Current == new UserSettings());
        viewModel = new AnnotationViewModel(
            new AnnotationGeometryService(),
            NullLogger<AnnotationViewModel>.Instance,
            settings,
            new DefaultEventAggregator(NullLogger<DefaultEventAggregator>.Instance));
        renderer = new AnnotationCanvasRenderer(canvas, viewModel, viewModel.TrackElement, NullLogger<AnnotationCanvasRenderer>.Instance);

        return new AnnotationCanvasInteractionController(canvas, viewModel, renderer, () => counter.Value++);
    }

    private sealed class Counter
    {
        public int Value { get; set; }
    }
}