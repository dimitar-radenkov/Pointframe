using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SnippingTool.Models;
using SnippingTool.Services.Handlers;
using Xunit;

namespace SnippingTool.Tests.Services.Handlers;

public sealed class LineShapeHandlerTests
{
    [Fact]
    public void BeginUpdateCommit_RoundTripTracksLine()
    {
        StaTestHelper.Run(() =>
        {
            var canvas = new Canvas();
            var tracked = new List<UIElement>();
            var start = new Point(10, 20);
            LineShapeParameters current = new(start, new Point(10, 20), Colors.Red, 3);

            var handler = new LineShapeHandler(() => current);

            handler.Begin(start, new SolidColorBrush(current.Color), current.Thickness, canvas);

            var line = Assert.IsType<Line>(Assert.Single(canvas.Children));
            var end = new Point(80, 40);
            current = new LineShapeParameters(start, end, current.Color, current.Thickness);

            handler.Update(end);

            Assert.Equal(end.X, line.X2);
            Assert.Equal(end.Y, line.Y2);

            handler.Commit(canvas, tracked.Add);

            Assert.Single(tracked);
            Assert.Same(line, tracked[0]);
        });
    }

    [Fact]
    public void Commit_WithoutShapeParameters_CancelsLine()
    {
        StaTestHelper.Run(() =>
        {
            var canvas = new Canvas();
            ShapeParameters? current = new LineShapeParameters(new Point(10, 20), new Point(30, 40), Colors.Red, 3);
            var handler = new LineShapeHandler(() => current);

            handler.Begin(new Point(10, 20), new SolidColorBrush(Colors.Red), 3, canvas);
            current = null;

            handler.Commit(canvas, _ => throw new Xunit.Sdk.XunitException("Should not track cancelled line."));

            Assert.Empty(canvas.Children);
        });
    }
}