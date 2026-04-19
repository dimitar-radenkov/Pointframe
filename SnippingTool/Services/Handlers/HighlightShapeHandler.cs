using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pointframe.Models;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Pointframe.Services.Handlers;

internal sealed class HighlightShapeHandler : IAnnotationShapeHandler
{
    private readonly Func<ShapeParameters?> _getShapeParameters;

    private Rectangle? _rectangle;

    public HighlightShapeHandler(Func<ShapeParameters?> getShapeParameters)
    {
        _getShapeParameters = getShapeParameters;
    }

    public void Begin(Point point, SolidColorBrush brush, double thickness, Canvas canvas)
    {
        _rectangle = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(100, brush.Color.R, brush.Color.G, brush.Color.B))
        };
        Canvas.SetLeft(_rectangle, point.X);
        Canvas.SetTop(_rectangle, point.Y);
        canvas.Children.Add(_rectangle);
    }

    public void Update(Point point)
    {
        if (_rectangle is null || _getShapeParameters() is not HighlightShapeParameters parameters)
        {
            return;
        }

        Apply(parameters);
    }

    public void Commit(Canvas canvas, Action<UIElement> trackElement)
    {
        if (_rectangle is null || _getShapeParameters() is not HighlightShapeParameters parameters)
        {
            Cancel(canvas);
            return;
        }

        Apply(parameters);
        _rectangle.Fill = new SolidColorBrush(Color.FromArgb(100, parameters.BaseColor.R, parameters.BaseColor.G, parameters.BaseColor.B));
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

    private void Apply(HighlightShapeParameters parameters)
    {
        var rectangle = _rectangle!;
        Canvas.SetLeft(rectangle, parameters.Left);
        Canvas.SetTop(rectangle, parameters.Top);
        rectangle.Width = parameters.Width;
        rectangle.Height = parameters.Height;
    }
}
