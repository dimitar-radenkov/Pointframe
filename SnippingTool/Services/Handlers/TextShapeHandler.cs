using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;

namespace SnippingTool.Services.Handlers;

internal sealed class TextShapeHandler : IAnnotationShapeHandler
{
    private readonly Action<UIElement, UIElement> _replaceTrackedElement;
    private readonly Action<UIElement> _removeTrackedElement;
    private TextBox? _textBox;

    public TextShapeHandler(Action<UIElement, UIElement> replaceTrackedElement, Action<UIElement> removeTrackedElement)
    {
        _replaceTrackedElement = replaceTrackedElement;
        _removeTrackedElement = removeTrackedElement;
    }

    public void Begin(Point point, SolidColorBrush brush, double thickness, Canvas canvas)
    {
        var textBox = new TextBox
        {
            FontSize = 16,
            Foreground = brush,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
            MinWidth = 80,
            AcceptsReturn = false,
            Padding = new Thickness(2)
        };
        textBox.LostFocus += (_, _) => FinalizeTextBox(textBox, canvas);
        textBox.KeyDown += (_, keyEventArgs) =>
        {
            if (keyEventArgs.Key is Key.Enter or Key.Escape)
            {
                FinalizeTextBox(textBox, canvas);
                keyEventArgs.Handled = true;
            }
        };
        Canvas.SetLeft(textBox, point.X);
        Canvas.SetTop(textBox, point.Y);
        canvas.Children.Add(textBox);
        textBox.Focus();
        _textBox = textBox;
    }

    public void Update(Point point)
    {
    }

    public void Commit(Canvas canvas, Action<UIElement> trackElement)
    {
        if (_textBox is not null && canvas.Children.Contains(_textBox))
        {
            trackElement(_textBox);
        }

        _textBox = null;
    }

    public void Cancel(Canvas canvas)
    {
        if (_textBox is not null && canvas.Children.Contains(_textBox))
        {
            canvas.Children.Remove(_textBox);
        }

        _textBox = null;
    }

    private void FinalizeTextBox(TextBox textBox, Canvas canvas)
    {
        if (!canvas.Children.Contains(textBox))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(textBox.Text))
        {
            canvas.Children.Remove(textBox);
            _removeTrackedElement(textBox);
            return;
        }

        var position = new Point(Canvas.GetLeft(textBox), Canvas.GetTop(textBox));
        var text = textBox.Text;
        var foreground = textBox.Foreground;
        canvas.Children.Remove(textBox);

        var block = new TextBlock
        {
            Text = text,
            Foreground = foreground,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        };
        Canvas.SetLeft(block, position.X);
        Canvas.SetTop(block, position.Y);
        canvas.Children.Add(block);
        _replaceTrackedElement(textBox, block);
    }
}
