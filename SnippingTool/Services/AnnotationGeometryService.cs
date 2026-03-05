using Point = System.Windows.Point;

namespace SnippingTool.Services;

public sealed class AnnotationGeometryService : IAnnotationGeometryService
{
    private const double HeadLength = 14.0;
    private const double HeadAngle = 25.0 * Math.PI / 180.0;

    public Point[] CalculateArrowHead(Point p1, Point p2)
    {
        var theta = Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);
        return
        [
            new Point(p2.X - HeadLength * Math.Cos(theta - HeadAngle), p2.Y - HeadLength * Math.Sin(theta - HeadAngle)),
            p2,
            new Point(p2.X - HeadLength * Math.Cos(theta + HeadAngle), p2.Y - HeadLength * Math.Sin(theta + HeadAngle))
        ];
    }

    public (double left, double top, double width, double height) CalculateRect(Point start, Point end)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var width = Math.Abs(end.X - start.X);
        var height = Math.Abs(end.Y - start.Y);
        return (left, top, width, height);
    }

    public (double left, double top, double width, double height) CalculateEllipse(Point start, Point end)
        => CalculateRect(start, end);

    public bool IsValidShapeSize(
        Point start,
        Point end,
        double minSide = 4.0)
    {
        var width = Math.Abs(end.X - start.X);
        var height = Math.Abs(end.Y - start.Y);
        return Math.Max(width, height) >= minSide;
    }
}
