using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SnippingTool.Models;
using SnippingTool.Services.Handlers;
using Xunit;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace SnippingTool.Tests.Services.Handlers;

public sealed class HighlightShapeHandlerTests
{
    [Fact]
    public void BeginUpdateCommit_RoundTripTracksHighlight()
    {
        StaTestHelper.Run(() =>
        {
            var canvas = new Canvas();
            var tracked = new List<UIElement>();
            HighlightShapeParameters current = new(10, 20, 30, 40, Colors.Yellow);

            var handler = new HighlightShapeHandler(() => current);

            handler.Begin(new Point(10, 20), new SolidColorBrush(current.BaseColor), 2, canvas);

            var rectangle = Assert.IsType<Rectangle>(Assert.Single(canvas.Children));
            current = new HighlightShapeParameters(5, 15, 80, 60, Colors.LimeGreen);

            handler.Update(new Point(85, 75));

            Assert.Equal(5, Canvas.GetLeft(rectangle));
            Assert.Equal(15, Canvas.GetTop(rectangle));
            Assert.Equal(80, rectangle.Width);
            Assert.Equal(60, rectangle.Height);

            handler.Commit(canvas, tracked.Add);

            var fillBrush = Assert.IsType<SolidColorBrush>(rectangle.Fill);
            Assert.Equal(Color.FromArgb(100, Colors.LimeGreen.R, Colors.LimeGreen.G, Colors.LimeGreen.B), fillBrush.Color);
            Assert.Single(tracked);
            Assert.Same(rectangle, tracked[0]);
        });
    }

    [Fact]
    public void Commit_WithoutShapeParameters_CancelsHighlight()
    {
        StaTestHelper.Run(() =>
        {
            var canvas = new Canvas();
            ShapeParameters? current = new HighlightShapeParameters(10, 20, 30, 40, Colors.Yellow);
            var handler = new HighlightShapeHandler(() => current);

            handler.Begin(new Point(10, 20), new SolidColorBrush(Colors.Yellow), 2, canvas);
            current = null;

            handler.Commit(canvas, _ => throw new Xunit.Sdk.XunitException("Should not track cancelled highlight."));

            Assert.Empty(canvas.Children);
        });
    }
}