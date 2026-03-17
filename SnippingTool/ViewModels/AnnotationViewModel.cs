using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SnippingTool.Models;
using SnippingTool.Services;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace SnippingTool.ViewModels;

public partial class AnnotationViewModel : ObservableObject
{
    private readonly IAnnotationGeometryService _geometry;
    protected readonly ILogger _logger;

    public AnnotationViewModel(
        IAnnotationGeometryService geometry,
        ILogger logger,
        IUserSettingsService settings)
    {
        _geometry = geometry;
        _logger = logger;

        try
        {
            _activeColor = (Color)System.Windows.Media.ColorConverter.ConvertFromString(settings.Current.DefaultAnnotationColor);
        }
        catch
        {
            _activeColor = Colors.Red;
        }

        _strokeThickness = settings.Current.DefaultStrokeThickness;
    }

    [ObservableProperty]
    private AnnotationTool _selectedTool = AnnotationTool.Rectangle;

    [ObservableProperty]
    private Color _activeColor;

    [ObservableProperty]
    private double _strokeThickness;

    public SolidColorBrush ActiveBrush => new(ActiveColor);

    partial void OnSelectedToolChanged(AnnotationTool value) =>
        _logger.LogDebug("Tool selected: {Tool}", value);

    partial void OnActiveColorChanged(Color value)
    {
        OnPropertyChanged(nameof(ActiveBrush));
        _logger.LogDebug("Color changed: {Color}", value);
    }

    public bool IsDragging { get; private set; }
    public Point DragStart { get; private set; }
    public Point DragCurrent { get; private set; }

    public void BeginDrawing(Point pt)
    {
        DragStart = pt;
        DragCurrent = pt;
        IsDragging = true;
        _logger.LogDebug("Drawing begun at ({X:F1},{Y:F1}) with {Tool}", pt.X, pt.Y, SelectedTool);
    }

    public void UpdateDrawing(Point pt)
    {
        DragCurrent = pt;
    }

    public void CommitDrawing()
    {
        IsDragging = false;
        _logger.LogDebug("Drawing committed");
    }

    public void CancelDrawing()
    {
        IsDragging = false;
        _logger.LogDebug("Drawing cancelled");
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

            AnnotationTool.Blur => BuildBlurParams(DragStart, DragCurrent),

            AnnotationTool.Callout => BuildCalloutParams(DragStart, DragCurrent, color, thick),

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

    private BlurShapeParameters BuildBlurParams(Point start, Point end)
    {
        var (left, top, width, height) = _geometry.CalculateRect(start, end);
        return new BlurShapeParameters(left, top, width, height);
    }

    private CalloutShapeParameters BuildCalloutParams(Point start, Point end, Color stroke, double thick)
    {
        var (left, top, width, height) = _geometry.CalculateRect(start, end);
        var tail = new Point(left - width * 0.15, top + height + height * 0.25);
        return new CalloutShapeParameters(left, top, width, height, tail, string.Empty, Colors.White, stroke, thick);
    }

    private readonly List<List<object>> _undoStack = [];
    private readonly List<List<object>> _redoStack = [];
    private List<object>? _currentGroup;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private int _undoCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedoCommand))]
    private int _redoCount;

    public void BeginGroup() => _currentGroup = [];

    public void CommitGroup()
    {
        if (_currentGroup is { Count: > 0 })
        {
            _undoStack.Add(_currentGroup);
            _redoStack.Clear();
            UndoCount = _undoStack.Count;
            RedoCount = 0;
        }

        _currentGroup = null;
    }

    public void TrackElement(object element) => _currentGroup?.Add(element);

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        var group = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _redoStack.Add(group);
        UndoCount = _undoStack.Count;
        RedoCount = _redoStack.Count;
        _logger.LogDebug("Undo applied: undoStack={UndoCount}, redoStack={RedoCount}", UndoCount, RedoCount);
        UndoApplied?.Invoke(group);
    }

    private bool CanUndo() => _undoStack.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        var group = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _undoStack.Add(group);
        UndoCount = _undoStack.Count;
        RedoCount = _redoStack.Count;
        _logger.LogDebug("Redo applied: undoStack={UndoCount}, redoStack={RedoCount}", UndoCount, RedoCount);
        RedoApplied?.Invoke(group);
    }

    private bool CanRedo() => _redoStack.Count > 0;

    public event Action<List<object>>? UndoApplied;
    public event Action<List<object>>? RedoApplied;
}
