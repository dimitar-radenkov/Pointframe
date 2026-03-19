using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SnippingTool.Models;
using SnippingTool.Services.Handlers;
using Xunit;

namespace SnippingTool.Tests.Services.Handlers;

public sealed class RectShapeHandlerTests
{
    [Fact]
    public void BeginUpdateCommit_RoundTripTracksRectangle()
    {
        StaTestHelper.Run(() =>
        {
            var canvas = new Canvas();
            var tracked = new List<UIElement>();
            RectShapeParameters current = new(10, 20, 30, 40, Colors.Blue, 2.5);

            var handler = new RectShapeHandler(() => current);

            handler.Begin(new Point(10, 20), new SolidColorBrush(current.Color), current.Thickness, canvas);

            Assert.Single(canvas.Children);
            var rectangle = Assert.IsType<System.Windows.Shapes.Rectangle>(canvas.Children[0]);

            current = new RectShapeParameters(5, 15, 80, 60, current.Color, current.Thickness);
            handler.Update(new Point(85, 75));

            Assert.Equal(5, Canvas.GetLeft(rectangle));
            Assert.Equal(15, Canvas.GetTop(rectangle));
            Assert.Equal(80, rectangle.Width);
            Assert.Equal(60, rectangle.Height);

            handler.Commit(canvas, tracked.Add);

            Assert.Single(tracked);
            Assert.Same(rectangle, tracked[0]);
        });
    }
}