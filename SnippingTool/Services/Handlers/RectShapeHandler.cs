using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SnippingTool.Models;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace SnippingTool.Services.Handlers;

internal sealed class RectShapeHandler : IAnnotationShapeHandler
{
    private readonly Func<ShapeParameters?> _getShapeParameters;

    private Rectangle? _rectangle;

    public RectShapeHandler(Func<ShapeParameters?> getShapeParameters)
    {
        _getShapeParameters = getShapeParameters;
    }

    public void Begin(Point point, SolidColorBrush brush, double thickness, Canvas canvas)
    {
        _rectangle = new Rectangle
        {
            Stroke = brush,
            StrokeThickness = thickness,
            Fill = Brushes.Transparent
        };
        Canvas.SetLeft(_rectangle, point.X);
        Canvas.SetTop(_rectangle, point.Y);
        canvas.Children.Add(_rectangle);
    }

    public void Update(Point point)
    {
        if (_rectangle is null || _getShapeParameters() is not RectShapeParameters parameters)
        {
            return;
        }

        Apply(parameters);
    }

    public void Commit(Canvas canvas, Action<UIElement> trackElement)
    {
        if (_rectangle is null || _getShapeParameters() is not RectShapeParameters parameters)
        {
            Cancel(canvas);
            return;
        }

        Apply(parameters);
        trackElement(_rectangle);
        _rectangle = null;
    }

    public void Cancel(Canvas canvas)
    {
        if (_rectangle is not null && canvas.Children.Contains(_rectangle))
        {
            canvas.Children.Remove(_rectangle);
        }

        _rectangle = null;
    }

    private void Apply(RectShapeParameters parameters)
    {
        var rectangle = _rectangle!;
        Canvas.SetLeft(rectangle, parameters.Left);
        Canvas.SetTop(rectangle, parameters.Top);
        rectangle.Width = parameters.Width;
        rectangle.Height = parameters.Height;
    }
}
