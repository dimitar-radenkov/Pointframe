using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SnippingTool.Services.Handlers;
using Xunit;

namespace SnippingTool.Tests.Services.Handlers;

public sealed class TextShapeHandlerTests
{
    [Fact]
    public void BeginCommitAndLostFocus_ReplacesTrackedTextBoxWithTextBlock()
    {
        StaTestHelper.Run(() =>
        {
            var canvas = new Canvas();
            var tracked = new List<UIElement>();
            var replacements = new List<(UIElement Original, UIElement Replacement)>();
            var handler = new TextShapeHandler(
                (original, replacement) => replacements.Add((original, replacement)),
                _ => { });

            handler.Begin(new Point(25, 35), new SolidColorBrush(Colors.Green), 2.5, canvas);

            Assert.Single(canvas.Children);
            var textBox = Assert.IsType<TextBox>(canvas.Children[0]);
            textBox.Text = "hello";

            handler.Commit(canvas, tracked.Add);
            textBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, textBox));

            Assert.Single(canvas.Children);
            var block = Assert.IsType<TextBlock>(canvas.Children[0]);
            Assert.Equal("hello", block.Text);
            var replacement = Assert.Single(replacements);
            Assert.Same(textBox, replacement.Original);
            Assert.Same(block, replacement.Replacement);

            Assert.Collection(
                tracked,
                element => Assert.Same(textBox, element));
        });
    }

    [Fact]
    public void BeginCommitAndLostFocus_WithEmptyText_RemovesTrackedTextBox()
    {
        StaTestHelper.Run(() =>
        {
            var canvas = new Canvas();
            var removed = new List<UIElement>();
            var handler = new TextShapeHandler(
                (_, _) => { },
                removed.Add);

            handler.Begin(new Point(25, 35), new SolidColorBrush(Colors.Green), 2.5, canvas);

            var textBox = Assert.IsType<TextBox>(canvas.Children[0]);
            handler.Commit(canvas, _ => { });
            textBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, textBox));

            Assert.Empty(canvas.Children);
            Assert.Collection(removed, element => Assert.Same(textBox, element));
        });
    }
}