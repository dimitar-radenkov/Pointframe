using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SnippingTool.Models;
using SnippingTool.Services;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace SnippingTool.ViewModels;

public partial class AnnotationViewModel : ObservableObject
{
    private readonly IAnnotationGeometryService _geometry;

    public AnnotationViewModel(IAnnotationGeometryService geometry)
    {
        _geometry = geometry;
    }

    [ObservableProperty]
    private AnnotationTool _selectedTool = AnnotationTool.Rectangle;

    [ObservableProperty]
    private Color _activeColor = Colors.Red;

    [ObservableProperty]
    private double _strokeThickness = 2.5;

    public SolidColorBrush ActiveBrush => new(ActiveColor);

    partial void OnActiveColorChanged(Color value) => OnPropertyChanged(nameof(ActiveBrush));

    public bool IsDragging { get; private set; }
    public Point DragStart { get; private set; }
    public Point DragCurrent { get; private set; }

    public void BeginDrawing(Point pt)
    {
        DragStart = pt;
        DragCurrent = pt;
        IsDragging = true;
    }

    public void UpdateDrawing(Point pt)
    {
        DragCurrent = pt;
    }

    public void CommitDrawing()
    {
        IsDragging = false;
    }

    public void CancelDrawing()
    {
        IsDragging = false;
    }

    public int NumberCounter { get; private set; }

    public int IncrementNumberCounter()
    {
        NumberCounter++;
        return NumberCounter;
    }

    public void ResetNumberCounter(int value)
    {
        NumberCounter = value;
    }

    public void SetColorFromTag(string? tag)
    {
        ActiveColor = tag switch
        {
            "Red" => Colors.Red,
            "Blue" => Colors.DodgerBlue,
            "Black" => Color.FromRgb(0x1A, 0x1A, 0x1A),
            "Green" => Color.FromRgb(0x22, 0xA4, 0x22),
            "Orange" => Colors.Orange,
            "Purple" => Color.FromRgb(0x8B, 0x2B, 0xE2),
            "White" => Colors.White,
            "Pink" => Colors.HotPink,
            _ => Colors.Red
        };
    }

    public void SetStrokeThicknessFromText(string? text)
    {
        if (double.TryParse(text, out var t))
        {
            StrokeThickness = t;
        }
    }

    public ShapeParameters? TryGetShapeParameters()
    {
        if (!_geometry.IsValidShapeSize(DragStart, DragCurrent))
        {
            return null;
        }

        var color = ActiveColor;
        var thick = StrokeThickness;

        return SelectedTool switch
        {
            AnnotationTool.Arrow => new ArrowShapeParameters(
                P1: DragStart,
                P2: DragCurrent,
                Color: color,
                Thickness: thick,
                ArrowHead: _geometry.CalculateArrowHead(DragStart, DragCurrent)),

            AnnotationTool.Rectangle => BuildRectParams(DragStart, DragCurrent, color, thick, isHighlight: false),

            AnnotationTool.Highlight => BuildRectParams(DragStart, DragCurrent, color, thick, isHighlight: true),

            AnnotationTool.Circle => BuildEllipseParams(DragStart, DragCurrent, color, thick),

            AnnotationTool.Line => new LineShapeParameters(
                P1: DragStart,
                P2: DragCurrent,
                Color: color,
                Thickness: thick),

            AnnotationTool.Pen => new PenShapeParameters(
                StartPoint: DragStart,
                Color: color,
                Thickness: thick),

            _ => null
        };
    }

    private RectShapeParameters BuildRectParams(Point start, Point end, Color color, double thick, bool isHighlight)
    {
        var (left, top, width, height) = _geometry.CalculateRect(start, end);
        return new RectShapeParameters(left, top, width, height, color, thick, isHighlight);
    }

    private EllipseShapeParameters BuildEllipseParams(Point start, Point end, Color color, double thick)
    {
        var (left, top, width, height) = _geometry.CalculateEllipse(start, end);
        return new EllipseShapeParameters(left, top, width, height, color, thick);
    }
}
