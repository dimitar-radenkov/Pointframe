using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace SnippingTool.Models;

public abstract record ShapeParameters;

public sealed record ArrowShapeParameters(
    Point P1,
    Point P2,
    Color Color,
    double Thickness,
    Point[] ArrowHead) : ShapeParameters;

public sealed record RectShapeParameters(
    double Left,
    double Top,
    double Width,
    double Height,
    Color Color,
    double Thickness,
    bool IsHighlight) : ShapeParameters;

public sealed record EllipseShapeParameters(
    double Left,
    double Top,
    double Width,
    double Height,
    Color Color,
    double Thickness) : ShapeParameters;

public sealed record LineShapeParameters(
    Point P1,
    Point P2,
    Color Color,
    double Thickness) : ShapeParameters;

public sealed record PenShapeParameters(
    Point StartPoint,
    Color Color,
    double Thickness) : ShapeParameters;
