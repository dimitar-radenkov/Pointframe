using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SnippingTool.Models;

namespace SnippingTool.Services.Handlers;

internal sealed class PenShapeHandler : IAnnotationShapeHandler
{
    private readonly Func<ShapeParameters?> _getShapeParameters;

    private Polyline? _polyline;

    public PenShapeHandler(Func<ShapeParameters?> getShapeParameters)
    {
        _getShapeParameters = getShapeParameters;
    }

    public void Begin(Point point, SolidColorBrush brush, double thickness, Canvas canvas)
    {
        _polyline = new Polyline
        {
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        _polyline.Points.Add(point);
        canvas.Children.Add(_polyline);
    }

    public void Update(Point point)
    {
        if (_polyline is null || _getShapeParameters() is not PenShapeParameters)
        {
            return;
        }

        _polyline.Points.Add(point);
    }

    public void Commit(Canvas canvas, Action<UIElement> trackElement)
    {
        if (_polyline is null || _polyline.Points.Count < 2 || _getShapeParameters() is not PenShapeParameters)
        {
            Cancel(canvas);
            return;
        }

        trackElement(_polyline);
        _polyline = null;
    }

    public void Cancel(Canvas canvas)
    {
        if (_polyline is not null && canvas.Children.Contains(_polyline))
        {
            canvas.Children.Remove(_polyline);
        }

        _polyline = null;
    }
}
