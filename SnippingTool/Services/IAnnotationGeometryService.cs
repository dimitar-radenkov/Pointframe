namespace SnippingTool.Services;

public interface IAnnotationGeometryService
{
    Point[] CalculateArrowHead(Point p1, Point p2);
    (double left, double top, double width, double height) CalculateRect(Point start, Point end);
    (double left, double top, double width, double height) CalculateEllipse(Point start, Point end);
    bool IsValidShapeSize(
        Point start,
        Point end,
        double minSide = 4.0);
}
