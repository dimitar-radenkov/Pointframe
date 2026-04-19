namespace Pointframe.Models;

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
    double Thickness) : ShapeParameters;

public sealed record HighlightShapeParameters(
    double Left,
    double Top,
    double Width,
    double Height,
    Color BaseColor) : ShapeParameters;

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

public sealed record BlurShapeParameters(
    double Left,
    double Top,
    double Width,
    double Height) : ShapeParameters;

public sealed record CalloutShapeParameters(
    double Left,
    double Top,
    double Width,
    double Height,
    Point Tail,
    string Text,
    Color Fill,
    Color Stroke,
    double Thickness) : ShapeParameters;
