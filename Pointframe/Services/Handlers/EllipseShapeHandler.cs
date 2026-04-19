using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Pointframe.Models;

namespace Pointframe.Services.Handlers;

internal sealed class EllipseShapeHandler : IAnnotationShapeHandler
{
    private readonly Func<ShapeParameters?> _getShapeParameters;

    private Ellipse? _ellipse;

    public EllipseShapeHandler(Func<ShapeParameters?> getShapeParameters)
    {
        _getShapeParameters = getShapeParameters;
    }

    public void Begin(Point point, SolidColorBrush brush, double thickness, Canvas canvas)
    {
        _ellipse = new Ellipse
        {
            Stroke = brush,
            StrokeThickness = thickness,
            Fill = Brushes.Transparent
        };
        Canvas.SetLeft(_ellipse, point.X);
        Canvas.SetTop(_ellipse, point.Y);
        canvas.Children.Add(_ellipse);
    }

    public void Update(Point point)
    {
        if (_ellipse is null || _getShapeParameters() is not EllipseShapeParameters parameters)
        {
            return;
        }

        Apply(parameters);
    }

    public void Commit(Canvas canvas, Action<UIElement> trackElement)
    {
        if (_ellipse is null || _getShapeParameters() is not EllipseShapeParameters parameters)
        {
            Cancel(canvas);
            return;
        }

        Apply(parameters);
        trackElement(_ellipse);
        _ellipse = null;
    }

    public void Cancel(Canvas canvas)
    {
        if (_ellipse is not null && canvas.Children.Contains(_ellipse))
        {
            canvas.Children.Remove(_ellipse);
        }

        _ellipse = null;
    }

    private void Apply(EllipseShapeParameters parameters)
    {
        var ellipse = _ellipse!;
        Canvas.SetLeft(ellipse, parameters.Left);
        Canvas.SetTop(ellipse, parameters.Top);
        ellipse.Width = parameters.Width;
        ellipse.Height = parameters.Height;
    }
}
