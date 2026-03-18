using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SnippingTool.Models;
using Point = System.Windows.Point;

namespace SnippingTool.Services.Handlers;

internal sealed class LineShapeHandler : IAnnotationShapeHandler
{
    private readonly Func<ShapeParameters?> _getShapeParameters;

    private Line? _line;

    public LineShapeHandler(Func<ShapeParameters?> getShapeParameters)
    {
        _getShapeParameters = getShapeParameters;
    }

    public void Begin(Point point, SolidColorBrush brush, double thickness, Canvas canvas)
    {
        _line = new Line
        {
            X1 = point.X,
            Y1 = point.Y,
            X2 = point.X,
            Y2 = point.Y,
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        canvas.Children.Add(_line);
    }

    public void Update(Point point)
    {
        if (_line is null || _getShapeParameters() is not LineShapeParameters parameters)
        {
            return;
        }

        _line.X2 = parameters.P2.X;
        _line.Y2 = parameters.P2.Y;
    }

    public void Commit(Canvas canvas, Action<UIElement> trackElement)
    {
        if (_line is null || _getShapeParameters() is not LineShapeParameters parameters)
        {
            Cancel(canvas);
            return;
        }

        _line.X2 = parameters.P2.X;
        _line.Y2 = parameters.P2.Y;
        trackElement(_line);
        _line = null;
    }

    public void Cancel(Canvas canvas)
    {
        if (_line is not null && canvas.Children.Contains(_line))
        {
            canvas.Children.Remove(_line);
        }

        _line = null;
    }
}
