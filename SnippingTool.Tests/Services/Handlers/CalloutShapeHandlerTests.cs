using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Pointframe.Models;
using Pointframe.Services.Handlers;
using Xunit;

namespace Pointframe.Tests.Services.Handlers;

public sealed class CalloutShapeHandlerTests
{
    [Fact]
    public void CommitAndLostFocus_ReplacesTrackedTextBoxWithTextBlock()
    {
        StaTestHelper.Run(() =>
        {
            var canvas = new Canvas();
            var tracked = new List<UIElement>();
            var replacements = new List<(UIElement Original, UIElement Replacement)>();
            var handler = new CalloutShapeHandler(
                () => new CalloutShapeParameters(20, 30, 120, 60, new Point(10, 110), string.Empty, Colors.White, Colors.Black, 2.0),
                (original, replacement) => replacements.Add((original, replacement)),
                _ => { });

            handler.Begin(new Point(20, 30), new SolidColorBrush(Colors.Black), 2.0, canvas);
            handler.Update(new Point(140, 90));
            handler.Commit(canvas, tracked.Add);

            var textBox = Assert.IsType<TextBox>(canvas.Children[2]);
            textBox.Text = "callout";
            textBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, textBox));

            Assert.IsType<Rectangle>(canvas.Children[0]);
            Assert.IsType<Polygon>(canvas.Children[1]);
            var block = Assert.IsType<TextBlock>(canvas.Children[2]);
            Assert.Equal("callout", block.Text);

            Assert.Equal(3, tracked.Count);
            Assert.Same(textBox, tracked[2]);
            var replacement = Assert.Single(replacements);
            Assert.Same(textBox, replacement.Original);
            Assert.Same(block, replacement.Replacement);
        });
    }

    [Fact]
    public void BeginCommitAndFinalize_InvokesCanvasChangedCallback()
    {
        StaTestHelper.Run(() =>
        {
            // Arrange
            var canvas = new Canvas();
            var canvasChangedCount = 0;
            var handler = new CalloutShapeHandler(
                () => new CalloutShapeParameters(20, 30, 120, 60, new Point(10, 110), string.Empty, Colors.White, Colors.Black, 2.0),
                (_, _) => { },
                _ => { },
                () => canvasChangedCount++);

            // Act
            handler.Begin(new Point(20, 30), new SolidColorBrush(Colors.Black), 2.0, canvas);
            handler.Update(new Point(140, 90));
            handler.Commit(canvas, _ => { });

            var textBox = Assert.IsType<TextBox>(canvas.Children[2]);
            textBox.Text = "callout";
            textBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, textBox));

            // Assert
            Assert.Equal(2, canvasChangedCount);
            Assert.IsType<TextBlock>(canvas.Children[2]);
        });
    }
}
