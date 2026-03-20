using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SnippingTool.Models;
using SnippingTool.Services.Handlers;
using Xunit;

namespace SnippingTool.Tests.Services.Handlers;

public sealed class EllipseShapeHandlerTests
{
    [Fact]
    public void BeginUpdateCommit_RoundTripTracksEllipse()
    {
        StaTestHelper.Run(() =>
        {
            var canvas = new Canvas();
            var tracked = new List<UIElement>();
            EllipseShapeParameters current = new(10, 20, 30, 40, Colors.Blue, 2.5);

            var handler = new EllipseShapeHandler(() => current);

            handler.Begin(new Point(10, 20), new SolidColorBrush(current.Color), current.Thickness, canvas);

            var ellipse = Assert.IsType<Ellipse>(Assert.Single(canvas.Children));
            Assert.Equal(current.Thickness, ellipse.StrokeThickness);

            current = new EllipseShapeParameters(5, 15, 80, 60, current.Color, current.Thickness);
            handler.Update(new Point(85, 75));

            Assert.Equal(5, Canvas.GetLeft(ellipse));
            Assert.Equal(15, Canvas.GetTop(ellipse));
            Assert.Equal(80, ellipse.Width);
            Assert.Equal(60, ellipse.Height);

            handler.Commit(canvas, tracked.Add);

            Assert.Single(tracked);
            Assert.Same(ellipse, tracked[0]);
        });
    }

    [Fact]
    public void Commit_WithoutShapeParameters_CancelsEllipse()
    {
        StaTestHelper.Run(() =>
        {
            var canvas = new Canvas();
            ShapeParameters? current = new EllipseShapeParameters(10, 20, 30, 40, Colors.Blue, 2.5);
            var handler = new EllipseShapeHandler(() => current);

            handler.Begin(new Point(10, 20), new SolidColorBrush(Colors.Blue), 2.5, canvas);
            current = null;

            handler.Commit(canvas, _ => throw new Xunit.Sdk.XunitException("Should not track cancelled ellipse."));

            Assert.Empty(canvas.Children);
        });
    }
}