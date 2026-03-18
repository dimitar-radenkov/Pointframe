using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using SnippingTool.Models;
using SnippingTool.Services.Handlers;
using SnippingTool.ViewModels;
using Point = System.Windows.Point;

namespace SnippingTool.Services;

internal sealed class AnnotationCanvasRenderer
{
    private readonly Canvas _canvas;
    private readonly AnnotationViewModel _vm;
    private readonly Action<UIElement> _onAdd;
    private readonly ILogger<AnnotationCanvasRenderer> _logger;
    private readonly Dictionary<AnnotationTool, IAnnotationShapeHandler> _handlers;

    private IAnnotationShapeHandler? _activeHandler;

    private BitmapSource? _backgroundCapture;
    private double _dpiX = 1.0;
    private double _dpiY = 1.0;

    public BitmapSource? BackgroundCapture => _backgroundCapture;

    public void SetBackground(BitmapSource background, double dpiX, double dpiY)
    {
        _backgroundCapture = background;
        _dpiX = dpiX;
        _dpiY = dpiY;
    }

    public AnnotationCanvasRenderer(
        Canvas canvas,
        AnnotationViewModel vm,
        Action<UIElement> onAdd,
        ILogger<AnnotationCanvasRenderer> logger)
    {
        _canvas = canvas;
        _vm = vm;
        _onAdd = onAdd;
        _logger = logger;

        _handlers = new Dictionary<AnnotationTool, IAnnotationShapeHandler>
        {
            [AnnotationTool.Arrow] = new ArrowShapeHandler(GetShapeParameters),
            [AnnotationTool.Rectangle] = new RectShapeHandler(GetShapeParameters),
            [AnnotationTool.Text] = new TextShapeHandler(),
            [AnnotationTool.Highlight] = new HighlightShapeHandler(GetShapeParameters),
            [AnnotationTool.Pen] = new PenShapeHandler(GetShapeParameters),
            [AnnotationTool.Line] = new LineShapeHandler(GetShapeParameters),
            [AnnotationTool.Circle] = new EllipseShapeHandler(GetShapeParameters),
            [AnnotationTool.Number] = new NumberShapeHandler(_vm.IncrementNumberCounter),
            [AnnotationTool.Blur] = new BlurShapeHandler(GetShapeParameters, () => _backgroundCapture, () => _dpiX, () => _dpiY),
            [AnnotationTool.Callout] = new CalloutShapeHandler(GetShapeParameters)
        };
    }

    private SolidColorBrush ActiveBrush() => new(_vm.ActiveColor);
    private ShapeParameters? GetShapeParameters() => _vm.TryGetShapeParameters();

    public void BeginShape(Point p)
    {
        _logger.LogDebug("Shape begin: {Tool}", _vm.SelectedTool);
        if (!_handlers.TryGetValue(_vm.SelectedTool, out var handler))
        {
            return;
        }

        _activeHandler = handler;
        handler.Begin(p, ActiveBrush(), _vm.StrokeThickness, _canvas);
    }

    public void UpdateShape(Point p)
    {
        if (_activeHandler is null)
        {
            return;
        }

        _activeHandler.Update(p);
    }

    public void CommitShape(Point p)
    {
        _logger.LogDebug("Shape committed: {Tool}", _vm.SelectedTool);
        if (_activeHandler is null)
        {
            return;
        }

        _activeHandler.Update(p);
        _activeHandler.Commit(_canvas, _onAdd);
        _activeHandler = null;
    }

    public void CancelShape()
    {
        _activeHandler?.Cancel(_canvas);
        _activeHandler = null;
    }
}
