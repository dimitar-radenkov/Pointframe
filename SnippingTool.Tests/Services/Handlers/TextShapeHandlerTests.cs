using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SnippingTool.Services.Handlers;
using Xunit;

namespace SnippingTool.Tests.Services.Handlers;

public sealed class TextShapeHandlerTests
{
    [Fact]
    public void BeginCommitAndLostFocus_ReplacesTextBoxWithTextBlock()
    {
        StaTestHelper.Run(() =>
        {
            var canvas = new Canvas();
            var tracked = new List<UIElement>();
            var handler = new TextShapeHandler();

            handler.Begin(new Point(25, 35), new SolidColorBrush(Colors.Green), 2.5, canvas);

            Assert.Single(canvas.Children);
            var textBox = Assert.IsType<TextBox>(canvas.Children[0]);
            textBox.Text = "hello";

            handler.Commit(canvas, tracked.Add);
            textBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, textBox));

            Assert.Single(canvas.Children);
            var block = Assert.IsType<TextBlock>(canvas.Children[0]);
            Assert.Equal("hello", block.Text);

            Assert.Collection(
                tracked,
                element => Assert.Same(textBox, element),
                element => Assert.Same(block, element));
        });
    }
}