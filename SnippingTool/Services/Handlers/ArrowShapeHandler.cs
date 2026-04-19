using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Pointframe.Models;

namespace Pointframe.Services.Handlers;

internal sealed class ArrowShapeHandler : IAnnotationShapeHandler
{
    private readonly Func<ShapeParameters?> _getShapeParameters;

    private Line? _shaft;
    private Polyline? _head;

    public ArrowShapeHandler(Func<ShapeParameters?> getShapeParameters)
    {
        _getShapeParameters = getShapeParameters;
    }

    public void Begin(Point point, SolidColorBrush brush, double thickness, Canvas canvas)
    {
        _shaft = new Line
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
        _head = new Polyline
        {
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        canvas.Children.Add(_shaft);
        canvas.Children.Add(_head);
    }

    public void Update(Point point)
    {
        if (_shaft is null
            || _head is null
            || _getShapeParameters() is not ArrowShapeParameters parameters)
        {
            return;
        }

        Apply(parameters);
    }

    public void Commit(Canvas canvas, Action<UIElement> trackElement)
    {
        if (_shaft is null
            || _head is null
            || _getShapeParameters() is not ArrowShapeParameters parameters)
        {
            Cancel(canvas);
            return;
        }

        Apply(parameters);
        trackElement(_shaft);
        trackElement(_head);
        Reset();
    }

    public void Cancel(Canvas canvas)
    {
        if (_shaft is not null && canvas.Children.Contains(_shaft))
        {
            canvas.Children.Remove(_shaft);
        }

        if (_head is not null && canvas.Children.Contains(_head))
        {
            canvas.Children.Remove(_head);
        }

        Reset();
    }

    private void Apply(ArrowShapeParameters parameters)
    {
        _shaft!.X2 = parameters.P2.X;
        _shaft.Y2 = parameters.P2.Y;
        _head!.Points.Clear();
        foreach (var point in parameters.ArrowHead)
        {
            _head.Points.Add(point);
        }
    }

    private void Reset()
    {
        _shaft = null;
        _head = null;
    }
}
