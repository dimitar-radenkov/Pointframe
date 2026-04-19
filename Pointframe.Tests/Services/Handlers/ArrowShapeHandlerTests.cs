using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Pointframe.Models;
using Pointframe.Services;
using Pointframe.Services.Handlers;
using Xunit;

namespace Pointframe.Tests.Services.Handlers;

public sealed class ArrowShapeHandlerTests
{
    [Fact]
    public void BeginUpdateCommit_RoundTripTracksArrowElements()
    {
        StaTestHelper.Run(() =>
        {
            var geometry = new AnnotationGeometryService();
            var canvas = new Canvas();
            var tracked = new List<UIElement>();

            var start = new Point(10, 20);
            var end = new Point(80, 40);
            ArrowShapeParameters current = new(
                start,
                end,
                Colors.Red,
                3,
                geometry.CalculateArrowHead(start, end));

            var handler = new ArrowShapeHandler(() => current);

            handler.Begin(start, new SolidColorBrush(current.Color), current.Thickness, canvas);

            Assert.Equal(2, canvas.Children.Count);
            var shaft = Assert.IsType<Line>(canvas.Children[0]);
            var head = Assert.IsType<Polyline>(canvas.Children[1]);

            var movedEnd = new Point(120, 60);
            current = new ArrowShapeParameters(
                start,
                movedEnd,
                current.Color,
                current.Thickness,
                geometry.CalculateArrowHead(start, movedEnd));

            handler.Update(movedEnd);

            Assert.Equal(movedEnd.X, shaft.X2);
            Assert.Equal(movedEnd.Y, shaft.Y2);
            Assert.Equal(3, head.Points.Count);

            handler.Commit(canvas, tracked.Add);

            Assert.Collection(
                tracked,
                element => Assert.Same(shaft, element),
                element => Assert.Same(head, element));
        });
    }
}
