using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Point = System.Windows.Point;

namespace SnippingTool.Services.Handlers;

internal sealed class NumberShapeHandler : IAnnotationShapeHandler
{
    private readonly Func<int> _getNextNumber;

    private Grid? _badge;

    public NumberShapeHandler(Func<int> getNextNumber)
    {
        _getNextNumber = getNextNumber;
    }

    public void Begin(Point point, SolidColorBrush brush, double thickness, Canvas canvas)
    {
        const double BadgeSize = 28;
        var number = _getNextNumber();
        _badge = new Grid
        {
            Width = BadgeSize,
            Height = BadgeSize,
            Tag = "number"
        };
        _badge.Children.Add(new Ellipse { Fill = brush });
        _badge.Children.Add(new TextBlock
        {
            Text = number.ToString(),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        Canvas.SetLeft(_badge, point.X - BadgeSize / 2);
        Canvas.SetTop(_badge, point.Y - BadgeSize / 2);
        canvas.Children.Add(_badge);
    }

    public void Update(Point point)
    {
    }

    public void Commit(Canvas canvas, Action<UIElement> trackElement)
    {
        if (_badge is null)
        {
            return;
        }

        trackElement(_badge);
        _badge = null;
    }

    public void Cancel(Canvas canvas)
    {
        if (_badge is not null && canvas.Children.Contains(_badge))
        {
            canvas.Children.Remove(_badge);
        }

        _badge = null;
    }
}
