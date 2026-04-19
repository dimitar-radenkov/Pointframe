using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Pointframe.Services.Handlers;
using Xunit;

namespace Pointframe.Tests.Services.Handlers;

public sealed class NumberShapeHandlerTests
{
    [Fact]
    public void BeginCommit_CreatesCenteredBadgeAndTracksIt()
    {
        StaTestHelper.Run(() =>
        {
            var canvas = new Canvas();
            var tracked = new List<UIElement>();
            var handler = new NumberShapeHandler(() => 7);
            var point = new Point(100, 80);

            handler.Begin(point, new SolidColorBrush(Colors.OrangeRed), 0, canvas);

            var badge = Assert.IsType<Grid>(Assert.Single(canvas.Children));
            Assert.Equal("number", badge.Tag);
            Assert.Equal(28, badge.Width);
            Assert.Equal(28, badge.Height);
            Assert.Equal(86, Canvas.GetLeft(badge));
            Assert.Equal(66, Canvas.GetTop(badge));

            Assert.IsType<Ellipse>(badge.Children[0]);
            var text = Assert.IsType<TextBlock>(badge.Children[1]);
            Assert.Equal("7", text.Text);

            handler.Commit(canvas, tracked.Add);

            Assert.Single(tracked);
            Assert.Same(badge, tracked[0]);
        });
    }

    [Fact]
    public void Cancel_RemovesBadgeFromCanvas()
    {
        StaTestHelper.Run(() =>
        {
            var canvas = new Canvas();
            var handler = new NumberShapeHandler(() => 1);

            handler.Begin(new Point(20, 20), new SolidColorBrush(Colors.Blue), 0, canvas);
            handler.Cancel(canvas);

            Assert.Empty(canvas.Children);
        });
    }
}
