using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SnippingTool.Models;
using SnippingTool.Services;
using SnippingTool.Services.Messaging;

namespace SnippingTool.ViewModels;

public partial class AnnotationViewModel : ObservableObject
{
    private readonly IEventAggregator _eventAggregator;
    private readonly IAnnotationGeometryService _geometry;
    protected readonly ILogger _logger;

    public AnnotationViewModel(
        IAnnotationGeometryService geometry,
        ILogger logger,
        IUserSettingsService settings,
        IEventAggregator eventAggregator)
    {
        _eventAggregator = eventAggregator;
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

            AnnotationTool.Rectangle => BuildRectParams(DragStart, DragCurrent, color, thick),

            AnnotationTool.Highlight => BuildHighlightParams(DragStart, DragCurrent, color),

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

    private RectShapeParameters BuildRectParams(Point start, Point end, Color color, double thick)
    {
        var (left, top, width, height) = _geometry.CalculateRect(start, end);
        return new RectShapeParameters(left, top, width, height, color, thick);
    }

    private HighlightShapeParameters BuildHighlightParams(Point start, Point end, Color baseColor)
    {
        var (left, top, width, height) = _geometry.CalculateRect(start, end);
        return new HighlightShapeParameters(left, top, width, height, baseColor);
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

    internal void ReplaceTrackedElement(object originalElement, object replacementElement)
    {
        ArgumentNullException.ThrowIfNull(originalElement);
        ArgumentNullException.ThrowIfNull(replacementElement);

        if (ReferenceEquals(originalElement, replacementElement))
        {
            return;
        }

        ReplaceTrackedElement(_currentGroup, originalElement, replacementElement);
        ReplaceTrackedElement(_undoStack, originalElement, replacementElement);
        ReplaceTrackedElement(_redoStack, originalElement, replacementElement);
    }

    internal void RemoveTrackedElement(object element)
    {
        ArgumentNullException.ThrowIfNull(element);

        RemoveTrackedElement(_currentGroup, element);
        RemoveTrackedElement(_undoStack, element);
        RemoveTrackedElement(_redoStack, element);
        UndoCount = _undoStack.Count;
        RedoCount = _redoStack.Count;
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undoStack.Count == 0)
        {
            _logger.LogDebug("Undo requested with empty stack");
            return;
        }

        var group = _undoStack[^1];
        PublishSync(new UndoGroupMessage(group));
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _redoStack.Add(group);
        UndoCount = _undoStack.Count;
        RedoCount = _redoStack.Count;
        _logger.LogDebug("Undo applied: undoStack={UndoCount}, redoStack={RedoCount}", UndoCount, RedoCount);
    }

    private bool CanUndo() => _undoStack.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_redoStack.Count == 0)
        {
            _logger.LogDebug("Redo requested with empty stack");
            return;
        }

        var group = _redoStack[^1];
        PublishSync(new RedoGroupMessage(group));
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _undoStack.Add(group);
        UndoCount = _undoStack.Count;
        RedoCount = _redoStack.Count;
        _logger.LogDebug("Redo applied: undoStack={UndoCount}, redoStack={RedoCount}", UndoCount, RedoCount);
    }

    private bool CanRedo() => _redoStack.Count > 0;

    private static void ReplaceTrackedElement(List<object>? group, object originalElement, object replacementElement)
    {
        if (group is null)
        {
            return;
        }

        for (var i = 0; i < group.Count; i++)
        {
            if (ReferenceEquals(group[i], originalElement))
            {
                group[i] = replacementElement;
            }
        }
    }

    private static void ReplaceTrackedElement(List<List<object>> stack, object originalElement, object replacementElement)
    {
        foreach (var group in stack)
        {
            ReplaceTrackedElement(group, originalElement, replacementElement);
        }
    }

    private static void RemoveTrackedElement(List<object>? group, object element)
    {
        group?.RemoveAll(candidate => ReferenceEquals(candidate, element));
    }

    private static void RemoveTrackedElement(List<List<object>> stack, object element)
    {
        for (var i = stack.Count - 1; i >= 0; i--)
        {
            stack[i].RemoveAll(candidate => ReferenceEquals(candidate, element));
            if (stack[i].Count == 0)
            {
                stack.RemoveAt(i);
            }
        }
    }

    protected void ClearHistoryState()
    {
        _currentGroup = null;
        _undoStack.Clear();
        _redoStack.Clear();
        UndoCount = 0;
        RedoCount = 0;
    }

    private void PublishSync(object message)
    {
        var publishTask = _eventAggregator.PublishAsync(message);
        if (!publishTask.IsCompletedSuccessfully)
        {
            publishTask.AsTask().GetAwaiter().GetResult();
        }
    }
}
