using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Pointframe.Models;

namespace Pointframe.Services.Handlers;

internal sealed class PixelRulerShapeHandler : IAnnotationShapeHandler
{
    private readonly Func<ShapeParameters?> _getShapeParameters;
    private Canvas? _container;

    public PixelRulerShapeHandler(Func<ShapeParameters?> getShapeParameters)
        => _getShapeParameters = getShapeParameters;

    public void Begin(Point point, SolidColorBrush brush, double thickness, Canvas canvas)
    {
        _container = new Canvas { IsHitTestVisible = false };
        Canvas.SetLeft(_container, 0);
        Canvas.SetTop(_container, 0);
        canvas.Children.Add(_container);
    }

    public void Update(Point point)
    {
        if (_container is null || _getShapeParameters() is not PixelRulerShapeParameters p)
        {
            return;
        }

        Rebuild(p);
    }

    public void Commit(Canvas canvas, Action<UIElement> trackElement)
    {
        if (_container is null || _getShapeParameters() is not PixelRulerShapeParameters p)
        {
            Cancel(canvas);
            return;
        }

        Rebuild(p);
        trackElement(_container);
        _container = null;
    }

    public void Cancel(Canvas canvas)
    {
        if (_container is not null && canvas.Children.Contains(_container))
        {
            canvas.Children.Remove(_container);
        }

        _container = null;
    }

    private void Rebuild(PixelRulerShapeParameters p)
    {
        if (_container is null)
        {
            return;
        }

        _container.Children.Clear();

        var brush = new SolidColorBrush(p.Color);
        brush.Freeze();

        var dx = p.P2.X - p.P1.X;
        var dy = p.P2.Y - p.P1.Y;
        var dipLen = Math.Sqrt(dx * dx + dy * dy);

        // Main line
        var shaft = new Line
        {
            X1 = p.P1.X,
            Y1 = p.P1.Y,
            X2 = p.P2.X,
            Y2 = p.P2.Y,
            Stroke = brush,
            StrokeThickness = p.Thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        _container.Children.Add(shaft);

        // Endpoint dots
        AddEndpointDot(p.P1, brush);
        AddEndpointDot(p.P2, brush);

        if (dipLen < 5)
        {
            var shortPixelDx = dx * p.DpiX;
            var shortPixelDy = dy * p.DpiY;
            var shortPixelLen = Math.Sqrt(shortPixelDx * shortPixelDx + shortPixelDy * shortPixelDy);
            AddLabel(p, shortPixelLen, brush);
            return;
        }

        // Unit tangent and perpendicular vectors in DIP space
        var tx = dx / dipLen;
        var ty = dy / dipLen;
        var px = -ty; // perpendicular
        var py = tx;

        // Pixel distance
        var pixelDx = dx * p.DpiX;
        var pixelDy = dy * p.DpiY;
        var pixelLen = Math.Sqrt(pixelDx * pixelDx + pixelDy * pixelDy);

        // Adaptive tick interval: aim for ~8 major ticks
        double tickPx = 50;
        var estimatedTicks = pixelLen / tickPx;
        if (estimatedTicks < 3)
        {
            tickPx = 25;
        }
        else if (estimatedTicks > 20)
        {
            tickPx = 100;
        }

        // Convert tick pixel spacing to DIP
        var tickDip = tickPx / Math.Max(p.DpiX, p.DpiY);
        var tickCount = (int)(dipLen / tickDip);

        var majorTickHalf = 5.0;
        var minorTickHalf = 3.0;

        for (var i = 1; i < tickCount; i++)
        {
            var t = (i * tickDip) / dipLen;
            var cx2 = p.P1.X + dx * t;
            var cy2 = p.P1.Y + dy * t;
            var isMajor = i % 2 == 0;
            var half = isMajor ? majorTickHalf : minorTickHalf;

            var tick = new Line
            {
                X1 = cx2 - px * half,
                Y1 = cy2 - py * half,
                X2 = cx2 + px * half,
                Y2 = cy2 + py * half,
                Stroke = brush,
                StrokeThickness = p.Thickness * 0.7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            _container.Children.Add(tick);
        }

        AddLabel(p, pixelLen, brush);
    }

    private void AddEndpointDot(Point center, SolidColorBrush brush)
    {
        const double R = 2.5;
        var dot = new Ellipse
        {
            Width = R * 2,
            Height = R * 2,
            Fill = brush,
        };
        Canvas.SetLeft(dot, center.X - R);
        Canvas.SetTop(dot, center.Y - R);
        _container!.Children.Add(dot);
    }

    private void AddLabel(PixelRulerShapeParameters p, double pixelLen, SolidColorBrush brush)
    {
        var midX = (p.P1.X + p.P2.X) / 2;
        var midY = (p.P1.Y + p.P2.Y) / 2;

        // Perpendicular offset for label (12 DIPs away from the line)
        var dx = p.P2.X - p.P1.X;
        var dy = p.P2.Y - p.P1.Y;
        var dipLen = Math.Sqrt(dx * dx + dy * dy);
        var px = dipLen > 0 ? -dy / dipLen : 0;
        var py = dipLen > 0 ? dx / dipLen : -1;

        const double LabelOffset = 14;
        var lx = midX + px * LabelOffset;
        var ly = midY + py * LabelOffset;

        var label = new TextBlock
        {
            Text = $"{pixelLen:F0} px",
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
        };

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 30, 30, 30)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 2, 4, 2),
            Child = label,
        };

        border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(border, lx - border.DesiredSize.Width / 2);
        Canvas.SetTop(border, ly - border.DesiredSize.Height / 2);
        _container!.Children.Add(border);
    }
}
