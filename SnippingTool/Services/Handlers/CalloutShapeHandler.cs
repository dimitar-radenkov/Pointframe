using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Pointframe.Models;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Pointframe.Services.Handlers;

internal sealed class CalloutShapeHandler : IAnnotationShapeHandler
{
    private readonly Func<ShapeParameters?> _getShapeParameters;
    private readonly Action<UIElement, UIElement> _replaceTrackedElement;
    private readonly Action<UIElement> _removeTrackedElement;
    private readonly Action? _onCanvasChanged;

    private Rectangle? _bubble;
    private Polygon? _tail;
    private Color _textColor;

    public CalloutShapeHandler(
        Func<ShapeParameters?> getShapeParameters,
        Action<UIElement, UIElement> replaceTrackedElement,
        Action<UIElement> removeTrackedElement,
        Action? onCanvasChanged = null)
    {
        _getShapeParameters = getShapeParameters;
        _replaceTrackedElement = replaceTrackedElement;
        _removeTrackedElement = removeTrackedElement;
        _onCanvasChanged = onCanvasChanged;
    }

    public void Begin(Point point, SolidColorBrush brush, double thickness, Canvas canvas)
    {
        _textColor = brush.Color;
        _bubble = new Rectangle
        {
            RadiusX = 6,
            RadiusY = 6,
            Stroke = brush,
            StrokeThickness = thickness,
            Fill = Brushes.White
        };
        Canvas.SetLeft(_bubble, point.X);
        Canvas.SetTop(_bubble, point.Y);

        _tail = new Polygon
        {
            Stroke = brush,
            StrokeThickness = thickness,
            Fill = Brushes.White,
            StrokeLineJoin = PenLineJoin.Round
        };

        canvas.Children.Add(_bubble);
        canvas.Children.Add(_tail);
    }

    public void Update(Point point)
    {
        if (_bubble is null
            || _tail is null
            || _getShapeParameters() is not CalloutShapeParameters parameters)
        {
            return;
        }

        Apply(parameters);
    }

    public void Commit(Canvas canvas, Action<UIElement> trackElement)
    {
        if (_bubble is null
            || _tail is null
            || _getShapeParameters() is not CalloutShapeParameters parameters)
        {
            Cancel(canvas);
            return;
        }

        Apply(parameters);
        trackElement(_bubble);
        trackElement(_tail);

        var textBox = CreateTextBox(parameters, canvas);
        canvas.Children.Add(textBox);
        trackElement(textBox);
        _onCanvasChanged?.Invoke();
        textBox.Focus();

        _bubble = null;
        _tail = null;
    }

    public void Cancel(Canvas canvas)
    {
        if (_bubble is not null && canvas.Children.Contains(_bubble))
        {
            canvas.Children.Remove(_bubble);
        }

        if (_tail is not null && canvas.Children.Contains(_tail))
        {
            canvas.Children.Remove(_tail);
        }

        _bubble = null;
        _tail = null;
    }

    private void Apply(CalloutShapeParameters parameters)
    {
        var bubble = _bubble!;
        var tail = _tail!;
        Canvas.SetLeft(bubble, parameters.Left);
        Canvas.SetTop(bubble, parameters.Top);
        bubble.Width = parameters.Width;
        bubble.Height = parameters.Height;

        tail.Points.Clear();
        tail.Points.Add(new Point(parameters.Left + parameters.Width * 0.15, parameters.Top + parameters.Height));
        tail.Points.Add(new Point(parameters.Left + parameters.Width * 0.35, parameters.Top + parameters.Height));
        tail.Points.Add(parameters.Tail);
    }

    private TextBox CreateTextBox(CalloutShapeParameters parameters, Canvas canvas)
    {
        var textBox = new TextBox
        {
            FontSize = 14,
            Foreground = new SolidColorBrush(_textColor),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Width = Math.Max(parameters.Width - 16, 40),
            Padding = new Thickness(2)
        };
        textBox.LostFocus += (_, _) => FinalizeTextBox(textBox, canvas);
        textBox.KeyDown += (_, keyEventArgs) =>
        {
            if (keyEventArgs.Key == Key.Escape)
            {
                FinalizeTextBox(textBox, canvas);
                keyEventArgs.Handled = true;
            }
        };
        Canvas.SetLeft(textBox, parameters.Left + 8);
        Canvas.SetTop(textBox, parameters.Top + 8);
        return textBox;
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
            _onCanvasChanged?.Invoke();
            return;
        }

        var left = Canvas.GetLeft(textBox);
        var top = Canvas.GetTop(textBox);
        var width = textBox.Width;
        var text = textBox.Text;
        var foreground = textBox.Foreground;
        canvas.Children.Remove(textBox);

        var block = new TextBlock
        {
            Text = text,
            Foreground = foreground,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Width = width
        };
        Canvas.SetLeft(block, left);
        Canvas.SetTop(block, top);
        canvas.Children.Add(block);
        _replaceTrackedElement(textBox, block);
        _onCanvasChanged?.Invoke();
    }
}
