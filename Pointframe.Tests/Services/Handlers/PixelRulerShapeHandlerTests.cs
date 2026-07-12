using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Pointframe.Models;
using Pointframe.Services.Handlers;
using Xunit;

namespace Pointframe.Tests.Services.Handlers;

public sealed class PixelRulerShapeHandlerTests
{
    [Fact]
    public void BeginUpdateCommit_RoundTripTracksRulerContainer()
    {
        StaTestHelper.Run(() =>
        {
            var canvas = new Canvas();
            var tracked = new List<UIElement>();
            var p1 = new Point(0, 0);
            var p2 = new Point(100, 0);
            ShapeParameters? current = new PixelRulerShapeParameters(p1, p2, Colors.Red, 2, 1.0, 1.0);

            var handler = new PixelRulerShapeHandler(() => current);

            handler.Begin(p1, new SolidColorBrush(Colors.Red), 2, canvas);

            var container = Assert.IsType<Canvas>(Assert.Single(canvas.Children));

            handler.Update(p2);

            // Shaft line runs exactly from P1 to P2.
            Assert.Contains(
                container.Children.OfType<Line>(),
                line => line.X1 == p1.X && line.Y1 == p1.Y && line.X2 == p2.X && line.Y2 == p2.Y);

            // Two endpoint dots.
            Assert.Equal(2, container.Children.OfType<Ellipse>().Count());

            handler.Commit(canvas, tracked.Add);

            Assert.Same(container, Assert.Single(tracked));
        });
    }

    [Fact]
    public void Update_LabelReportsDpiScaledPixelLength()
    {
        StaTestHelper.Run(() =>
        {
            var canvas = new Canvas();
            // 100 DIPs at 2.0 horizontal DPI scale => 200 physical pixels.
            ShapeParameters? current = new PixelRulerShapeParameters(
                new Point(0, 0), new Point(100, 0), Colors.Red, 2, 2.0, 2.0);

            var handler = new PixelRulerShapeHandler(() => current);
            handler.Begin(new Point(0, 0), new SolidColorBrush(Colors.Red), 2, canvas);

            handler.Update(new Point(100, 0));

            var container = Assert.IsType<Canvas>(Assert.Single(canvas.Children));
            var label = container.Children.OfType<Border>()
                .Select(border => border.Child)
                .OfType<TextBlock>()
                .Single();

            Assert.Equal("200 px", label.Text);
        });
    }

    [Fact]
    public void Commit_WithoutShapeParameters_CancelsRuler()
    {
        StaTestHelper.Run(() =>
        {
            var canvas = new Canvas();
            ShapeParameters? current = new PixelRulerShapeParameters(
                new Point(0, 0), new Point(30, 40), Colors.Red, 2, 1.0, 1.0);
            var handler = new PixelRulerShapeHandler(() => current);

            handler.Begin(new Point(0, 0), new SolidColorBrush(Colors.Red), 2, canvas);
            current = null;

            handler.Commit(canvas, _ => throw new Xunit.Sdk.XunitException("Should not track cancelled ruler."));

            Assert.Empty(canvas.Children);
        });
    }
}
