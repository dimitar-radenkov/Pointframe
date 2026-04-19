using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Pointframe.Models;
using Pointframe.Services.Handlers;
using Xunit;

namespace Pointframe.Tests.Services.Handlers;

public sealed class PenShapeHandlerTests
{
    [Fact]
    public void BeginUpdateCommit_WithTwoPoints_TracksPolyline()
    {
        StaTestHelper.Run(() =>
        {
            var canvas = new Canvas();
            var tracked = new List<UIElement>();
            var start = new Point(10, 20);
            PenShapeParameters current = new(start, Colors.Red, 3);

            var handler = new PenShapeHandler(() => current);

            handler.Begin(start, new SolidColorBrush(current.Color), current.Thickness, canvas);
            handler.Update(new Point(15, 25));
            handler.Update(new Point(20, 30));

            var polyline = Assert.IsType<Polyline>(Assert.Single(canvas.Children));
            Assert.Equal(3, polyline.Points.Count);

            handler.Commit(canvas, tracked.Add);

            Assert.Single(tracked);
            Assert.Same(polyline, tracked[0]);
        });
    }

    [Fact]
    public void Commit_WithSinglePoint_CancelsPolyline()
    {
        StaTestHelper.Run(() =>
        {
            var canvas = new Canvas();
            var start = new Point(10, 20);
            PenShapeParameters current = new(start, Colors.Red, 3);
            var handler = new PenShapeHandler(() => current);

            handler.Begin(start, new SolidColorBrush(current.Color), current.Thickness, canvas);
            handler.Commit(canvas, _ => throw new Xunit.Sdk.XunitException("Should not track cancelled pen stroke."));

            Assert.Empty(canvas.Children);
        });
    }
}
